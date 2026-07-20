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
    public string TranslationTargetLanguage { get; set; } = "ru";
    public string SmartDictionaryBackend { get; set; } = SmartDictionaryBackends.Auto;
    public string SmartDictionarySourceLanguage { get; set; } = SmartDictionarySourceLanguages.Auto;
    public string SmartDictionaryFallbackMode { get; set; } = SmartDictionaryFallbackModes.Never;
    public bool SmartDictionaryKeepWarm { get; set; } = true;

    // Optional Textractor text hook (roadmap #4). Off by default: OCR stays
    // the universal path; the hook only upgrades hover context for games it
    // can attach to. TextractorDirectory points at a user-installed Textractor
    // (the folder containing x86/x64 TextractorCLI.exe), like Magpie.
    public bool TextHookEnabled { get; set; }

    public string TextractorDirectory { get; set; } = string.Empty;
    public List<AutomationProfile> Profiles { get; set; } = [];
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
    public const string Neural = "neural";
    public const string Dictionary = "dictionary";
    public const string Screen = "screen";

    public static string Normalize(string? value)
    {
        if (string.Equals(value, Smart, StringComparison.OrdinalIgnoreCase))
        {
            return Smart;
        }

        if (string.Equals(value, Dictionary, StringComparison.OrdinalIgnoreCase))
        {
            return Dictionary;
        }

        return string.Equals(value, Screen, StringComparison.OrdinalIgnoreCase)
            ? Screen
            : Neural;
    }
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

public static class SmartDictionaryFallbackModes
{
    public const string Never = "never";
    public const string OnDemand = "on-demand";
    public const string Automatic = "automatic";

    public static string Normalize(string? value)
    {
        if (string.Equals(value, OnDemand, StringComparison.OrdinalIgnoreCase))
        {
            return OnDemand;
        }

        return string.Equals(value, Automatic, StringComparison.OrdinalIgnoreCase)
            ? Automatic
            : Never;
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
