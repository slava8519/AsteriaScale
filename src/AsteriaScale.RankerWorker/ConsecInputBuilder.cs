using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AsteriaScale.RankerProtocol;

namespace AsteriaScale.RankerWorker;

internal sealed class ConsecInputBuilder
{
    internal const int MaximumSequenceLength = 512;
    private static readonly Regex ContextTokenPattern = new(
        @"[\p{L}\p{M}\p{N}]+(?:['\u2019\u02BC\-\u2010\u2011\u2012\u2013\u2014\u2015][\p{L}\p{M}\p{N}]+)*|[^\s]",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private readonly IConsecPieceTokenizer _tokenizer;

    internal ConsecInputBuilder(IConsecPieceTokenizer tokenizer)
    {
        _tokenizer = tokenizer ?? throw new ArgumentNullException(nameof(tokenizer));
    }

    internal ConsecInput BuildFromContext(
        string context,
        string target,
        int? characterIndex,
        IReadOnlyList<string> candidates)
    {
        ConsecResolvedContext resolved = ResolveContext(
            context,
            target,
            characterIndex);
        return BuildFromTokens(
            resolved.Tokens,
            resolved.TargetIndex,
            candidates,
            []);
    }

    internal ConsecInput BuildFromTokens(
        IReadOnlyList<string> sentence,
        int targetIndex,
        IReadOnlyList<string> candidates,
        IReadOnlyList<ConsecFeedbackDefinition> feedback)
    {
        ArgumentNullException.ThrowIfNull(sentence);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(feedback);
        if (sentence.Count == 0 ||
            sentence.Any(string.IsNullOrWhiteSpace))
        {
            throw new RankerException(
                "consec_invalid_input",
                "ConSeC requires a non-empty sentence with no empty tokens.");
        }
        if (targetIndex < 0 || targetIndex >= sentence.Count)
        {
            throw new RankerException(
                "consec_invalid_input",
                "The ConSeC target token index is outside the sentence.");
        }
        if (candidates.Count == 0 || candidates.Any(string.IsNullOrWhiteSpace))
        {
            throw new RankerException(
                "consec_invalid_input",
                "ConSeC requires at least one non-empty candidate definition.");
        }
        if (feedback.Any(item =>
                string.IsNullOrWhiteSpace(item.Definition) ||
                item.TokenIndex < 0 ||
                item.TokenIndex >= sentence.Count))
        {
            throw new RankerException(
                "consec_invalid_input",
                "A ConSeC feedback definition is empty or points outside the sentence.");
        }

        var sentenceIds = new List<long> { _tokenizer.ClassifierTokenId };
        var sentenceOffsets = new List<ConsecTokenOffset>(sentence.Count);
        for (int index = 0; index < sentence.Count; index++)
        {
            string token = index == targetIndex
                ? $"<d> {sentence[index]} </d>"
                : sentence[index];
            IReadOnlyList<long> piece = _tokenizer.EncodePiece(token);
            if (piece.Count == 0)
            {
                throw new RankerException(
                    "consec_tokenization_failed",
                    $"Sentence token {index} produced no ConSeC subword ids.");
            }

            sentenceOffsets.Add(new ConsecTokenOffset(
                sentenceIds.Count,
                sentenceIds.Count + piece.Count));
            sentenceIds.AddRange(piece);
        }
        sentenceIds.Add(_tokenizer.SeparatorTokenId);

        IReadOnlyList<long>[] candidateIds = candidates
            .Select(candidate => EncodeDefinition(candidate))
            .ToArray();
        IReadOnlyList<long>[] feedbackIds = feedback
            .Select(item => EncodeDefinition(item.Definition))
            .ToArray();
        IReadOnlyList<long>[] definitionBlocks =
            [.. candidateIds, .. feedbackIds];
        int totalDefinitionIds = definitionBlocks.Sum(piece => piece.Count);
        int sequenceLength;
        try
        {
            sequenceLength = checked(
                sentenceIds.Count + totalDefinitionIds + 1);
        }
        catch (OverflowException ex)
        {
            throw SequenceTooLong(int.MaxValue, ex);
        }
        if (sequenceLength > MaximumSequenceLength)
        {
            throw SequenceTooLong(sequenceLength);
        }

        var inputIds = new long[sequenceLength];
        int cursor = 0;
        sentenceIds.CopyTo(inputIds, cursor);
        cursor += sentenceIds.Count;
        var markerPositions = new int[candidateIds.Length];
        for (int index = 0; index < candidateIds.Length; index++)
        {
            markerPositions[index] = cursor;
            Copy(candidateIds[index], inputIds, ref cursor);
        }
        foreach (IReadOnlyList<long> piece in feedbackIds)
        {
            Copy(piece, inputIds, ref cursor);
        }
        inputIds[cursor++] = _tokenizer.SeparatorTokenId;
        if (cursor != sequenceLength)
        {
            throw new RankerException(
                "consec_invalid_input",
                "The ConSeC input builder produced an inconsistent sequence length.");
        }

        var attentionMask = new long[sequenceLength];
        Array.Fill(attentionMask, 1L);
        var tokenTypeIds = new long[sequenceLength];
        Array.Fill(
            tokenTypeIds,
            1L,
            sentenceIds.Count,
            sequenceLength - sentenceIds.Count);
        long[] relativePositions = BuildRelativePositions(
            sequenceLength,
            sentenceIds.Count,
            totalDefinitionIds,
            definitionBlocks,
            sentenceOffsets,
            targetIndex,
            feedback);

        return new ConsecInput(
            inputIds,
            attentionMask,
            tokenTypeIds,
            relativePositions,
            markerPositions,
            sequenceLength);
    }

    internal static ConsecResolvedContext ResolveContext(
        string context,
        string target,
        int? characterIndex)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(target);
        int anchor = Math.Clamp(characterIndex ?? 0, 0, context.Length);
        int targetStart = RankerTargetAnchor.FindClosestOccurrence(
            context,
            target,
            anchor);
        if (targetStart < 0)
        {
            throw new RankerException(
                "target_not_in_context",
                "The selected target does not occur in the source context.");
        }

        var tokens = new List<string>();
        AppendContextTokens(context.AsSpan(0, targetStart), tokens);
        int targetIndex = tokens.Count;
        tokens.Add(context.Substring(targetStart, target.Length));
        AppendContextTokens(
            context.AsSpan(targetStart + target.Length),
            tokens);
        return new ConsecResolvedContext(tokens, targetIndex);
    }

    internal static string ComputeTensorBundleSha256(ConsecInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        using IncrementalHash digest = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        AppendTensor(
            digest,
            "input_ids",
            input.InputIds,
            $"[1,{input.SequenceLength}]");
        AppendTensor(
            digest,
            "attention_mask",
            input.AttentionMask,
            $"[1,{input.SequenceLength}]");
        AppendTensor(
            digest,
            "token_type_ids",
            input.TokenTypeIds,
            $"[1,{input.SequenceLength}]");
        AppendTensor(
            digest,
            "relative_pos",
            input.RelativePositions,
            $"[1,{input.SequenceLength},{input.SequenceLength}]");
        return Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
    }

    private IReadOnlyList<long> EncodeDefinition(string definition)
    {
        string refined = RefineDefinition(definition);
        IReadOnlyList<long> ids = _tokenizer.EncodePiece(refined);
        if (ids.Count == 0 || ids[0] != _tokenizer.DefinitionMarkerTokenId)
        {
            throw new RankerException(
                "consec_tokenizer_incompatible",
                "A refined definition did not begin with the pinned DEF_SEP token.");
        }

        return ids;
    }

    private static string RefineDefinition(string value)
    {
        ValidatePinnedEnglishCasingDomain(value);
        char[] capitalized = value.ToCharArray();
        capitalized[0] = ToUpperAscii(capitalized[0]);
        for (int index = 1; index < capitalized.Length; index++)
        {
            capitalized[index] = ToLowerAscii(capitalized[index]);
        }

        // The order is material. Pinned upstream applies capitalize() first
        // and only then strips literal full stops; it does not trim whitespace.
        string clean = new string(capitalized).Trim('.');
        if (clean.Length == 0)
        {
            throw new RankerException(
                "consec_invalid_input",
                "A ConSeC candidate definition contains only punctuation.");
        }

        return "DEF_SEP " + clean + ".";
    }

    private static void ValidatePinnedEnglishCasingDomain(string value)
    {
        bool firstRune = true;
        foreach (Rune rune in value.EnumerateRunes())
        {
            if (rune.Value <= 0x7f)
            {
                firstRune = false;
                continue;
            }

            UnicodeCategory category = Rune.GetUnicodeCategory(rune);
            bool cased = category is UnicodeCategory.UppercaseLetter or
                UnicodeCategory.LowercaseLetter or
                UnicodeCategory.TitlecaseLetter;
            bool alreadyLowercaseTail = !firstRune &&
                category == UnicodeCategory.LowercaseLetter;
            if (cased && !alreadyLowercaseTail)
            {
                // Python ships its own Unicode casing tables and permits
                // one-to-many/titlecase mappings (for example ß -> Ss and
                // ǳ -> ǲ). Lowercase tail letters need no transformation and
                // are safe to preserve; every non-ASCII mapping still fails
                // closed rather than approximating Python 3.9 with .NET.
                throw new RankerException(
                    "consec_definition_casing_unsupported",
                    "A ConSeC English definition requires a non-ASCII case " +
                    "mapping. Exact Python 3.9 capitalize semantics cannot " +
                    "be guaranteed for that input.");
            }

            firstRune = false;
        }
    }

    private static char ToUpperAscii(char value) =>
        value is >= 'a' and <= 'z' ? (char)(value - ('a' - 'A')) : value;

    private static char ToLowerAscii(char value) =>
        value is >= 'A' and <= 'Z' ? (char)(value + ('a' - 'A')) : value;

    private static long[] BuildRelativePositions(
        int sequenceLength,
        int sentenceLength,
        int totalDefinitionIds,
        IReadOnlyList<IReadOnlyList<long>> definitionBlocks,
        IReadOnlyList<ConsecTokenOffset> sentenceOffsets,
        int targetIndex,
        IReadOnlyList<ConsecFeedbackDefinition> feedback)
    {
        var relative = new long[checked(sequenceLength * sequenceLength)];
        for (int sentencePosition = 0;
             sentencePosition < sentenceLength;
             sentencePosition++)
        {
            for (int otherPosition = 0;
                 otherPosition < sentenceLength;
                 otherPosition++)
            {
                Set(
                    relative,
                    sequenceLength,
                    sentencePosition,
                    otherPosition,
                    sentencePosition - otherPosition);
            }

            int definitionColumn = sentenceLength;
            foreach (IReadOnlyList<long> definition in definitionBlocks)
            {
                for (int definitionOffset = 0;
                     definitionOffset < definition.Count;
                     definitionOffset++)
                {
                    Set(
                        relative,
                        sequenceLength,
                        sentencePosition,
                        definitionColumn++,
                        sentencePosition - sentenceLength - definitionOffset);
                }
            }
        }

        // This assignment order is material: it mirrors the exact upstream
        // `relative_positions.T[:sentence] = -relative_positions[:sentence]`
        // operation before target-specific links overwrite selected cells.
        for (int row = 0; row < sequenceLength; row++)
        {
            for (int column = 0; column < sentenceLength; column++)
            {
                Set(
                    relative,
                    sequenceLength,
                    row,
                    column,
                    -Get(relative, sequenceLength, column, row));
            }
        }

        int candidateCount = definitionBlocks.Count - feedback.Count;
        int currentOffset = sentenceLength;
        for (int definitionIndex = 0;
             definitionIndex < definitionBlocks.Count;
             definitionIndex++)
        {
            IReadOnlyList<long> definition = definitionBlocks[definitionIndex];
            int linkedToken = definitionIndex < candidateCount
                ? targetIndex
                : feedback[definitionIndex - candidateCount].TokenIndex;
            bool symmetric = definitionIndex >= candidateCount;
            ConsecTokenOffset linked = sentenceOffsets[linkedToken];
            int linkedWidth = linked.End - linked.Start;
            for (int definitionOffset = 0;
                 definitionOffset < definition.Count;
                 definitionOffset++)
            {
                int definitionPosition = currentOffset + definitionOffset;
                for (int linkedOffset = 0;
                     linkedOffset < linkedWidth;
                     linkedOffset++)
                {
                    int linkedPosition = linked.Start + linkedOffset;
                    long distance = definitionOffset + linkedWidth - linkedOffset;
                    Set(
                        relative,
                        sequenceLength,
                        definitionPosition,
                        linkedPosition,
                        distance);
                    if (symmetric)
                    {
                        Set(
                            relative,
                            sequenceLength,
                            linkedPosition,
                            definitionPosition,
                            -distance);
                    }
                }

                long first = currentOffset + definitionOffset - sentenceLength;
                for (int definitionColumn = 0;
                     definitionColumn < totalDefinitionIds;
                     definitionColumn++)
                {
                    Set(
                        relative,
                        sequenceLength,
                        definitionPosition,
                        sentenceLength + definitionColumn,
                        first - definitionColumn);
                }
            }

            currentOffset += definition.Count;
        }

        var lastTokenPositions = new long[sequenceLength];
        for (int row = 0; row < sequenceLength; row++)
        {
            long minimum = long.MaxValue;
            int rowOffset = row * sequenceLength;
            for (int column = 0; column < sequenceLength; column++)
            {
                minimum = Math.Min(minimum, relative[rowOffset + column]);
            }
            lastTokenPositions[row] = minimum - 1;
        }
        for (int column = 0; column < sequenceLength; column++)
        {
            Set(
                relative,
                sequenceLength,
                sequenceLength - 1,
                column,
                -lastTokenPositions[column]);
        }
        for (int row = 0; row < sequenceLength; row++)
        {
            Set(
                relative,
                sequenceLength,
                row,
                sequenceLength - 1,
                lastTokenPositions[row]);
        }
        Set(
            relative,
            sequenceLength,
            sequenceLength - 1,
            sequenceLength - 1,
            0);
        return relative;
    }

    private static void AppendContextTokens(
        ReadOnlySpan<char> text,
        ICollection<string> destination)
    {
        foreach (Match match in ContextTokenPattern.Matches(text.ToString()))
        {
            destination.Add(match.Value);
        }
    }

    private static void Copy(
        IReadOnlyList<long> source,
        long[] destination,
        ref int cursor)
    {
        for (int index = 0; index < source.Count; index++)
        {
            destination[cursor++] = source[index];
        }
    }

    private static void AppendTensor(
        IncrementalHash digest,
        string name,
        long[] values,
        string shape)
    {
        digest.AppendData(Encoding.ASCII.GetBytes(name));
        digest.AppendData("torch.int64"u8);
        digest.AppendData(Encoding.ASCII.GetBytes(shape));
        if (BitConverter.IsLittleEndian)
        {
            digest.AppendData(MemoryMarshal.AsBytes(values.AsSpan()));
            return;
        }

        Span<byte> buffer = stackalloc byte[sizeof(long)];
        foreach (long value in values)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(
                buffer,
                value);
            digest.AppendData(buffer);
        }
    }

    private static long Get(
        IReadOnlyList<long> matrix,
        int width,
        int row,
        int column) => matrix[(row * width) + column];

    private static void Set(
        IList<long> matrix,
        int width,
        int row,
        int column,
        long value) => matrix[(row * width) + column] = value;

    private static RankerException SequenceTooLong(
        int observedLength,
        Exception? inner = null) => new(
        "consec_sequence_too_long",
        $"ConSeC requires the complete sentence and all definitions, but the " +
        $"request needs {observedLength} tokens; the pinned maximum is " +
        $"{MaximumSequenceLength}. No truncation was applied.",
        inner);
}

internal sealed record ConsecInput(
    long[] InputIds,
    long[] AttentionMask,
    long[] TokenTypeIds,
    long[] RelativePositions,
    int[] MarkerPositions,
    int SequenceLength);

internal sealed record ConsecFeedbackDefinition(
    string Definition,
    int TokenIndex);

internal sealed record ConsecResolvedContext(
    IReadOnlyList<string> Tokens,
    int TargetIndex);

internal readonly record struct ConsecTokenOffset(int Start, int End);
