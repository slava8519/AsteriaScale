using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace AsteriaScale.Models;

public sealed class AppSettings
{
    public string SelectedPresetId { get; set; } = PresetCatalog.Default.Id;
    public int VisualNovelFrameRateLimit { get; set; } = VisualNovelFrameRates.Default;
    public bool AutomationEnabled { get; set; } = true;
    public bool RunAtStartup { get; set; }
    public bool StartMinimized { get; set; }
    public bool CloseToTray { get; set; } = true;
    public bool ShowFirstTrayHint { get; set; } = true;
    public bool HoverTranslationEnabled { get; set; }
    public int HoverTranslationDelayMs { get; set; } = 700;
    public string HoverTranslationMode { get; set; } = HoverTranslationModes.Smart;
    public string SmartDictionaryBackend { get; set; } = SmartDictionaryBackends.Auto;
    public string SmartDictionaryRankerModel { get; set; } =
        SmartDictionaryRankerModels.GlossBert;
    public string SmartDictionarySourceLanguage { get; set; } = SmartDictionarySourceLanguages.Auto;
    public bool SmartDictionaryKeepWarm { get; set; } = true;
    public string SmartPhraseTranslationMode { get; set; } = SmartPhraseTranslationModes.Off;
    public string SmartPhraseTranslationBackend { get; set; } = SmartPhraseTranslationBackends.Cpu;

    // Optional Textractor text hook (roadmap #4). Off by default: OCR stays
    // the universal path; the hook only upgrades hover context for games it
    // can attach to. TextractorDirectory points at a user-installed Textractor
    // (the folder containing x86/x64 TextractorCLI.exe), like Magpie.
    public bool TextHookEnabled { get; set; }

    public string TextractorDirectory { get; set; } = string.Empty;
    public List<ManagedDpiOverride> DpiOverrides { get; set; } = [];
    public List<SpeakerGenderPreference> SpeakerGenders { get; set; } = [];
    public List<AutomationProfile> Profiles { get; set; } = [];
}

public sealed record SpeakerGenderPreference
{
    public string GameKey { get; init; } = string.Empty;
    public string SpeakerName { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Gender { get; init; } = string.Empty;
}

public sealed class ManagedDpiOverride
{
    public string ProcessPath { get; set; } = string.Empty;
    public string? OriginalCompatibilityLayers { get; set; }
    public string AppliedCompatibilityLayers { get; set; } = string.Empty;
}

public static class VisualNovelFrameRates
{
    public const int Default = 60;

    public static int Normalize(int value) => value switch
    {
        30 or 45 or 60 => value,
        _ => Default
    };
}

public static class HoverTranslationModes
{
    public const string Smart = "smart";
    public const string Dictionary = "dictionary";

    public static string NormalizeUserMode(string? value) =>
        string.Equals(value, Dictionary, StringComparison.OrdinalIgnoreCase)
            ? Dictionary
            : Smart;
}

public static class SmartDictionaryBackends
{
    public const string Auto = "auto";
    public const string Npu = "npu";
    public const string Cpu = "cpu";
    // Legacy value from releases that used the full translation model as a ranker.
    public const string Gpu = "gpu";

    public static string Normalize(string? value)
    {
        if (string.Equals(value, Npu, StringComparison.OrdinalIgnoreCase))
        {
            return Npu;
        }

        if (string.Equals(value, Cpu, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, Gpu, StringComparison.OrdinalIgnoreCase))
        {
            return Cpu;
        }

        return Auto;
    }
}

public static class SmartDictionarySourceLanguages
{
    public const string Auto = "auto";
    public const string English = "en";
    public const string Japanese = "ja";

    public static string Normalize(string? value)
    {
        if (string.Equals(value, English, StringComparison.OrdinalIgnoreCase))
        {
            return English;
        }

        return string.Equals(value, Japanese, StringComparison.OrdinalIgnoreCase)
            ? Japanese
            : Auto;
    }
}

public static class SmartDictionaryRankerModels
{
    public const string GlossBert = "glossbert";
    public const string Consec = "consec";

    // Removed profiles ("sandwich", "deberta") deliberately fall through to the
    // default: an old settings.json must keep loading instead of being treated
    // as broken and reset.
    public static string Normalize(string? value) =>
        string.Equals(value, Consec, StringComparison.OrdinalIgnoreCase)
            ? Consec
            : GlossBert;
}

public static class SmartPhraseTranslationModes
{
    public const string Off = "off";
    public const string TranslateGemma = "translategemma-4b";

    public static string Normalize(string? value) =>
        string.Equals(value, TranslateGemma, StringComparison.OrdinalIgnoreCase)
            ? TranslateGemma
            : Off;
}

public static class SmartPhraseTranslationBackends
{
    public const string Cpu = "cpu";
    public const string Vulkan = "vulkan";

    public static string Normalize(string? value) =>
        TryNormalize(value, out string normalized) ? normalized : Cpu;

    public static bool TryNormalize(string? value, out string normalized)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            string.Equals(value, Cpu, StringComparison.OrdinalIgnoreCase))
        {
            normalized = Cpu;
            return true;
        }

        if (string.Equals(value, Vulkan, StringComparison.OrdinalIgnoreCase))
        {
            normalized = Vulkan;
            return true;
        }

        normalized = Cpu;
        return false;
    }
}

public sealed class AutomationProfile : INotifyPropertyChanged
{
    private string _presetId = PresetCatalog.Default.Id;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;

    // Per-profile text hook: on by default so profile games use the hook once
    // the global toggle is enabled; non-profile apps follow the global toggle.
    public bool TextHookEnabled { get; set; } = true;

    // Optional Textractor hook code (H-code/R-code) for engines whose
    // automatic hooks fail; sent to the CLI right after attach.
    public string TextHookCode { get; set; } = string.Empty;

    public string ProcessName { get; set; } = string.Empty;
    public string ProcessPath { get; set; } = string.Empty;
    public string ClassName { get; set; } = string.Empty;
    public string PresetId
    {
        get => _presetId;
        set
        {
            string normalized = PresetCatalog.Find(value).Id;
            if (string.Equals(_presetId, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _presetId = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Details));
        }
    }
    public bool Windowed { get; set; }
    public bool Enabled { get; set; } = true;

    [JsonIgnore]
    public string Details
    {
        get
        {
            UpscalePreset preset = PresetCatalog.Find(PresetId);
            return string.Format("{0}  •  {1}", preset.Name, Windowed ? "оконный" : "полный экран");
        }
    }

    public bool Matches(WindowInfo window)
    {
        if (!Enabled)
        {
            return false;
        }

        bool processMatches = !string.IsNullOrWhiteSpace(ProcessPath)
            ? string.Equals(ProcessPath, window.ProcessPath, StringComparison.OrdinalIgnoreCase)
            : string.Equals(ProcessName, window.ProcessName, StringComparison.OrdinalIgnoreCase);

        return processMatches &&
               (string.IsNullOrWhiteSpace(ClassName) ||
                string.Equals(ClassName, window.ClassName, StringComparison.Ordinal));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
