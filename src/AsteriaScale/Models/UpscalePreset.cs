using System.Collections.ObjectModel;

namespace AsteriaScale.Models;

public sealed record EffectDefinition(
    string Name,
    int? ScalingType = null,
    float ScaleX = 1f,
    float ScaleY = 1f,
    IReadOnlyDictionary<string, float>? Parameters = null);

public sealed record UpscalePreset(
    string Id,
    string Name,
    string ShortDescription,
    string DetailedDescription,
    string PerformanceHint,
    IReadOnlyList<EffectDefinition> Effects,
    bool IsRecommended = false)
{
    public bool IsVisualNovel => Id.StartsWith("vn-", StringComparison.OrdinalIgnoreCase);
    public string EngineModeName => string.Format("Asteria • {0}", Name);
    public string DisplayName => IsRecommended ? string.Format("{0}  ·  рекомендуется", Name) : Name;
}

public static class PresetCatalog
{
    private static readonly IReadOnlyList<UpscalePreset> PresetsValue =
        new ReadOnlyCollection<UpscalePreset>(
        [
            new(
                "vn-performance",
                "Visual Novel Fast",
                "Плавный режим для уже крупных окон и экранов 3K.",
                "FSR увеличивает почти готовый кадр до размера экрана, а лёгкий RCAS подчёркивает текст и контуры. Режим не создаёт промежуточный CNN-кадр 5K и поэтому оставляет Radeon запас для плавного захвата.",
                "Для Rewrite+ и других VN с увеличением до 1.6×; минимальная задержка и нагрузка",
                [
                    new("FSR\\FSR_EASU", 1),
                    new("FSR\\FSR_RCAS", Parameters: Params(("sharpness", 0.82f)))
                ],
                true),
            new(
                "vn-efficient",
                "Visual Novel Balanced",
                "Облегчённый CNN-режим для исходного разрешения 720p–1080p.",
                "CuNNy2 4×12 восстанавливает текст и рисованные контуры заметно экономнее HQ-модели 4×24. Затем FSR подгоняет изображение под экран, а быстрый RCAS возвращает локальный контраст без тяжёлого AdaptiveSharpen.",
                "Рекомендуется для 1080p на Radeon 860M: качественнее Fast и существенно плавнее HQ",
                [
                    new("CuNNy2\\CuNNy-4x12-NVL"),
                    new("FSR\\FSR_EASU", 1),
                    new("FSR\\FSR_RCAS", Parameters: Params(("sharpness", 0.84f)))
                ]),
            new(
                "vn-artcnn",
                "Visual Novel ArtCNN (эксперимент)",
                "Официальные MIT-веса ArtCNN C4F16 для рисованного контента.",
                "ArtCNN обучен на аниме-стилле и восстанавливает линии и градиенты иначе, чем CuNNy: попробуйте на сценах, где CuNNy перешарпливает фон. Затем FSR доводит кадр до экрана и RCAS возвращает контраст. Эффект устанавливается в Magpie автоматически при первом запуске масштабирования.",
                "Экспериментальный: сравните с Balanced на вашей новелле",
                [
                    new("ArtCNN_C4F16"),
                    new("FSR\\FSR_EASU", 1),
                    new("FSR\\FSR_RCAS", Parameters: Params(("sharpness", 0.84f)))
                ]),
            new(
                "vn-balanced",
                "Visual Novel HQ",
                "Чёткий текст и аккуратные линии без тяжёлого перешарпа.",
                "Усиленная CuNNy2 4×24 восстанавливает контуры и глифы, затем FSR доводит кадр до точного размера экрана, а мягкий AdaptiveSharpen возвращает локальный контраст. При входе 1080p CNN обрабатывает промежуточный кадр 4K.",
                "Для 720p–1080p при большом запасе GPU; при рывках выберите Visual Novel Balanced",
                [
                    new("CuNNy2\\CuNNy-4x24-NVL"),
                    new("FSR\\FSR_EASU", 1),
                    new("Sharpen\\AdaptiveSharpen", Parameters: Params(("curveHeight", 0.28f)))
                ]),
            new(
                "vn-ultra",
                "Visual Novel Ultra",
                "Максимальная модель CuNNy2 для статичных сцен и CG.",
                "Самый тяжёлый вариант CuNNy2 8×32 сохраняет тонкие линии и детали шрифтов. Рекомендуется для визуальных новел с невысокой частотой обновления сцены.",
                "Максимум качества; при просадках выберите Visual Novel HQ",
                [
                    new("CuNNy2\\CuNNy-8x32-NVL"),
                    new("FSR\\FSR_EASU", 1),
                    new("Sharpen\\AdaptiveSharpen", Parameters: Params(("curveHeight", 0.22f)))
                ]),
            new(
                "vn-soft",
                "Visual Novel Soft",
                "Консервативный режим без ореолов вокруг текста.",
                "Более лёгкая нейросетевая модель с perceptual downscale. Подходит для чистых 1080p-новел и тонких светлых шрифтов.",
                "Мягкая картинка и низкий риск артефактов",
                [
                    new("CuNNy2\\CuNNy-4x16-NVL"),
                    new("SSimDownscaler", 1, Parameters: Params(("oversharp", 1.0f)))
                ]),
            new(
                "anime",
                "Anime Line Art",
                "Восстановление линий Anime4K для роликов и анимации.",
                "Сначала устраняет дефекты линий, затем выполняет CNN-апскейл и подгоняет результат к экрану.",
                "Хорошо для анимированных сцен; тяжелее универсального режима",
                [
                    new("Anime4K\\Anime4K_Restore_Soft_M"),
                    new("Anime4K\\Anime4K_Upscale_L"),
                    new("FSR\\FSR_EASU", 1)
                ]),
            new(
                "general",
                "Universal FSR",
                "Быстрый пространственный апскейл для любых программ и 3D.",
                "AMD FSR EASU реконструирует границы, RCAS возвращает контраст. Это быстрый запасной вариант, когда CNN-режимы слишком тяжёлые.",
                "Минимальная задержка и высокая частота кадров",
                [
                    new("FSR\\FSR_EASU", 1),
                    new("FSR\\FSR_RCAS", Parameters: Params(("sharpness", 0.82f)))
                ]),
            new(
                "pixel",
                "Pixel Perfect",
                "Целочисленные края для пиксель-арта и старых игр.",
                "xBRZ распознаёт пиксельные контуры и масштабирует их без размытия обычной интерполяции.",
                "Для пиксельной графики; не использовать для фотографий",
                [new("xBRZ\\xBRZ_Freescale", 1)])
        ]);

    public static IReadOnlyList<UpscalePreset> All => PresetsValue;
    public static UpscalePreset Default => PresetsValue[0];

    public static UpscalePreset Find(string? id) =>
        PresetsValue.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Default;

    private static IReadOnlyDictionary<string, float> Params(params (string Name, float Value)[] values) =>
        values.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal);
}
