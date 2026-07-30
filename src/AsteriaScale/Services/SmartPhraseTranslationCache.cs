using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AsteriaScale.Services;

/// <summary>
/// Persistent LRU for guarded sentence generations. Only the SHA-256 request
/// fingerprint and the Russian result reach disk; current or previous dialogue
/// never does.
/// </summary>
internal sealed class SmartPhraseTranslationCache : IAsyncDisposable
{
    internal const int SchemaVersion = 4;
    private const int DefaultCapacity = 4_096;
    private const long DefaultMaximumFileBytes = 4L * 1024 * 1024;
    private const int MaximumStoredTranslationLength =
        SmartPhraseTranslationGuard.MaximumOutputCharacters;
    private const int MaximumStoredIdentityLength = 256;

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = false };

    private readonly PersistentLruStore<PersistedEntry> _store;

    public SmartPhraseTranslationCache(
        string? cachePath = null,
        int capacity = DefaultCapacity,
        long maximumFileBytes = DefaultMaximumFileBytes)
    {
        _store = new PersistentLruStore<PersistedEntry>(
            cachePath ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AsteriaScale",
                "smart-phrase-translations.v4.json"),
            SchemaVersion,
            capacity,
            maximumFileBytes,
            IsStructurallyValid,
            JsonOptions);
    }

    public string CachePath => _store.CachePath;

    public Task<SmartPhraseTranslationResult?> TryGetAsync(
        SmartPhraseTranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        string fingerprint = CreateFingerprint(request);
        return _store.UseAsync(access =>
        {
            if (!access.Entries.TryGetValue(fingerprint, out PersistedEntry? entry))
            {
                return null;
            }

            SmartPhraseTranslationResult result = new(
                entry.Translation,
                entry.ModelIdentity,
                entry.ElapsedMilliseconds,
                FromCache: true);
            if (!SmartPhraseTranslationGuard.ValidateForDisplay(request, result).Accepted)
            {
                access.Entries.Remove(fingerprint);
                access.MarkDirty();
                return null;
            }

            entry.LastAccess = access.Touch();
            access.MarkDirty();
            return result;
        }, cancellationToken);
    }

    /// <summary>
    /// Stores only output that passes the display guard. Neural WSD evidence is
    /// advisory here; deterministic terms, safety and completeness stay strict.
    /// </summary>
    public Task<bool> StoreAsync(
        SmartPhraseTranslationRequest request,
        SmartPhraseTranslationResult result,
        CancellationToken cancellationToken = default)
    {
        string fingerprint = CreateFingerprint(request);
        SmartPhraseGuardResult guarded = SmartPhraseTranslationGuard.ValidateForDisplay(
            request,
            result);
        if (!guarded.Accepted || result.ElapsedMilliseconds < 0)
        {
            return Task.FromResult(false);
        }

        return _store.UseAsync(access =>
        {
            access.Entries[fingerprint] = new PersistedEntry
            {
                Key = fingerprint,
                Translation = guarded.Translation,
                ModelIdentity = result.ModelIdentity,
                ElapsedMilliseconds = result.ElapsedMilliseconds,
                LastAccess = access.Touch()
            };
            access.MarkDirty();
            return true;
        }, cancellationToken);
    }

    public Task ClearAsync(CancellationToken cancellationToken = default) =>
        _store.ClearAsync(cancellationToken);

    public Task FlushAsync(CancellationToken cancellationToken = default) =>
        _store.FlushAsync(cancellationToken);

    internal Task<int> CountAsync(CancellationToken cancellationToken = default) =>
        _store.UseAsync(access => access.Entries.Count, cancellationToken);

    public ValueTask DisposeAsync() => _store.DisposeAsync();

    internal static string CreateFingerprint(SmartPhraseTranslationRequest request)
    {
        ValidateRequest(request);
        StringBuilder canonical = new();
        Append(canonical, "asteria-smart-phrase-cache-v4");
        Append(canonical, request.SourcePhrase);
        Append(canonical, request.ModelInputPhrase);
        Append(canonical, request.TransformIdentity);
        AppendList(canonical, "proper-name", request.ProperNames);
        AppendList(canonical, "previous-context", request.PreviousContext);
        Append(canonical, "semantic-terms");
        Append(canonical, request.SemanticTerms.Count.ToString(CultureInfo.InvariantCulture));
        foreach (ScaffoldTerm term in request.SemanticTerms)
        {
            Append(canonical, term.Source);
            Append(canonical, term.Translation);
            Append(canonical, term.Confidence.ToString("R", CultureInfo.InvariantCulture));
            Append(canonical, ((int)term.Provenance).ToString(CultureInfo.InvariantCulture));
        }

        Append(canonical, "sense-hints");
        Append(canonical, request.SenseHints.Count.ToString(CultureInfo.InvariantCulture));
        foreach (ScaffoldSenseHint hint in request.SenseHints)
        {
            Append(canonical, hint.Source);
            Append(canonical, hint.EnglishDefinition);
            Append(canonical, hint.SelectedTranslation);
            AppendList(canonical, "rejected", hint.RejectedTranslations);
            Append(canonical, hint.Confidence.ToString("R", CultureInfo.InvariantCulture));
        }

        Append(canonical, request.ModelIdentity);
        Append(canonical, request.PromptVersion);
        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static void ValidateRequest(SmartPhraseTranslationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourcePhrase);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ModelInputPhrase);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TransformIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ModelIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PromptVersion);
        ArgumentNullException.ThrowIfNull(request.ProperNames);
        ArgumentNullException.ThrowIfNull(request.PreviousContext);
        ArgumentNullException.ThrowIfNull(request.SemanticTerms);
        ArgumentNullException.ThrowIfNull(request.SenseHints);
        if (SmartPhraseTranslationGuard.ValidateRequest(request) !=
            SmartPhraseRejectionReason.None)
        {
            throw new ArgumentException("Invalid Smart phrase cache request.", nameof(request));
        }
    }

    private static bool IsStructurallyValid(PersistedEntry entry)
    {
        // System.Text.Json can assign null to non-nullable reference properties
        // when a syntactically valid cache file contains explicit null values.
        // Cache state is disposable optimization data, so malformed entries must
        // be skipped as misses instead of escaping the loader and disabling the
        // independent phrase line on every subsequent hover.
        if (entry is null)
        {
            return false;
        }

        string? key = entry.Key;
        string? translation = entry.Translation;
        string? modelIdentity = entry.ModelIdentity;
        return key is { Length: 64 } &&
               key.All(Uri.IsHexDigit) &&
               translation is { Length: > 0 and <= MaximumStoredTranslationLength } &&
               modelIdentity is { Length: > 0 and <= MaximumStoredIdentityLength } &&
               entry.ElapsedMilliseconds >= 0 &&
               entry.LastAccess >= 0;
    }

    private static void AppendList(
        StringBuilder builder,
        string label,
        IReadOnlyList<string> values)
    {
        Append(builder, label);
        Append(builder, values.Count.ToString(CultureInfo.InvariantCulture));
        foreach (string value in values)
        {
            Append(builder, value ?? string.Empty);
        }
    }

    private static void Append(StringBuilder builder, string value)
    {
        builder.Append(value.Length);
        builder.Append(':');
        builder.Append(value);
        builder.Append('|');
    }

    private sealed class PersistedEntry : IPersistentLruEntry
    {
        public string Key { get; set; } = string.Empty;
        public string Translation { get; set; } = string.Empty;
        public string ModelIdentity { get; set; } = string.Empty;
        public long ElapsedMilliseconds { get; set; }
        public long LastAccess { get; set; }
    }
}
