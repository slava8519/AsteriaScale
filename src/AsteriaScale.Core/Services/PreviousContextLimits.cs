namespace AsteriaScale.Services;

// Bounds on the earlier dialogue lines a text source may attach to the current
// one. The Textractor collector fills the context and the phrase guard rejects
// anything larger, so both must read the same numbers from one place.
internal static class PreviousContextLimits
{
    internal const int MaximumSentences = 3;
    internal const int MaximumCharacters = 1_200;
}
