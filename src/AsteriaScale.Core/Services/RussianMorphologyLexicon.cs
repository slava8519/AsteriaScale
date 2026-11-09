using System.IO.Compression;
using System.Text;

namespace AsteriaScale.Services;

internal enum RussianVerbFormKind
{
    Infinitive,
    PastMasculine,
    PastFeminine,
    PastNeuter,
    PastPlural
}

internal enum RussianAspect
{
    Unknown,
    Perfective,
    Imperfective,
    Both
}

internal readonly record struct RussianVerbFormInfo(
    RussianVerbFormKind Kind,
    RussianAspect Aspect,
    string Lemma);

/// <summary>
/// OpenCorpora-backed verb lexicon (infinitives and past-tense forms only —
/// the categories the runtime morphology consults). Loaded lazily from
/// <see cref="CoreDataLayout.MorphologyFile"/> (Morphology/ru-morph.tsv.gz
/// beside the executable on Windows). A missing or damaged
/// data file, or an unknown word, must leave the suffix fallback in
/// <see cref="RussianMorphology"/> fully in charge.
/// </summary>
internal static class RussianMorphologyLexicon
{
    private static readonly Lazy<Dictionary<string, RussianVerbFormInfo>> Entries =
        new(Load, isThreadSafe: true);

    public static bool TryGet(string normalizedWord, out RussianVerbFormInfo info) =>
        Entries.Value.TryGetValue(normalizedWord, out info);

    internal static int EntryCount => Entries.Value.Count;

    private static Dictionary<string, RussianVerbFormInfo> Load()
    {
        Dictionary<string, RussianVerbFormInfo> entries = new(StringComparer.Ordinal);
        try
        {
            // Only the bundled half is read, which resolves even when the
            // user half does not, so this Lazy never caches a user-root error.
            string path = CoreDataLayout.Current.MorphologyFile;
            if (!File.Exists(path))
            {
                return entries;
            }

            using FileStream file = File.OpenRead(path);
            using GZipStream gzip = new(file, CompressionMode.Decompress);
            using StreamReader reader = new(gzip, Encoding.UTF8);
            while (reader.ReadLine() is { } line)
            {
                string[] parts = line.Split('\t');
                if (parts.Length != 4 || parts[0].Length == 0 || parts[3].Length == 0)
                {
                    continue;
                }

                RussianVerbFormKind kind;
                switch (parts[1])
                {
                    case "i": kind = RussianVerbFormKind.Infinitive; break;
                    case "m": kind = RussianVerbFormKind.PastMasculine; break;
                    case "f": kind = RussianVerbFormKind.PastFeminine; break;
                    case "n": kind = RussianVerbFormKind.PastNeuter; break;
                    case "p": kind = RussianVerbFormKind.PastPlural; break;
                    default: continue;
                }

                RussianAspect aspect = parts[2] switch
                {
                    "P" => RussianAspect.Perfective,
                    "I" => RussianAspect.Imperfective,
                    "B" => RussianAspect.Both,
                    _ => RussianAspect.Unknown
                };
                entries[parts[0]] = new RussianVerbFormInfo(kind, aspect, parts[3]);
            }
        }
        catch (Exception exception) when (exception is IOException or
                                          InvalidDataException or
                                          UnauthorizedAccessException)
        {
            // Damaged optional data must not disable the suffix fallback.
            entries.Clear();
        }

        return entries;
    }
}
