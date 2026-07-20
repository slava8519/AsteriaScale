using System.Text.Json;
using Tokenizers.DotNet;

namespace AsteriaScale.RankerWorker;

internal sealed class PairTokenizer : IDisposable
{
    private readonly Tokenizer _tokenizer;
    private readonly uint _classifierTokenId;
    private readonly uint _separatorTokenId;
    private readonly uint _paddingTokenId;
    private readonly bool _usesRobertaPairLayout;

    public PairTokenizer(string tokenizerPath)
    {
        _tokenizer = new Tokenizer(tokenizerPath);
        var specialTokens = ReadSpecialTokens(tokenizerPath);
        _classifierTokenId = specialTokens.ClassifierTokenId;
        _separatorTokenId = specialTokens.SeparatorTokenId;
        _paddingTokenId = specialTokens.PaddingTokenId;
        _usesRobertaPairLayout = specialTokens.UsesRobertaPairLayout;
    }

    public EncodedBatch EncodePairs(
        string query,
        IReadOnlyList<string> candidates,
        int maximumSequenceLength,
        bool padToLongest = false)
    {
        if (maximumSequenceLength < 8)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumSequenceLength),
                maximumSequenceLength,
                "The sequence length must leave room for both texts and special tokens.");
        }

        var batchSize = candidates.Count;
        var queryTokens = StripSingleSequenceSpecialTokens(_tokenizer.Encode(query));
        var candidateTokens = candidates
            .Select(candidate =>
                StripSingleSequenceSpecialTokens(_tokenizer.Encode(candidate)))
            .ToArray();
        var sequenceLength = padToLongest
            ? CalculateLongestPairLength(
                queryTokens.Count,
                candidateTokens,
                maximumSequenceLength)
            : maximumSequenceLength;
        var inputIds = new long[batchSize * sequenceLength];
        var attentionMask = new long[inputIds.Length];
        var tokenTypeIds = new long[inputIds.Length];

        for (var batchIndex = 0; batchIndex < batchSize; batchIndex++)
        {
            var pair = ComposePair(
                queryTokens,
                candidateTokens[batchIndex],
                sequenceLength);
            var offset = batchIndex * sequenceLength;

            Array.Fill(inputIds, (long)_paddingTokenId, offset, sequenceLength);
            for (var tokenIndex = 0; tokenIndex < pair.InputIds.Count; tokenIndex++)
            {
                inputIds[offset + tokenIndex] = pair.InputIds[tokenIndex];
                attentionMask[offset + tokenIndex] = 1;
                tokenTypeIds[offset + tokenIndex] = pair.TokenTypeIds[tokenIndex];
            }
        }

        return new EncodedBatch(
            inputIds,
            attentionMask,
            tokenTypeIds,
            batchSize,
            sequenceLength);
    }

    private int CalculateLongestPairLength(
        int queryTokenCount,
        IReadOnlyList<IReadOnlyList<uint>> candidates,
        int maximumSequenceLength)
    {
        var specialTokenCount = _usesRobertaPairLayout ? 4 : 3;
        var longest = candidates.Count == 0
            ? specialTokenCount + queryTokenCount
            : candidates.Max(candidate =>
                specialTokenCount + queryTokenCount + candidate.Count);
        return Math.Clamp(longest, 8, maximumSequenceLength);
    }

    public void Dispose() => _tokenizer.Dispose();

    private PairEncoding ComposePair(
        IReadOnlyList<uint> query,
        IReadOnlyList<uint> candidate,
        int sequenceLength)
    {
        var specialTokenCount = _usesRobertaPairLayout ? 4 : 3;
        var available = sequenceLength - specialTokenCount;

        // Match tokenizer.json / Hugging Face LongestFirst truncation: remove
        // from the longer sequence, from the right, until the pair fits.
        var queryCount = query.Count;
        var candidateCount = candidate.Count;
        while (queryCount + candidateCount > available)
        {
            if (queryCount > candidateCount)
            {
                queryCount--;
            }
            else
            {
                candidateCount--;
            }
        }

        var ids = new List<long>(sequenceLength) { _classifierTokenId };
        var types = new List<long>(sequenceLength) { 0 };

        Append(ids, types, query, queryCount, tokenType: 0);
        ids.Add(_separatorTokenId);
        types.Add(0);

        if (_usesRobertaPairLayout)
        {
            ids.Add(_separatorTokenId);
            types.Add(0);
            Append(ids, types, candidate, candidateCount, tokenType: 0);
            ids.Add(_separatorTokenId);
            types.Add(0);
        }
        else
        {
            Append(ids, types, candidate, candidateCount, tokenType: 1);
            ids.Add(_separatorTokenId);
            types.Add(1);
        }

        return new PairEncoding(ids, types);
    }

    private static void Append(
        ICollection<long> ids,
        ICollection<long> tokenTypes,
        IReadOnlyList<uint> tokens,
        int count,
        long tokenType)
    {
        for (var i = 0; i < count; i++)
        {
            ids.Add(tokens[i]);
            tokenTypes.Add(tokenType);
        }
    }

    private IReadOnlyList<uint> StripSingleSequenceSpecialTokens(uint[] encoded)
    {
        var start = encoded.Length > 0 && encoded[0] == _classifierTokenId ? 1 : 0;
        var end = encoded.Length;
        while (end > start && encoded[end - 1] == _separatorTokenId)
        {
            end--;
        }

        if (start == 0 && end == encoded.Length)
        {
            return encoded;
        }

        return encoded.AsSpan(start, end - start).ToArray();
    }

    private static SpecialTokens ReadSpecialTokens(string tokenizerPath)
    {
        using var stream = File.OpenRead(tokenizerPath);
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        var vocabulary = ReadVocabulary(root);

        var classifier = FindToken(vocabulary, "[CLS]", "<s>");
        var separator = FindToken(vocabulary, "[SEP]", "</s>");
        var padding = FindToken(vocabulary, "[PAD]", "<pad>");

        if (classifier is null || separator is null || padding is null)
        {
            throw new RankerException(
                "unsupported_tokenizer",
                "tokenizer.json must expose [CLS]/[SEP]/[PAD] or <s>/</s>/<pad> special tokens.");
        }

        var postProcessorType = string.Empty;
        if (root.TryGetProperty("post_processor", out var postProcessor) &&
            postProcessor.ValueKind == JsonValueKind.Object &&
            postProcessor.TryGetProperty("type", out var typeElement))
        {
            postProcessorType = typeElement.GetString() ?? string.Empty;
        }

        var usesRobertaLayout =
            string.Equals(classifier.Value.Token, "<s>", StringComparison.Ordinal) ||
            postProcessorType.Contains("Roberta", StringComparison.OrdinalIgnoreCase);

        return new SpecialTokens(
            classifier.Value.Id,
            separator.Value.Id,
            padding.Value.Id,
            usesRobertaLayout);
    }

    private static Dictionary<string, uint> ReadVocabulary(JsonElement root)
    {
        var vocabulary = new Dictionary<string, uint>(StringComparer.Ordinal);
        if (root.TryGetProperty("model", out var model) &&
            model.ValueKind == JsonValueKind.Object &&
            model.TryGetProperty("vocab", out var vocab))
        {
            if (vocab.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in vocab.EnumerateObject())
                {
                    if (property.Value.TryGetUInt32(out var id))
                    {
                        vocabulary[property.Name] = id;
                    }
                }
            }
            else if (vocab.ValueKind == JsonValueKind.Array)
            {
                uint index = 0;
                foreach (var entry in vocab.EnumerateArray())
                {
                    if (entry.ValueKind == JsonValueKind.Array &&
                        entry.GetArrayLength() > 0 &&
                        entry[0].ValueKind == JsonValueKind.String)
                    {
                        vocabulary[entry[0].GetString()!] = index;
                    }
                    else if (entry.ValueKind == JsonValueKind.String)
                    {
                        vocabulary[entry.GetString()!] = index;
                    }

                    index++;
                }
            }
        }

        if (root.TryGetProperty("added_tokens", out var addedTokens) &&
            addedTokens.ValueKind == JsonValueKind.Array)
        {
            foreach (var token in addedTokens.EnumerateArray())
            {
                if (token.ValueKind == JsonValueKind.Object &&
                    token.TryGetProperty("content", out var contentElement) &&
                    token.TryGetProperty("id", out var idElement) &&
                    idElement.TryGetUInt32(out var id) &&
                    contentElement.GetString() is { Length: > 0 } content)
                {
                    vocabulary[content] = id;
                }
            }
        }

        return vocabulary;
    }

    private static (string Token, uint Id)? FindToken(
        IReadOnlyDictionary<string, uint> vocabulary,
        params string[] names)
    {
        foreach (var name in names)
        {
            if (vocabulary.TryGetValue(name, out var id))
            {
                return (name, id);
            }
        }

        return null;
    }

    private sealed record SpecialTokens(
        uint ClassifierTokenId,
        uint SeparatorTokenId,
        uint PaddingTokenId,
        bool UsesRobertaPairLayout);

    private sealed record PairEncoding(IReadOnlyList<long> InputIds, IReadOnlyList<long> TokenTypeIds);
}

internal sealed record EncodedBatch(
    long[] InputIds,
    long[] AttentionMask,
    long[] TokenTypeIds,
    int BatchSize,
    int SequenceLength);
