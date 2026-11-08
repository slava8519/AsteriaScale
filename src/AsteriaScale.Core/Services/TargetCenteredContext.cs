using System.Text.RegularExpressions;
using AsteriaScale.RankerProtocol;

namespace AsteriaScale.Services;

/// <summary>
/// The exact context handed to the sense ranker, together with the target's
/// position inside it.
/// </summary>
internal readonly record struct TargetCenteredContext(
    string Text,
    int CharacterIndex,
    bool Trimmed);

/// <summary>
/// Selects the context contract used for both worker input and decision-cache
/// keys. Pair scorers receive a bounded target-centred window; ConSeC receives
/// the complete source sentence because its independent extractor was trained
/// to reason over the full sentence and has its own 512-token fail-closed cap.
///
/// Two defects motivate it:
///
/// 1. The worker's pair tokenizer implements Hugging Face LongestFirst
///    truncation, which drops tokens from the **right**. On a reply longer than
///    the sequence budget a target sitting late in the line — together with its
///    quote markers — is silently cut before the model sees it, and the model
///    then scores glosses against a context that no longer contains the word
///    being disambiguated. Centering the window makes the surviving text the
///    neighbourhood that actually carries the sense signal.
/// 2. The hover cache used to key on a window narrower than the model's input,
///    so two replies sharing a neighbourhood could reuse each other's verdict.
///    Keying on the same string the model receives removes that approximation
///    instead of documenting it.
///
/// The window never drops the target: budget is spent outward from the target
/// span, and a target longer than the budget still survives whole.
/// </summary>
internal static partial class TargetCenteredContextBuilder
{
    /// <summary>
    /// Words kept around the target. Calibrated against the tighter of the two
    /// providers: the NPU sequence length is 64 tokens, of which three are
    /// special and the rest is shared with the gloss under LongestFirst. At the
    /// English wordpiece rate this budget leaves room for an ordinary gloss, and
    /// any residual worker-side truncation now removes distant context rather
    /// than the target. Recalibrating it changes model input, so it requires a
    /// `--ranker-benchmark` run.
    /// </summary>
    internal const int MaximumWords = 32;

    public static TargetCenteredContext BuildForRanker(
        string context,
        string target,
        int characterIndex,
        GlossBertRankerProvider provider,
        RankerCpuModelProfile cpuModelProfile) =>
        provider.Traits().AlternativeModels &&
        cpuModelProfile == RankerCpuModelProfile.Consec
            ? PreserveFull(context, target, characterIndex)
            : Build(context, target, characterIndex);

    public static TargetCenteredContext PreserveFull(
        string context,
        string target,
        int characterIndex)
    {
        string fullContext = context ?? string.Empty;
        if (fullContext.Length == 0 || string.IsNullOrEmpty(target))
        {
            return new TargetCenteredContext(fullContext, characterIndex, false);
        }

        int anchor = RankerTargetAnchor.FindClosestOccurrence(
            fullContext,
            target,
            characterIndex);
        return new TargetCenteredContext(
            fullContext,
            anchor >= 0 ? anchor : characterIndex,
            Trimmed: false);
    }

    public static TargetCenteredContext Build(
        string context,
        string target,
        int characterIndex)
    {
        if (string.IsNullOrEmpty(context) || string.IsNullOrEmpty(target))
        {
            return new TargetCenteredContext(context ?? string.Empty, characterIndex, false);
        }

        // Resolve the occurrence with the worker's own anchoring so the window
        // is centred on the span the worker will mark, not on a different one.
        int anchor = RankerTargetAnchor.FindClosestOccurrence(
            context,
            target,
            characterIndex);
        if (anchor < 0)
        {
            // No standalone occurrence: the worker will reject this request
            // anyway. Leave the context untouched rather than invent a window.
            return new TargetCenteredContext(context, characterIndex, false);
        }

        Match[] words = WordRegex().Matches(context).Cast<Match>().ToArray();
        if (words.Length <= MaximumWords)
        {
            return new TargetCenteredContext(context, anchor, false);
        }

        int targetEnd = anchor + target.Length;
        int first = -1;
        int last = -1;
        for (int index = 0; index < words.Length; index++)
        {
            Match word = words[index];
            bool overlapsTarget = word.Index < targetEnd &&
                                  anchor < word.Index + word.Length;
            if (!overlapsTarget)
            {
                continue;
            }

            if (first < 0)
            {
                first = index;
            }

            last = index;
        }

        if (first < 0)
        {
            // The anchor fell between word matches (punctuation-only span).
            // Centre on the nearest word instead of failing.
            first = last = NearestWordIndex(words, anchor);
        }

        int remaining = Math.Max(0, MaximumWords - (last - first + 1));
        int left = first;
        int right = last;
        // Spend the budget alternately so the target stays centred, and let one
        // side absorb the remainder when the other reaches a sentence edge.
        while (remaining > 0 && (left > 0 || right < words.Length - 1))
        {
            if (left > 0)
            {
                left--;
                remaining--;
            }

            if (remaining > 0 && right < words.Length - 1)
            {
                right++;
                remaining--;
            }
        }

        int start = words[left].Index;
        int end = words[right].Index + words[right].Length;
        string window = context[start..end];
        return new TargetCenteredContext(window, anchor - start, Trimmed: true);
    }

    private static int NearestWordIndex(IReadOnlyList<Match> words, int anchor)
    {
        int best = 0;
        int bestDistance = int.MaxValue;
        for (int index = 0; index < words.Count; index++)
        {
            Match word = words[index];
            int distance = anchor < word.Index
                ? word.Index - anchor
                : anchor > word.Index + word.Length
                    ? anchor - (word.Index + word.Length)
                    : 0;
            if (distance < bestDistance)
            {
                best = index;
                bestDistance = distance;
            }
        }

        return best;
    }

    [GeneratedRegex(
        @"[\p{L}\p{M}\p{N}]+(?:['’\-][\p{L}\p{M}\p{N}]+)*",
        RegexOptions.CultureInvariant)]
    private static partial Regex WordRegex();
}
