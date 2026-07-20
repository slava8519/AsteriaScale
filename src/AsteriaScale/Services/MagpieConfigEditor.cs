using System.Text.Json;
using System.Text.Json.Nodes;
using AsteriaScale.Models;

namespace AsteriaScale.Services;

public sealed class MagpieConfigEditor
{
    private const double VisualNovelMinimumFrameRate = 10.0;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task ApplyPresetAsync(
        string configPath,
        UpscalePreset selectedPreset,
        bool hideEngineTray = true,
        int visualNovelFrameRateLimit = VisualNovelFrameRates.Default,
        CancellationToken cancellationToken = default)
    {
        JsonObject root = await ReadConfigAsync(configPath, cancellationToken);
        JsonArray scalingModes = root["scalingModes"] as JsonArray ?? [];
        root["scalingModes"] = scalingModes;

        RemoveAsteriaModes(scalingModes);
        foreach (UpscalePreset preset in PresetCatalog.All)
        {
            scalingModes.Add(CreateMode(preset));
        }

        int selectedIndex = FindModeIndex(scalingModes, selectedPreset.EngineModeName);
        if (selectedIndex < 0)
        {
            throw new InvalidOperationException("Не удалось добавить выбранный пресет в конфигурацию Magpie.");
        }

        JsonArray profiles = root["profiles"] as JsonArray ?? [];
        root["profiles"] = profiles;
        bool isVisualNovelPreset = selectedPreset.IsVisualNovel;
        int normalizedFrameRateLimit = VisualNovelFrameRates.Normalize(visualNovelFrameRateLimit);
        if (profiles.Count == 0)
        {
            profiles.Add(CreateDefaultProfile(selectedIndex, isVisualNovelPreset, normalizedFrameRateLimit));
        }
        else if (profiles[0] is JsonObject defaultProfile)
        {
            defaultProfile["scalingMode"] = selectedIndex;
            defaultProfile["captureMethod"] = 0;
            defaultProfile["captureTitleBar"] = false;
            defaultProfile["adjustCursorSpeed"] = true;
            defaultProfile["frameRateLimiterEnabled"] = isVisualNovelPreset;
            defaultProfile["maxFrameRate"] = normalizedFrameRateLimit;
            if (isVisualNovelPreset)
            {
                // Magpie skips duplicate-frame detection completely in 3D game mode.
                defaultProfile["3DGameMode"] = false;
                defaultProfile["disableDirectFlip"] = false;
            }
        }

        JsonObject shortcuts = root["shortcuts"] as JsonObject ?? new JsonObject();
        shortcuts["scale"] = 3137;              // Alt+Shift+A
        shortcuts["windowedModeScale"] = 3153; // Alt+Shift+Q
        root["shortcuts"] = shortcuts;
        root["showNotifyIcon"] = !hideEngineTray;
        root["countdownSeconds"] = 0;
        root["duplicateFrameDetectionMode"] = isVisualNovelPreset ? 0 : 1;
        if (isVisualNovelPreset)
        {
            // A small redraw floor avoids aggressive GPU downclocking between VN scene changes.
            root["minFrameRate"] = VisualNovelMinimumFrameRate;
        }

        // Keep compiled shaders cached and allow the faster half-precision shader path.
        root["disableEffectCache"] = false;
        root["disableFP16"] = false;
        root["autoCheckForUpdates"] = true;

        string? directory = Path.GetDirectoryName(configPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporaryPath = configPath + ".asteria-tmp";
        await File.WriteAllTextAsync(temporaryPath, root.ToJsonString(JsonOptions), cancellationToken);
        File.Move(temporaryPath, configPath, true);
    }

    public IReadOnlyList<string> ValidateEffects(string effectsDirectory, UpscalePreset preset)
    {
        List<string> missing = [];
        foreach (EffectDefinition effect in preset.Effects)
        {
            string relativePath = effect.Name.Replace('\\', Path.DirectorySeparatorChar) + ".hlsl";
            if (!File.Exists(Path.Combine(effectsDirectory, relativePath)))
            {
                missing.Add(effect.Name);
            }
        }

        return missing;
    }

    private static async Task<JsonObject> ReadConfigAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return CreateBaseConfig();
        }

        string json = await File.ReadAllTextAsync(path, cancellationToken);
        try
        {
            return JsonNode.Parse(json)?.AsObject() ?? CreateBaseConfig();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Конфигурация Magpie повреждена. Откройте Magpie один раз для её восстановления.", ex);
        }
    }

    private static void RemoveAsteriaModes(JsonArray modes)
    {
        for (int index = modes.Count - 1; index >= 0; index--)
        {
            string? name = modes[index]?["name"]?.GetValue<string>();
            if (name?.StartsWith("Asteria • ", StringComparison.Ordinal) == true)
            {
                modes.RemoveAt(index);
            }
        }
    }

    private static JsonObject CreateMode(UpscalePreset preset)
    {
        JsonArray effects = [];
        foreach (EffectDefinition definition in preset.Effects)
        {
            JsonObject effect = new() { ["name"] = definition.Name };
            if (definition.ScalingType.HasValue)
            {
                effect["scalingType"] = definition.ScalingType.Value;
                effect["scale"] = new JsonObject
                {
                    ["x"] = definition.ScaleX,
                    ["y"] = definition.ScaleY
                };
            }

            if (definition.Parameters is { Count: > 0 })
            {
                JsonObject parameters = [];
                foreach ((string name, float value) in definition.Parameters)
                {
                    parameters[name] = value;
                }

                effect["parameters"] = parameters;
            }

            effects.Add(effect);
        }

        return new JsonObject
        {
            ["name"] = preset.EngineModeName,
            ["effects"] = effects
        };
    }

    private static int FindModeIndex(JsonArray modes, string name)
    {
        for (int index = 0; index < modes.Count; index++)
        {
            if (string.Equals(modes[index]?["name"]?.GetValue<string>(), name, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private static JsonObject CreateDefaultProfile(
        int scalingMode,
        bool isVisualNovelPreset,
        int visualNovelFrameRateLimit) => new()
    {
        ["scalingMode"] = scalingMode,
        ["captureMethod"] = 0,
        ["multiMonitorUsage"] = 0,
        ["initialWindowedScaleFactor"] = 0,
        ["customInitialWindowedScaleFactor"] = 1.25,
        ["graphicsCardId"] = new JsonObject { ["idx"] = -1, ["vendorId"] = 0, ["deviceId"] = 0 },
        ["frameRateLimiterEnabled"] = isVisualNovelPreset,
        ["maxFrameRate"] = visualNovelFrameRateLimit,
        ["3DGameMode"] = false,
        ["captureTitleBar"] = false,
        ["adjustCursorSpeed"] = true,
        ["disableDirectFlip"] = false,
        ["cursorScaling"] = 2,
        ["customCursorScaling"] = 1.0,
        ["cursorInterpolationMode"] = 0,
        ["autoHideCursorEnabled"] = false,
        ["autoHideCursorDelay"] = 3.0,
        ["croppingEnabled"] = false,
        ["cropping"] = new JsonObject { ["left"] = 0, ["top"] = 0, ["right"] = 0, ["bottom"] = 0 }
    };

    private static JsonObject CreateBaseConfig() => new()
    {
        ["language"] = "",
        ["theme"] = 2,
        ["shortcuts"] = new JsonObject(),
        ["scalingModes"] = new JsonArray(),
        ["profiles"] = new JsonArray()
    };
}
