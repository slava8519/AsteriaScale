namespace AsteriaScale.Services;

/// <summary>
/// Normalized stylistic register of a dictionary sense (roadmap #14.2C).
///
/// The label is a property of the **English sense**: the generator derives it
/// from that sense's Wiktionary tags/categories (`_kaikki_style_prefix`) and
/// attaches it to every translation collected from it. So it describes the
/// original, not the Russian wording — which is why treating a coarse label as
/// a defect of the surface silently softened the source.
///
/// Ordering is by strength, so a row carrying several markers resolves to the
/// strongest one ("жарг. груб." is vulgar, not slang).
/// </summary>
internal enum SenseRegister
{
    Neutral = 0,
    Literary = 1,
    Archaic = 2,
    Dialect = 3,
    Colloquial = 4,
    Slang = 5,
    SexualDomain = 6,
    Vulgar = 7
}

/// <summary>
/// Parses the sidecar's register column. The vocabulary is small and closed:
/// the shipped English sidecar uses exactly seven markers (`устар.`, `перен.`,
/// `жарг.`, `груб.`, `разг.`, `диал.`, `лит.`) in 204 combinations, so the
/// parser matches those explicitly instead of guessing.
///
/// `перен.` (figurative) is deliberately **not** a register: it describes what
/// the sense means, not how blunt it is, and treating it as one would make
/// ordinary metaphors look marked.
/// </summary>
internal static class SenseRegisters
{
    /// <summary>UI labels for an uncertain card, per roadmap #14.2C.</summary>
    internal static string? DisplayLabel(SenseRegister register) => register switch
    {
        SenseRegister.Literary => "ЛИТ.",
        SenseRegister.Colloquial => "РАЗГ.",
        SenseRegister.Slang => "ЖАРГ.",
        SenseRegister.Vulgar => "ГРУБ.",
        SenseRegister.SexualDomain => "СЕКС.",
        SenseRegister.Archaic => "УСТАР.",
        SenseRegister.Dialect => "ДИАЛ.",
        _ => null
    };

    /// <summary>
    /// Returns the strongest register mentioned in a raw label. Unknown text
    /// resolves to <see cref="SenseRegister.Neutral"/>: an unrecognized marker
    /// must not silently promote a sense to "coarse".
    /// </summary>
    internal static SenseRegister Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return SenseRegister.Neutral;
        }

        string normalized = value.ToLowerInvariant();
        SenseRegister strongest = SenseRegister.Neutral;
        foreach ((string marker, SenseRegister register) in Markers)
        {
            if (normalized.Contains(marker, StringComparison.Ordinal) &&
                register > strongest)
            {
                strongest = register;
            }
        }

        return strongest;
    }

    /// <summary>
    /// Distance used to keep a chosen sense's bluntness. Zero means the surface
    /// carries the sense's own register. Softening and strengthening are both
    /// penalized: the card forbids either direction, so this is symmetric and
    /// the "no unjustified strengthening" gate is measured separately.
    /// </summary>
    internal static int MatchRank(SenseRegister expected, SenseRegister candidate) =>
        expected == candidate ? 0 : Math.Abs((int)expected - (int)candidate);

    // Ordered so that a substring cannot shadow a longer marker; matching is
    // by containment because the column packs several markers into one string.
    private static readonly (string Marker, SenseRegister Register)[] Markers =
    [
        ("груб", SenseRegister.Vulgar),
        ("вульгар", SenseRegister.Vulgar),
        ("бран", SenseRegister.Vulgar),
        ("табу", SenseRegister.Vulgar),
        ("vulgar", SenseRegister.Vulgar),
        ("offensive", SenseRegister.Vulgar),
        ("obscene", SenseRegister.Vulgar),
        ("секс", SenseRegister.SexualDomain),
        ("эротич", SenseRegister.SexualDomain),
        ("sexual", SenseRegister.SexualDomain),
        ("erotic", SenseRegister.SexualDomain),
        ("жарг", SenseRegister.Slang),
        ("сленг", SenseRegister.Slang),
        ("slang", SenseRegister.Slang),
        ("разг", SenseRegister.Colloquial),
        ("прост", SenseRegister.Colloquial),
        ("colloq", SenseRegister.Colloquial),
        ("informal", SenseRegister.Colloquial),
        ("диал", SenseRegister.Dialect),
        ("dialect", SenseRegister.Dialect),
        ("устар", SenseRegister.Archaic),
        ("архаич", SenseRegister.Archaic),
        ("archaic", SenseRegister.Archaic),
        ("obsolete", SenseRegister.Archaic),
        ("лит", SenseRegister.Literary),
        ("literary", SenseRegister.Literary),
        ("poetic", SenseRegister.Literary)
    ];
}
