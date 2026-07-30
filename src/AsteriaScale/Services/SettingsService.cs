using System.Text.Json;
using AsteriaScale.Models;

namespace AsteriaScale.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _settingsPath;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public SettingsService(string? settingsPath = null)
    {
        _settingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AsteriaScale",
            "settings.json");
    }

    public string SettingsPath => _settingsPath;

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_settingsPath))
        {
            return new AppSettings();
        }

        try
        {
            await using FileStream stream = File.OpenRead(_settingsPath);
            AppSettings? settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, cancellationToken);
            if (settings is null)
            {
                return new AppSettings();
            }

            settings.Profiles ??= [];
            settings.DpiOverrides ??= [];
            settings.SelectedPresetId = PresetCatalog.Find(settings.SelectedPresetId).Id;
            settings.VisualNovelFrameRateLimit = VisualNovelFrameRates.Normalize(settings.VisualNovelFrameRateLimit);
            settings.HoverTranslationMode =
                HoverTranslationModes.NormalizeUserMode(settings.HoverTranslationMode);
            settings.SmartDictionaryBackend = SmartDictionaryBackends.Normalize(settings.SmartDictionaryBackend);
            settings.SmartDictionaryRankerModel =
                SmartDictionaryRankerModels.Normalize(settings.SmartDictionaryRankerModel);
            settings.SmartDictionarySourceLanguage =
                SmartDictionarySourceLanguages.Normalize(settings.SmartDictionarySourceLanguage);
            settings.SmartPhraseTranslationMode =
                SmartPhraseTranslationModes.Normalize(settings.SmartPhraseTranslationMode);
            if (!SmartPhraseTranslationBackends.TryNormalize(
                    settings.SmartPhraseTranslationBackend,
                    out string phraseBackend))
            {
                throw new JsonException(
                    $"Неизвестный backend TranslateGemma: {settings.SmartPhraseTranslationBackend}.");
            }

            settings.SmartPhraseTranslationBackend = phraseBackend;
            return settings;
        }
        catch (JsonException)
        {
            string backupPath = _settingsPath + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            File.Copy(_settingsPath, backupPath, true);
            return new AppSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        string temporaryPath = _settingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            string? directory = Path.GetDirectoryName(_settingsPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using (FileStream stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken);
            }

            AtomicFile.Replace(temporaryPath, _settingsPath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            _writeLock.Release();
        }
    }
}
