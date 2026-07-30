using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using AsteriaScale.Models;

namespace AsteriaScale.Services;

internal sealed record SpeakerGameIdentity(string GameKey, string DisplayName);

/// <summary>
/// Stores only explicit, game-scoped speaker choices. A name that happens to
/// occur in another game never inherits gender from this table.
/// </summary>
internal static class SpeakerGenderPreferences
{
    internal const string MasculineValue = "masculine";
    internal const string FeminineValue = "feminine";
    internal const string IgnoreValue = "ignore";

    private const string PathKeyPrefix = "path:";
    private const string ProcessKeyPrefix = "process:";

    internal static SpeakerGameIdentity? ResolveGameIdentity(int processId)
    {
        if (processId <= 0)
        {
            return null;
        }

        try
        {
            using Process process = Process.GetProcessById(processId);
            string? processPath = null;
            string? processName = null;
            try
            {
                processPath = process.MainModule?.FileName;
            }
            catch (Exception exception) when (IsProcessMetadataException(exception))
            {
                // Protected and cross-architecture processes can deny module
                // inspection. Their process name remains a stable fallback.
            }

            try
            {
                processName = process.ProcessName;
            }
            catch (Exception exception) when (IsProcessMetadataException(exception))
            {
                // The process can exit between PID selection and inspection.
            }

            return CreateGameIdentity(processPath, processName);
        }
        catch (Exception exception) when (IsProcessMetadataException(exception))
        {
            return null;
        }
    }

    internal static SpeakerGameIdentity? CreateGameIdentity(
        string? processPath,
        string? processName)
    {
        string? normalizedPath = NormalizeProcessPath(processPath);
        if (normalizedPath is not null)
        {
            string displayName = NormalizeDisplayName(Path.GetFileName(normalizedPath));
            if (displayName.Length == 0)
            {
                displayName = NormalizeProcessDisplayName(processName);
            }

            return new SpeakerGameIdentity(
                PathKeyPrefix + normalizedPath,
                displayName.Length == 0 ? "Игра" : displayName);
        }

        string normalizedProcessName = NormalizeProcessName(processName);
        if (normalizedProcessName.Length == 0)
        {
            return null;
        }

        return new SpeakerGameIdentity(
            ProcessKeyPrefix + normalizedProcessName,
            NormalizeProcessDisplayName(processName));
    }

    internal static CurrentSpeakerContext Resolve(
        AppSettings settings,
        SpeakerGameIdentity? gameIdentity,
        string? rawName)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string name = NormalizeSpeakerName(rawName);
        string gameKey = NormalizeGameKey(gameIdentity?.GameKey) ?? string.Empty;
        if (name.Length == 0 || gameKey.Length == 0)
        {
            return new CurrentSpeakerContext(name, SpeakerGender.Unknown, gameKey);
        }

        foreach (SpeakerGenderPreference preference in settings.SpeakerGenders ?? [])
        {
            string? preferenceGameKey = NormalizeGameKey(preference?.GameKey);
            string preferenceName = NormalizeSpeakerName(preference?.SpeakerName);
            if (!string.Equals(gameKey, preferenceGameKey, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(name, preferenceName, StringComparison.OrdinalIgnoreCase) ||
                !TryParseGender(preference?.Gender, out SpeakerGender gender))
            {
                continue;
            }

            string displayName = NormalizeSpeakerName(preference?.DisplayName);
            return new CurrentSpeakerContext(
                displayName.Length == 0 ? name : displayName,
                gender,
                gameKey);
        }

        return new CurrentSpeakerContext(name, SpeakerGender.Unknown, gameKey);
    }

    internal static bool Set(
        AppSettings settings,
        SpeakerGameIdentity? gameIdentity,
        string? rawName,
        SpeakerGender gender,
        string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string? gameKey = NormalizeGameKey(gameIdentity?.GameKey);
        string name = NormalizeSpeakerName(rawName);
        if (gameKey is null || name.Length == 0)
        {
            return false;
        }

        Normalize(settings);
        int existingIndex = FindPreferenceIndex(settings.SpeakerGenders, gameKey, name);
        if (gender == SpeakerGender.Unknown)
        {
            if (existingIndex < 0)
            {
                return false;
            }

            settings.SpeakerGenders.RemoveAt(existingIndex);
            return true;
        }

        string? persistedGender = SerializeGender(gender);
        if (persistedGender is null)
        {
            return false;
        }

        SpeakerGenderPreference replacement = new()
        {
            GameKey = gameKey,
            SpeakerName = name,
            DisplayName = NormalizeSpeakerName(displayName) is { Length: > 0 } normalizedDisplayName
                ? normalizedDisplayName
                : name,
            Gender = persistedGender
        };
        if (existingIndex >= 0)
        {
            SpeakerGenderPreference existing = settings.SpeakerGenders[existingIndex];
            if (string.Equals(
                    existing.GameKey,
                    replacement.GameKey,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    existing.SpeakerName,
                    replacement.SpeakerName,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    existing.DisplayName,
                    replacement.DisplayName,
                    StringComparison.Ordinal) &&
                string.Equals(
                    existing.Gender,
                    replacement.Gender,
                    StringComparison.Ordinal))
            {
                return false;
            }

            settings.SpeakerGenders[existingIndex] = replacement;
        }
        else
        {
            settings.SpeakerGenders.Add(replacement);
        }

        return true;
    }

    internal static void Normalize(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        List<SpeakerGenderPreference> normalized = [];
        foreach (SpeakerGenderPreference preference in settings.SpeakerGenders ?? [])
        {
            string? gameKey = NormalizeGameKey(preference?.GameKey);
            string name = NormalizeSpeakerName(preference?.SpeakerName);
            string displayName = NormalizeSpeakerName(preference?.DisplayName);
            if (gameKey is null ||
                name.Length == 0 ||
                !TryParseGender(preference?.Gender, out SpeakerGender gender))
            {
                continue;
            }

            string? persistedGender = SerializeGender(gender);
            if (persistedGender is null)
            {
                continue;
            }

            SpeakerGenderPreference canonical = new()
            {
                GameKey = gameKey,
                SpeakerName = name,
                DisplayName = displayName.Length == 0 ? name : displayName,
                Gender = persistedGender
            };
            int duplicateIndex = FindPreferenceIndex(normalized, gameKey, name);
            if (duplicateIndex >= 0)
            {
                // Later JSON entries represent the most recent explicit edit.
                normalized[duplicateIndex] = canonical;
            }
            else
            {
                normalized.Add(canonical);
            }
        }

        settings.SpeakerGenders = normalized;
    }

    internal static string NormalizeSpeakerName(string? value)
    {
        string normalized = (value ?? string.Empty).Normalize(NormalizationForm.FormC);
        StringBuilder result = new(normalized.Length);
        bool pendingWhitespace = false;
        foreach (char character in normalized)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingWhitespace = result.Length > 0;
                continue;
            }

            if (pendingWhitespace)
            {
                result.Append(' ');
                pendingWhitespace = false;
            }

            result.Append(character);
        }

        return result.Length <= SpeakerMetadataParser.MaximumNameLength
            ? result.ToString()
            : string.Empty;
    }

    private static int FindPreferenceIndex(
        IReadOnlyList<SpeakerGenderPreference> preferences,
        string gameKey,
        string speakerName)
    {
        for (int index = 0; index < preferences.Count; index++)
        {
            SpeakerGenderPreference preference = preferences[index];
            if (string.Equals(
                    NormalizeGameKey(preference.GameKey),
                    gameKey,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    NormalizeSpeakerName(preference.SpeakerName),
                    speakerName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static string? NormalizeGameKey(string? value)
    {
        string normalized = (value ?? string.Empty)
            .Trim()
            .Normalize(NormalizationForm.FormC);
        if (normalized.StartsWith(PathKeyPrefix, StringComparison.OrdinalIgnoreCase))
        {
            string? path = NormalizeProcessPath(normalized[PathKeyPrefix.Length..]);
            return path is null ? null : PathKeyPrefix + path;
        }

        if (normalized.StartsWith(ProcessKeyPrefix, StringComparison.OrdinalIgnoreCase))
        {
            string name = NormalizeProcessName(normalized[ProcessKeyPrefix.Length..]);
            return name.Length == 0 ? null : ProcessKeyPrefix + name;
        }

        return null;
    }

    private static string? NormalizeProcessPath(string? value)
    {
        string path = (value ?? string.Empty)
            .Trim()
            .Trim('"')
            .Normalize(NormalizationForm.FormC);
        if (path.Length == 0 || !Path.IsPathFullyQualified(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string NormalizeProcessName(string? value)
    {
        string candidate = NormalizeDisplayName(value);
        if (candidate.Length == 0)
        {
            return string.Empty;
        }

        candidate = Path.GetFileName(candidate);
        return candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? candidate[..^4]
            : candidate;
    }

    private static string NormalizeProcessDisplayName(string? value)
    {
        string name = NormalizeProcessName(value);
        return name.Length == 0 ? "Игра" : name + ".exe";
    }

    private static string NormalizeDisplayName(string? value) =>
        (value ?? string.Empty).Trim().Normalize(NormalizationForm.FormC);

    private static bool TryParseGender(string? value, out SpeakerGender gender)
    {
        if (string.Equals(value, MasculineValue, StringComparison.OrdinalIgnoreCase))
        {
            gender = SpeakerGender.Masculine;
            return true;
        }

        if (string.Equals(value, FeminineValue, StringComparison.OrdinalIgnoreCase))
        {
            gender = SpeakerGender.Feminine;
            return true;
        }

        if (string.Equals(value, IgnoreValue, StringComparison.OrdinalIgnoreCase))
        {
            gender = SpeakerGender.Ignore;
            return true;
        }

        gender = SpeakerGender.Unknown;
        return false;
    }

    private static string? SerializeGender(SpeakerGender gender) => gender switch
    {
        SpeakerGender.Masculine => MasculineValue,
        SpeakerGender.Feminine => FeminineValue,
        SpeakerGender.Ignore => IgnoreValue,
        _ => null
    };

    private static bool IsProcessMetadataException(Exception exception) =>
        exception is ArgumentException or InvalidOperationException or
            NotSupportedException or Win32Exception or UnauthorizedAccessException;
}
