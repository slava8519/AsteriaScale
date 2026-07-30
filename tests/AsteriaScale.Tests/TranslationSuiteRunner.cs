using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AsteriaScale.Models;
using AsteriaScale.RankerProtocol;
using AsteriaScale.Services;

internal static class TranslationSuiteRunner
{
    internal const int OutputSchemaVersion = 1;
    internal const int ReportSchemaVersion = 1;
    internal const string EvaluationScope = "offline-en-ru-generation";

    private static readonly JsonSerializerOptions OutputJsonOptions = new(
        JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
        }
    };

    private static readonly JsonSerializerOptions ReportJsonOptions = new(
        JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
        }
    };

    internal static async Task<TranslationSuiteReport> RunAsync(
        TranslationSuiteOptions options,
        Func<TranslationSuiteOptions, ITranslationSuiteSystem>? systemFactory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        RejectFrozenHoldout(options.DatasetPath);
        if (!File.Exists(options.DatasetPath))
        {
            throw new FileNotFoundException(
                "Translation-suite dataset was not found.",
                options.DatasetPath);
        }

        if (!Directory.Exists(options.WorkerBaseDirectory))
        {
            throw new DirectoryNotFoundException(
                $"Worker base directory was not found: '{options.WorkerBaseDirectory}'.");
        }

        TranslationSuiteDatasetSnapshot datasetSnapshot =
            await LoadDatasetSnapshotAsync(options.DatasetPath, cancellationToken);
        IReadOnlyList<TranslationSuiteCase> dataset = datasetSnapshot.Cases;
        IReadOnlyList<TranslationSuiteCase> selected = SelectCases(dataset, options);
        string selectionSha256 = CreateSelectionSha256(
            selected,
            datasetSnapshot.Artifact.Sha256);

        await using ITranslationSuiteSystem system =
            (systemFactory ?? CreateProductionSystem)(options);
        await system.PrepareAsync(cancellationToken);
        TranslationSuiteSystemProvenance systemProvenance =
            await system.CaptureProvenanceAsync(options.DatasetPath, cancellationToken);

        var outputs = new List<TranslationSuiteCaseOutput>(selected.Count);
        foreach (TranslationSuiteCase benchmarkCase in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Stopwatch timer = Stopwatch.StartNew();
            TranslationSuiteSystemResult result;
            try
            {
                result = await system.TranslateAsync(benchmarkCase, cancellationToken);
                result = ValidateSystemResult(result);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                result = new TranslationSuiteSystemResult(
                    TranslationSuiteCaseStatus.Error,
                    Hypothesis: string.Empty,
                    FromCache: false,
                    Focus: benchmarkCase.Focus,
                    ErrorCode: ResolveErrorCode(exception),
                    ErrorMessage: LimitError(exception.Message));
            }
            finally
            {
                timer.Stop();
            }

            outputs.Add(new TranslationSuiteCaseOutput(
                OutputSchemaVersion,
                benchmarkCase.Id,
                benchmarkCase.SourceSha256,
                selectionSha256,
                result.Hypothesis,
                result.Status,
                timer.ElapsedMilliseconds,
                result.ErrorCode,
                result.ErrorMessage,
                result.Focus,
                result.FromCache,
                options.SystemId,
                options.ProfileId,
                options.PhraseBackendId,
                options.OutputRankerCpuModelId,
                result.Engine,
                systemProvenance.TranslationModelIdentity,
                systemProvenance.TranslationPromptIdentity,
                result.Metadata));
        }

        ValidateDatasetOutputMatch(selected, outputs, selectionSha256);
        TranslationSuiteReport report = new(
            ReportSchemaVersion,
            DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            EvaluationScope,
            options.SystemId,
            options.ProfileId,
            options.PhraseBackendId,
            options.OutputRankerCpuModelId,
            dataset.Count,
            options.MaximumCases,
            options.ShardIndex,
            options.ShardCount,
            selectionSha256,
            selected.Select(item => item.Id).ToArray(),
            datasetSnapshot.Artifact,
            systemProvenance,
            CalculateSummary(outputs),
            outputs);

        await WriteOutputsAtomicallyAsync(
            options.TranslationOutputPath,
            outputs,
            cancellationToken);
        await WriteJsonAtomicallyAsync(
            options.TranslationReportPath,
            JsonSerializer.Serialize(report, ReportJsonOptions),
            cancellationToken);
        PrintSummary(report, options);
        return report;
    }

    internal static async Task<IReadOnlyList<TranslationSuiteCase>> LoadDatasetAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        TranslationSuiteDatasetSnapshot snapshot =
            await LoadDatasetSnapshotAsync(path, cancellationToken);
        return snapshot.Cases;
    }

    internal static async Task<TranslationSuiteDatasetSnapshot> LoadDatasetSnapshotAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var cases = new List<TranslationSuiteCase>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        long datasetBytes = stream.Length;
        using (var reader = new StreamReader(
                   stream,
                   new UTF8Encoding(
                       encoderShouldEmitUTF8Identifier: false,
                       throwOnInvalidBytes: true),
                   detectEncodingFromByteOrderMarks: true,
                   bufferSize: 64 * 1024,
                   leaveOpen: true))
        {
            int lineNumber = 0;
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                lineNumber++;
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    TranslationSuiteCase benchmarkCase = ParseCase(line);
                    if (!ids.Add(benchmarkCase.Id))
                    {
                        throw new InvalidDataException(
                            $"Duplicate translation-suite id '{benchmarkCase.Id}'.");
                    }

                    cases.Add(benchmarkCase);
                }
                catch (Exception exception) when (
                    exception is JsonException or InvalidDataException or FormatException)
                {
                    throw new InvalidDataException(
                        $"Invalid translation-suite JSONL line {lineNumber}: {exception.Message}",
                        exception);
                }
            }
        }

        if (cases.Count == 0)
        {
            throw new InvalidDataException(
                "Translation-suite dataset contains no cases.");
        }

        stream.Position = 0;
        string datasetSha256 = Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken));
        return new TranslationSuiteDatasetSnapshot(
            cases,
            new BenchmarkArtifactRecord(
                Path.GetFileName(path),
                datasetBytes,
                datasetSha256));
    }

    internal static IReadOnlyList<TranslationSuiteCase> SelectCases(
        IReadOnlyList<TranslationSuiteCase> dataset,
        TranslationSuiteOptions options)
    {
        TranslationSuiteCase[] selected = dataset
            .Where((_, index) => index % options.ShardCount == options.ShardIndex - 1)
            .Take(options.MaximumCases ?? int.MaxValue)
            .ToArray();
        if (selected.Length == 0)
        {
            throw new InvalidOperationException(
                $"Shard {options.ShardIndex}/{options.ShardCount} selected no cases.");
        }

        return selected;
    }

    internal static string CreateSelectionSha256(
        IReadOnlyList<TranslationSuiteCase> selected,
        string datasetSha256)
    {
        if (datasetSha256.Length != 64 ||
            datasetSha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException("Dataset artifact has an invalid SHA-256.");
        }

        var builder = new StringBuilder("translation-suite-selection-v2\n");
        builder.Append("dataset\t")
            .Append(datasetSha256.ToLowerInvariant())
            .Append('\n');
        foreach (TranslationSuiteCase benchmarkCase in selected)
        {
            builder.Append(benchmarkCase.Id)
                .Append('\t')
                .Append(benchmarkCase.SourceSha256)
                .Append('\n');
        }

        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
            .ToLowerInvariant();
    }

    internal static void ValidateDatasetOutputMatch(
        IReadOnlyList<TranslationSuiteCase> selected,
        IReadOnlyList<TranslationSuiteCaseOutput> outputs,
        string selectionSha256)
    {
        if (selected.Count != outputs.Count)
        {
            throw new InvalidDataException(
                $"Translation output contains {outputs.Count} rows for {selected.Count} selected cases.");
        }

        var outputById = new Dictionary<string, TranslationSuiteCaseOutput>(
            StringComparer.Ordinal);
        foreach (TranslationSuiteCaseOutput output in outputs)
        {
            if (!outputById.TryAdd(output.Id, output))
            {
                throw new InvalidDataException(
                    $"Translation output repeats id '{output.Id}'.");
            }

            if (!string.Equals(
                    output.SelectionSha256,
                    selectionSha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Translation output '{output.Id}' has a mismatched selection hash.");
            }
        }

        foreach (TranslationSuiteCase benchmarkCase in selected)
        {
            if (!outputById.TryGetValue(
                    benchmarkCase.Id,
                    out TranslationSuiteCaseOutput? output))
            {
                throw new InvalidDataException(
                    $"Translation output is missing id '{benchmarkCase.Id}'.");
            }

            if (!string.Equals(
                    output.SourceSha256,
                    benchmarkCase.SourceSha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Translation output '{benchmarkCase.Id}' belongs to a different source.");
            }
        }
    }

    internal static TranslationSuiteFocus ResolveFocus(TranslationSuiteCase benchmarkCase)
    {
        if (benchmarkCase.Focus is { } annotated)
        {
            return annotated;
        }

        Match[] words = EnglishContextLexicon.TokenRegex()
            .Matches(benchmarkCase.Source)
            .Cast<Match>()
            .ToArray();
        Match? selected = words.FirstOrDefault(match =>
        {
            string token = EnglishContextLexicon.NormalizeToken(match.Value);
            return token.Length > 1 &&
                   !EnglishContextLexicon.DefinitionStopWords.Contains(token);
        }) ?? words.FirstOrDefault();
        if (selected is null)
        {
            throw new InvalidDataException(
                $"Translation-suite case '{benchmarkCase.Id}' has no English focus token.");
        }

        return new TranslationSuiteFocus(selected.Value, selected.Index, Annotated: false);
    }

    internal static bool IsWithinModelGuard(TranslationSuiteCase benchmarkCase)
    {
        long contextCharacters = benchmarkCase.PreviousContext.Sum(
            value => (long)value.Length);
        return IsSourceWithinModelGuard(benchmarkCase) &&
               benchmarkCase.PreviousContext.Count <= TextHookService.MaximumContextSentences &&
               contextCharacters <=
                   TextHookService.MaximumContextCharacters;
    }

    internal static bool IsSourceWithinModelGuard(TranslationSuiteCase benchmarkCase)
    {
        int words = EnglishContextLexicon.TokenRegex().Matches(benchmarkCase.Source).Count;
        return benchmarkCase.Source.Length <= SmartPhraseContextBuilder.MaximumCharacters &&
               words is >= 1 and <= SmartPhraseContextBuilder.MaximumWords;
    }

    internal static void RejectFrozenHoldout(string path)
    {
        if (Path.GetFileName(path).Contains("holdout", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Translation suite refuses to open a frozen holdout dataset.");
        }
    }

    private static ITranslationSuiteSystem CreateProductionSystem(
        TranslationSuiteOptions options) => options.Profile switch
        {
            TranslationSuiteProfile.Direct => new DirectTranslationSuiteSystem(options),
            TranslationSuiteProfile.Smart => new SmartTranslationSuiteSystem(options),
            _ => throw new InvalidOperationException("Unsupported translation-suite profile.")
        };

    private static TranslationSuiteCase ParseCase(string line)
    {
        using JsonDocument document = JsonDocument.Parse(
            line,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32
            });
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Each JSONL row must be an object.");
        }

        RequireOnlyProperties(
            root,
            [
                "schemaVersion", "id", "source", "sourceSha256",
                "sourceLanguage", "targetLanguage", "references", "referenceFree",
                "previousContext", "focus", "speaker", "termAnchors",
                "contrastReferences", "contrastSetId", "domain", "tags",
                "documentId", "segmentIndex", "license", "baselineTranslations",
                "metadata"
            ],
            "case");
        if (root.TryGetProperty("schemaVersion", out JsonElement schemaVersion) &&
            (!schemaVersion.TryGetInt32(out int parsedSchemaVersion) ||
             parsedSchemaVersion != OutputSchemaVersion))
        {
            throw new InvalidDataException(
                $"Unsupported translation-suite schemaVersion '{schemaVersion}'.");
        }

        string id = ReadRequiredString(root, "id", "case");
        if (id.Length > 256 || id.Any(char.IsControl))
        {
            throw new InvalidDataException("Case id is too long or contains controls.");
        }

        string source = ReadRequiredString(root, "source", id);
        if (source.Length > 100_000 || HasUnsafeControls(source))
        {
            throw new InvalidDataException(
                $"Case '{id}' source is too large or contains unsafe controls.");
        }

        string sourceSha256 = ComputeSourceSha256(source);
        if (root.TryGetProperty("sourceSha256", out JsonElement suppliedHash))
        {
            if (suppliedHash.ValueKind != JsonValueKind.String ||
                !string.Equals(
                    suppliedHash.GetString(),
                    sourceSha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Case '{id}' sourceSha256 does not match its exact UTF-8 source.");
            }
        }

        IReadOnlyList<string> references = ReadStringArray(
            root,
            "references",
            id,
            required: true);
        bool referenceFree = ReadOptionalBoolean(root, "referenceFree", id);
        if (!referenceFree && references.Count == 0)
        {
            throw new InvalidDataException(
                $"Case '{id}' has no references and is not marked referenceFree.");
        }

        if (references.Any(value => HasUnsafeControls(value)) ||
            references.Distinct(StringComparer.Ordinal).Count() != references.Count)
        {
            throw new InvalidDataException(
                $"Case '{id}' has unsafe or duplicate references.");
        }

        ValidateLanguage(root, "sourceLanguage", "en", id);
        ValidateLanguage(root, "targetLanguage", "ru", id);

        IReadOnlyList<string> previousContext = ReadStringArray(
            root,
            "previousContext",
            id,
            required: false);
        if (previousContext.Any(value => HasUnsafeControls(value)))
        {
            throw new InvalidDataException(
                $"Case '{id}' has unsafe previousContext.");
        }

        IReadOnlyList<string> contrastReferences = ReadStringArray(
            root,
            "contrastReferences",
            id,
            required: false);
        if (contrastReferences.Count > 0 && references.Count == 0)
        {
            throw new InvalidDataException(
                $"Case '{id}' contrastReferences require at least one reference.");
        }

        if (contrastReferences.Any(value => HasUnsafeControls(value)))
        {
            throw new InvalidDataException(
                $"Case '{id}' has unsafe contrastReferences.");
        }

        IReadOnlyList<string> tags = ReadStringArray(
            root,
            "tags",
            id,
            required: false);
        if (tags.Any(value => HasUnsafeControls(value)))
        {
            throw new InvalidDataException($"Case '{id}' has unsafe tags.");
        }

        ValidateOptionalNonEmptyString(root, "contrastSetId", id);
        ValidateOptionalNonEmptyString(root, "domain", id);
        ValidateOptionalNonEmptyString(root, "documentId", id);
        ValidateOptionalSegmentIndex(root, id);
        ValidateTermAnchors(root, id);
        ValidateBaselineTranslations(root, id);
        ValidateLicense(root, id);

        TranslationSuiteFocus? focus = root.TryGetProperty(
            "focus",
            out JsonElement focusElement)
            ? ParseFocus(focusElement, id, source)
            : null;
        TranslationSuiteSpeaker? speaker = root.TryGetProperty(
            "speaker",
            out JsonElement speakerElement)
            ? ParseSpeaker(speakerElement, id)
            : null;
        if (root.TryGetProperty("metadata", out JsonElement metadata) &&
            metadata.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                $"Case '{id}' metadata must be an object.");
        }

        return new TranslationSuiteCase(
            id,
            source,
            sourceSha256,
            references,
            referenceFree,
            previousContext,
            focus,
            speaker);
    }

    private static TranslationSuiteFocus ParseFocus(
        JsonElement element,
        string caseId,
        string source)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"Case '{caseId}' focus must be an object.");
        }

        RequireOnlyProperties(element, ["text", "characterIndex", "annotated"], "focus");
        string text = ReadRequiredString(element, "text", caseId);
        if (!element.TryGetProperty("characterIndex", out JsonElement indexElement) ||
            !indexElement.TryGetInt32(out int characterIndex) || characterIndex < 0 ||
            characterIndex + text.Length > source.Length ||
            !source.AsSpan(characterIndex, text.Length).SequenceEqual(text.AsSpan()))
        {
            throw new InvalidDataException(
                $"Case '{caseId}' focus does not match its exact source span.");
        }

        bool isAnnotated = true;
        if (element.TryGetProperty("annotated", out JsonElement annotated))
        {
            if (annotated.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw new InvalidDataException(
                    $"Case '{caseId}' focus annotated flag must be boolean.");
            }

            isAnnotated = annotated.GetBoolean();
        }

        return new TranslationSuiteFocus(text, characterIndex, isAnnotated);
    }

    private static TranslationSuiteSpeaker ParseSpeaker(
        JsonElement element,
        string caseId)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"Case '{caseId}' speaker must be an object.");
        }

        RequireOnlyProperties(element, ["name", "gender"], "speaker");
        string name = ReadRequiredString(element, "name", caseId);
        if (name.Length > SpeakerMetadataParser.MaximumNameLength ||
            name.Any(char.IsControl))
        {
            throw new InvalidDataException($"Case '{caseId}' has an invalid speaker name.");
        }

        string genderValue = ReadRequiredString(element, "gender", caseId);
        SpeakerGender gender = genderValue switch
        {
            "unknown" => SpeakerGender.Unknown,
            "masculine" => SpeakerGender.Masculine,
            "feminine" => SpeakerGender.Feminine,
            _ => throw new InvalidDataException(
                $"Case '{caseId}' has unknown speaker gender '{genderValue}'.")
        };
        return new TranslationSuiteSpeaker(name, gender);
    }

    private static IReadOnlyList<string> ReadStringArray(
        JsonElement root,
        string propertyName,
        string caseId,
        bool required)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement element))
        {
            if (!required)
            {
                return [];
            }

            throw new InvalidDataException(
                $"Case '{caseId}' is missing '{propertyName}'.");
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                $"Case '{caseId}' property '{propertyName}' must be an array.");
        }

        var values = new List<string>();
        foreach (JsonElement item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(item.GetString()))
            {
                throw new InvalidDataException(
                    $"Case '{caseId}' property '{propertyName}' contains an invalid string.");
            }

            values.Add(item.GetString()!);
        }

        return values;
    }

    private static bool ReadOptionalBoolean(
        JsonElement root,
        string propertyName,
        string caseId)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement element))
        {
            return false;
        }

        if (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidDataException(
                $"Case '{caseId}' property '{propertyName}' must be boolean.");
        }

        return element.GetBoolean();
    }

    private static void ValidateLanguage(
        JsonElement root,
        string propertyName,
        string expected,
        string caseId)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement element))
        {
            return;
        }

        if (element.ValueKind != JsonValueKind.String ||
            !string.Equals(element.GetString(), expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Case '{caseId}' property '{propertyName}' must be '{expected}'.");
        }
    }

    private static void ValidateOptionalNonEmptyString(
        JsonElement root,
        string propertyName,
        string caseId)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement element))
        {
            return;
        }

        if (element.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(element.GetString()) ||
            HasUnsafeControls(element.GetString()!))
        {
            throw new InvalidDataException(
                $"Case '{caseId}' property '{propertyName}' must be a safe non-empty string.");
        }
    }

    private static void ValidateOptionalSegmentIndex(JsonElement root, string caseId)
    {
        if (root.TryGetProperty("segmentIndex", out JsonElement element) &&
            (!element.TryGetInt32(out int segmentIndex) || segmentIndex < 0))
        {
            throw new InvalidDataException(
                $"Case '{caseId}' segmentIndex must be a non-negative integer.");
        }
    }

    private static void ValidateTermAnchors(JsonElement root, string caseId)
    {
        if (!root.TryGetProperty("termAnchors", out JsonElement anchors))
        {
            return;
        }

        if (anchors.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                $"Case '{caseId}' termAnchors must be an array.");
        }

        int index = 0;
        foreach (JsonElement anchor in anchors.EnumerateArray())
        {
            string context = $"termAnchors[{index}]";
            if (anchor.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    $"Case '{caseId}' {context} must be an object.");
            }

            RequireOnlyProperties(
                anchor,
                ["source", "acceptable", "targets", "match", "en", "ru"],
                context);
            bool legacy = anchor.TryGetProperty("en", out _) ||
                          anchor.TryGetProperty("ru", out _);
            if (legacy)
            {
                _ = ReadRequiredString(anchor, "en", $"{caseId}/{context}");
                _ = ReadRequiredString(anchor, "ru", $"{caseId}/{context}");
                if (anchor.TryGetProperty("acceptable", out _) ||
                    anchor.TryGetProperty("targets", out _))
                {
                    throw new InvalidDataException(
                        $"Case '{caseId}' {context} mixes legacy and canonical targets.");
                }
            }
            else
            {
                if (anchor.TryGetProperty("source", out _))
                {
                    _ = ReadRequiredString(anchor, "source", $"{caseId}/{context}");
                }

                string alternativesProperty = anchor.TryGetProperty("acceptable", out _)
                    ? "acceptable"
                    : "targets";
                IReadOnlyList<string> alternatives = ReadStringArray(
                    anchor,
                    alternativesProperty,
                    $"{caseId}/{context}",
                    required: true);
                if (alternatives.Count == 0)
                {
                    throw new InvalidDataException(
                        $"Case '{caseId}' {context} requires at least one target.");
                }
            }

            if (anchor.TryGetProperty("match", out JsonElement match) &&
                (match.ValueKind != JsonValueKind.String ||
                 match.GetString() is not ("token" or "phrase" or "stem")))
            {
                throw new InvalidDataException(
                    $"Case '{caseId}' {context} has an invalid match mode.");
            }

            index++;
        }
    }

    private static void ValidateBaselineTranslations(JsonElement root, string caseId)
    {
        if (!root.TryGetProperty("baselineTranslations", out JsonElement baselines))
        {
            return;
        }

        if (baselines.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                $"Case '{caseId}' baselineTranslations must be an object.");
        }

        foreach (JsonProperty baseline in baselines.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(baseline.Name) ||
                baseline.Value.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException(
                    $"Case '{caseId}' baselineTranslations must map names to strings.");
            }
        }
    }

    private static void ValidateLicense(JsonElement root, string caseId)
    {
        if (root.TryGetProperty("license", out JsonElement license) &&
            license.ValueKind is not (JsonValueKind.String or JsonValueKind.Object))
        {
            throw new InvalidDataException(
                $"Case '{caseId}' license must be a string or object.");
        }
    }

    private static string ReadRequiredString(
        JsonElement root,
        string propertyName,
        string context)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement element) ||
            element.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(element.GetString()))
        {
            throw new InvalidDataException(
                $"'{context}' is missing non-empty string '{propertyName}'.");
        }

        return element.GetString()!;
    }

    private static void RequireOnlyProperties(
        JsonElement element,
        IReadOnlyCollection<string> allowed,
        string context)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                throw new InvalidDataException(
                    $"Duplicate property '{property.Name}' in {context}.");
            }

            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
            {
                throw new InvalidDataException(
                    $"Unknown property '{property.Name}' in {context}.");
            }
        }
    }

    private static string ComputeSourceSha256(string source) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))
            .ToLowerInvariant();

    private static bool HasUnsafeControls(string value) => value.Any(character =>
        char.IsControl(character) && character is not ('\r' or '\n' or '\t'));

    private static TranslationSuiteSystemResult ValidateSystemResult(
        TranslationSuiteSystemResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Status == TranslationSuiteCaseStatus.Ok &&
            string.IsNullOrWhiteSpace(result.Hypothesis))
        {
            throw new InvalidDataException(
                "A translation system returned status=ok with an empty hypothesis.");
        }

        if (result.Status != TranslationSuiteCaseStatus.Ok &&
            !string.IsNullOrEmpty(result.Hypothesis))
        {
            throw new InvalidDataException(
                "A failed translation-system result must not contain a hypothesis.");
        }

        return result;
    }

    private static TranslationSuiteSummary CalculateSummary(
        IReadOnlyList<TranslationSuiteCaseOutput> outputs)
    {
        int ok = outputs.Count(item => item.Status == TranslationSuiteCaseStatus.Ok);
        long[] allLatencies = outputs.Select(item => item.LatencyMilliseconds)
            .OrderBy(value => value)
            .ToArray();
        long[] successfulLatencies = outputs
            .Where(item => item.Status == TranslationSuiteCaseStatus.Ok)
            .Select(item => item.LatencyMilliseconds)
            .OrderBy(value => value)
            .ToArray();
        return new TranslationSuiteSummary(
            outputs.Count,
            ok,
            outputs.Count(item => item.Status == TranslationSuiteCaseStatus.Unavailable),
            outputs.Count(item => item.Status == TranslationSuiteCaseStatus.Error),
            outputs.Count == 0 ? 0 : ok / (double)outputs.Count,
            outputs.Count(item => item.FromCache),
            new TranslationSuiteLatencySummary(
                allLatencies.Length,
                Percentile(allLatencies, 0.50),
                Percentile(allLatencies, 0.95),
                allLatencies.Length == 0 ? null : allLatencies[^1],
                successfulLatencies.Length,
                Percentile(successfulLatencies, 0.50),
                Percentile(successfulLatencies, 0.95)));
    }

    internal static long? Percentile(IReadOnlyList<long> orderedValues, double percentile)
    {
        if (orderedValues.Count == 0)
        {
            return null;
        }

        int index = Math.Max(
            0,
            (int)Math.Ceiling(percentile * orderedValues.Count) - 1);
        return orderedValues[Math.Min(index, orderedValues.Count - 1)];
    }

    private static async Task WriteOutputsAtomicallyAsync(
        string path,
        IReadOnlyList<TranslationSuiteCaseOutput> outputs,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        foreach (TranslationSuiteCaseOutput output in outputs)
        {
            builder.Append(JsonSerializer.Serialize(output, OutputJsonOptions)).Append('\n');
        }

        await WriteJsonAtomicallyAsync(path, builder.ToString(), cancellationToken);
    }

    private static async Task WriteJsonAtomicallyAsync(
        string path,
        string content,
        CancellationToken cancellationToken)
    {
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        string temporaryPath = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                content,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);
            AtomicFile.Replace(temporaryPath, fullPath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string ResolveErrorCode(Exception exception) => exception switch
    {
        GlossBertRankerException ranker => ranker.ErrorCode,
        TimeoutException => "timeout",
        InvalidDataException => "invalid_system_result",
        _ => exception.GetType().Name
    };

    private static string LimitError(string value) => value.Length <= 1_000
        ? value
        : value[..1_000];

    private static void PrintSummary(
        TranslationSuiteReport report,
        TranslationSuiteOptions options)
    {
        Console.WriteLine();
        Console.WriteLine(
            $"Translation suite: {report.SystemId}, {report.Profile}/" +
            $"{report.PhraseBackend}, selection={report.SelectionSha256[..12]}");
        Console.WriteLine(
            $"  cases={report.Summary.Cases}, ok={report.Summary.Ok}, " +
            $"unavailable={report.Summary.Unavailable}, errors={report.Summary.Errors}, " +
            $"coverage={report.Summary.Coverage:P1}");
        Console.WriteLine(
            $"  latency p50={report.Summary.Latency.P50Milliseconds?.ToString() ?? "n/a"} ms, " +
            $"p95={report.Summary.Latency.P95Milliseconds?.ToString() ?? "n/a"} ms");
        Console.WriteLine($"  output: {options.TranslationOutputPath}");
        Console.WriteLine($"  report: {options.TranslationReportPath}");
    }
}

internal sealed class DirectTranslationSuiteSystem : ITranslationSuiteSystem
{
    private readonly TranslationSuiteOptions _options;
    private readonly TranslateGemmaTranslationService _translator;

    internal DirectTranslationSuiteSystem(TranslationSuiteOptions options)
    {
        _options = options;
        _translator = new TranslateGemmaTranslationService(
            backendResolver: () => options.PhraseBackend);
    }

    public Task PrepareAsync(CancellationToken cancellationToken) =>
        _translator.WarmUpAsync(cancellationToken);

    public async Task<TranslationSuiteSystemResult> TranslateAsync(
        TranslationSuiteCase benchmarkCase,
        CancellationToken cancellationToken)
    {
        if (!TranslationSuiteRunner.IsSourceWithinModelGuard(benchmarkCase))
        {
            return GuardUnavailable();
        }

        string translation = await _translator.TranslatePhraseToRussianAsync(
            benchmarkCase.Source,
            cancellationToken);
        return new TranslationSuiteSystemResult(
            TranslationSuiteCaseStatus.Ok,
            translation,
            FromCache: false,
            Focus: null,
            TranslateGemmaTranslationService.GetEngineLabel(_options.PhraseBackend));
    }

    public async Task<TranslationSuiteSystemProvenance> CaptureProvenanceAsync(
        string datasetPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new TranslationSuiteSystemProvenance(
            TranslateGemmaTranslationService.GetModelIdentity(_options.PhraseBackend),
            TranslateGemmaTranslationService.DirectPromptIdentity,
            RankerModelVersion: null,
            RankerProvider: null,
            AbstentionTag: null,
            await DescribeApplicationAsync(cancellationToken),
            await DescribeHarnessAsync(cancellationToken),
            Ranker: null);
    }

    public ValueTask DisposeAsync() => _translator.DisposeAsync();

    private static TranslationSuiteSystemResult GuardUnavailable() => new(
        TranslationSuiteCaseStatus.Unavailable,
        Hypothesis: string.Empty,
        FromCache: false,
        Focus: null,
        ErrorCode: "source_outside_model_guard",
        ErrorMessage: "Source exceeds the pinned TranslateGemma sentence/context budget.");

    internal static Task<BenchmarkArtifactRecord> DescribeApplicationAsync(
        CancellationToken cancellationToken = default) =>
        RankerBenchmarkProvenance.DescribeArtifactAsync(
            typeof(SmartHoverPipeline).Assembly.Location,
            Path.GetFileName(typeof(SmartHoverPipeline).Assembly.Location),
            cancellationToken);

    internal static Task<BenchmarkArtifactRecord> DescribeHarnessAsync(
        CancellationToken cancellationToken = default) =>
        RankerBenchmarkProvenance.DescribeArtifactAsync(
            typeof(TranslationSuiteRunner).Assembly.Location,
            Path.GetFileName(typeof(TranslationSuiteRunner).Assembly.Location),
            cancellationToken);
}

internal sealed class SmartTranslationSuiteSystem : ITranslationSuiteSystem
{
    private readonly TranslationSuiteOptions _options;
    private readonly string _isolatedRoot;
    private readonly LocalDictionaryService _dictionary;
    private readonly GlossBertDictionarySelectorService _selector;
    private readonly SmartDictionaryResultCache _resultCache;
    private readonly GlossBertDecisionCache _decisionCache;
    private readonly SmartPhraseTranslationCache _phraseCache;
    private readonly TranslateGemmaTranslationService _translator;
    private readonly SmartHoverPipeline _pipeline;
    private bool _disposed;

    internal SmartTranslationSuiteSystem(TranslationSuiteOptions options)
    {
        _options = options;
        _isolatedRoot = Path.Combine(
            Path.GetTempPath(),
            "AsteriaScale",
            "translation-suite-" + Guid.NewGuid().ToString("N"));
        string userDictionaryDirectory = Path.Combine(_isolatedRoot, "Dictionaries");
        GlossBertDictionarySelectorService? selector = null;
        SmartDictionaryResultCache? resultCache = null;
        GlossBertDecisionCache? decisionCache = null;
        SmartPhraseTranslationCache? phraseCache = null;
        TranslateGemmaTranslationService? translator = null;
        SmartHoverPipeline? pipeline = null;
        try
        {
            Directory.CreateDirectory(userDictionaryDirectory);
            var dictionary = new LocalDictionaryService(
                Path.Combine(AppContext.BaseDirectory, "Dictionaries"),
                userDictionaryDirectory);
            selector = new GlossBertDictionarySelectorService(
                options.WorkerBaseDirectory,
                TimeSpan.FromMinutes(10),
                options.RankerCpuModel);
            selector.ConfigureKeepWarm(true);
            resultCache = new SmartDictionaryResultCache(
                Path.Combine(_isolatedRoot, "smart-results.json"));
            decisionCache = new GlossBertDecisionCache(
                Path.Combine(_isolatedRoot, "ranker-decisions.json"));
            phraseCache = new SmartPhraseTranslationCache(
                Path.Combine(_isolatedRoot, "phrase-translations.json"));
            translator = new TranslateGemmaTranslationService(
                backendResolver: () => options.PhraseBackend);
            var settings = new AppSettings
            {
                SmartDictionaryBackend = SmartDictionaryBackends.Cpu,
                SmartDictionaryRankerModel = options.RankerCpuModelId,
                SmartDictionarySourceLanguage = SmartDictionarySourceLanguages.English,
                SmartDictionaryKeepWarm = true,
                SmartPhraseTranslationMode = SmartPhraseTranslationModes.TranslateGemma,
                SmartPhraseTranslationBackend = options.PhraseBackendId
            };
            var scaffoldRanker = new TranslationScaffoldGlossBertRanker(
                selector,
                decisionCache,
                () => GlossBertRankerProvider.Cpu,
                () => dictionary.VersionToken);
            pipeline = new SmartHoverPipeline(
                settings,
                dictionary,
                selector,
                resultCache,
                decisionCache,
                static _ => { },
                translator,
                phraseCache,
                scaffoldRanker.RankAsync);

            _dictionary = dictionary;
            _selector = selector;
            _resultCache = resultCache;
            _decisionCache = decisionCache;
            _phraseCache = phraseCache;
            _translator = translator;
            _pipeline = pipeline;
        }
        catch (Exception creationFailure)
        {
            IReadOnlyList<Exception> cleanupFailures =
                DisposeOwnedResourcesBestEffortAsync(
                        pipeline,
                        translator,
                        selector,
                        resultCache,
                        phraseCache,
                        decisionCache)
                    .GetAwaiter()
                    .GetResult();
            DeleteIsolationRootBestEffort(_isolatedRoot);
            ThrowPrimaryAndCleanupFailures(
                creationFailure,
                cleanupFailures,
                "Smart translation benchmark creation failed; cleanup errors follow.");
            throw;
        }
    }

    public async Task PrepareAsync(CancellationToken cancellationToken)
    {
        await Task.WhenAll(
            _selector.WarmUpAsync(GlossBertRankerProvider.Cpu, cancellationToken),
            _translator.WarmUpAsync(cancellationToken));
    }

    public async Task<TranslationSuiteSystemResult> TranslateAsync(
        TranslationSuiteCase benchmarkCase,
        CancellationToken cancellationToken)
    {
        if (!TranslationSuiteRunner.IsWithinModelGuard(benchmarkCase))
        {
            return new TranslationSuiteSystemResult(
                TranslationSuiteCaseStatus.Unavailable,
                Hypothesis: string.Empty,
                FromCache: false,
                Focus: benchmarkCase.Focus,
                ErrorCode: "source_outside_model_guard",
                ErrorMessage: "Source exceeds the pinned Smart Dictionary sentence/context budget.");
        }

        TranslationSuiteFocus focus = TranslationSuiteRunner.ResolveFocus(benchmarkCase);

        CurrentSpeakerContext? speaker = benchmarkCase.Speaker is { } annotatedSpeaker
            ? new CurrentSpeakerContext(
                annotatedSpeaker.Name,
                annotatedSpeaker.Gender,
                GameKey: "translation-suite")
            : null;
        var recognized = new HoverOcrResult(
            benchmarkCase.Source,
            focus.Text,
            focus.CharacterIndex,
            System.Drawing.Rectangle.Empty,
            HoverTextSource.TextHook)
        {
            IsSentenceContextStable = true,
            PreviousContext = benchmarkCase.PreviousContext,
            CurrentSpeaker = speaker
        };
        SmartHoverResult dictionaryResult = await _pipeline.ResolveSmartHoverAsync(
            recognized,
            SmartDictionaryBackends.Cpu,
            cancellationToken);
        SmartHoverPhraseLine? phrase = await _pipeline.TranslatePhraseLineAsync(
            recognized,
            dictionaryResult,
            cancellationToken);
        TranslationSuiteSmartMetadata metadata = CreateSmartMetadata(
            dictionaryResult,
            phrase);
        if (phrase is null || phrase.Status != SmartPhraseLineStatus.Translated ||
            string.IsNullOrWhiteSpace(phrase.Translation))
        {
            return new TranslationSuiteSystemResult(
                TranslationSuiteCaseStatus.Unavailable,
                Hypothesis: string.Empty,
                FromCache: false,
                focus,
                phrase?.Engine,
                ErrorCode: "phrase_translation_unavailable",
                ErrorMessage: "The production Smart Dictionary pipeline did not accept a phrase translation.",
                Metadata: metadata);
        }

        return new TranslationSuiteSystemResult(
            TranslationSuiteCaseStatus.Ok,
            phrase.Translation,
            phrase.FromCache,
            focus,
            phrase.Engine,
            Metadata: metadata);
    }

    internal static TranslationSuiteSmartMetadata CreateSmartMetadata(
        SmartHoverResult dictionaryResult,
        SmartHoverPhraseLine? phrase)
    {
        IReadOnlyList<ScaffoldSenseHint> selectedHints =
            phrase is { Status: SmartPhraseLineStatus.Translated }
                ? phrase.SenseHints
                : dictionaryResult.SelectedSenseHints;
        TranslationSuiteSenseEvidence[] evidence = ProjectSenseEvidence(selectedHints);
        TranslationSuiteSenseEvidence[] appliedEvidence =
            phrase is { Status: SmartPhraseLineStatus.Translated }
                ? ProjectSenseEvidence(phrase.SenseHints)
                : Array.Empty<TranslationSuiteSenseEvidence>();
        string evidenceFingerprint = FingerprintSenseEvidence(evidence);
        string? appliedFingerprint =
            phrase is { Status: SmartPhraseLineStatus.Translated }
                ? FingerprintSenseEvidence(appliedEvidence)
                : null;
        return new TranslationSuiteSmartMetadata(
            dictionaryResult.Engine,
            dictionaryResult.DictionaryFound,
            dictionaryResult.IsPhrase,
            dictionaryResult.Translation,
            evidence,
            evidenceFingerprint,
            appliedEvidence,
            appliedFingerprint);
    }

    private static TranslationSuiteSenseEvidence[] ProjectSenseEvidence(
        IReadOnlyList<ScaffoldSenseHint> senseHints) =>
        senseHints.Select(hint => new TranslationSuiteSenseEvidence(
                hint.Source,
                hint.EnglishDefinition,
                hint.SelectedTranslation))
            .ToArray();

    private static string FingerprintSenseEvidence(
        IReadOnlyList<TranslationSuiteSenseEvidence> evidence)
    {
        var canonical = new StringBuilder("asteria-smart-sense-evidence-v1\n");
        foreach (TranslationSuiteSenseEvidence item in evidence)
        {
            AppendEvidenceField(canonical, item.Source);
            AppendEvidenceField(canonical, item.EnglishDefinition);
            AppendEvidenceField(canonical, item.SelectedTranslation);
        }
        string fingerprint = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
        return fingerprint;
    }

    private static void AppendEvidenceField(StringBuilder builder, string value)
    {
        builder.Append(value.Length.ToString(CultureInfo.InvariantCulture));
        builder.Append(':');
        builder.Append(value);
        builder.Append('\n');
    }

    public async Task<TranslationSuiteSystemProvenance> CaptureProvenanceAsync(
        string datasetPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string modelVersion = _selector.ActiveModelVersion
            ?? throw new InvalidOperationException(
                "Smart translation benchmark has no active ranker model identity.");
        RankerBenchmarkProvenanceData ranker =
            await RankerBenchmarkProvenance.CaptureAsync(
                datasetPath,
                _options.WorkerBaseDirectory,
                GlossBertRankerProvider.Cpu,
                _options.RankerCpuModel,
                modelVersion,
                cancellationToken);
        return new TranslationSuiteSystemProvenance(
            TranslateGemmaTranslationService.GetModelIdentity(_options.PhraseBackend),
            TranslateGemmaTranslationService.PromptIdentity,
            modelVersion,
            GlossBertRankerProvider.Cpu.ToString(),
            RankerAbstention.CacheTag,
            await DirectTranslationSuiteSystem.DescribeApplicationAsync(cancellationToken),
            await DirectTranslationSuiteSystem.DescribeHarnessAsync(cancellationToken),
            ranker);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        IReadOnlyList<Exception> cleanupFailures =
            await DisposeOwnedResourcesBestEffortAsync(
                _pipeline,
                _translator,
                _selector,
                _resultCache,
                _phraseCache,
                _decisionCache);
        DeleteIsolationRootBestEffort(_isolatedRoot);
        ThrowCleanupFailures(
            cleanupFailures,
            "Smart translation benchmark cleanup failed.");
    }

    internal static async Task<IReadOnlyList<Exception>> DisposeStepsBestEffortAsync(
        params Func<ValueTask>[] cleanupSteps)
    {
        ArgumentNullException.ThrowIfNull(cleanupSteps);
        var failures = new List<Exception>();
        foreach (Func<ValueTask> cleanupStep in cleanupSteps)
        {
            ArgumentNullException.ThrowIfNull(cleanupStep);
            try
            {
                await cleanupStep();
            }
            catch (Exception failure)
            {
                failures.Add(failure);
            }
        }

        return failures;
    }

    private static Task<IReadOnlyList<Exception>> DisposeOwnedResourcesBestEffortAsync(
        SmartHoverPipeline? pipeline,
        TranslateGemmaTranslationService? translator,
        GlossBertDictionarySelectorService? selector,
        SmartDictionaryResultCache? resultCache,
        SmartPhraseTranslationCache? phraseCache,
        GlossBertDecisionCache? decisionCache)
    {
        var steps = new List<Func<ValueTask>>();
        if (pipeline is not null)
        {
            steps.Add(() =>
            {
                pipeline.Dispose();
                return ValueTask.CompletedTask;
            });
        }

        if (translator is not null)
        {
            steps.Add(translator.DisposeAsync);
        }

        if (selector is not null)
        {
            steps.Add(selector.DisposeAsync);
        }

        if (resultCache is not null)
        {
            steps.Add(resultCache.DisposeAsync);
        }

        if (phraseCache is not null)
        {
            steps.Add(phraseCache.DisposeAsync);
        }

        if (decisionCache is not null)
        {
            steps.Add(decisionCache.DisposeAsync);
        }

        return DisposeStepsBestEffortAsync([.. steps]);
    }

    private static void DeleteIsolationRootBestEffort(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // The unique root preserves benchmark isolation even when a failed
            // resource cleanup still owns one of its files.
        }
    }

    private static void ThrowCleanupFailures(
        IReadOnlyList<Exception> failures,
        string aggregateMessage)
    {
        if (failures.Count == 0)
        {
            return;
        }

        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }

        throw new AggregateException(aggregateMessage, failures);
    }

    private static void ThrowPrimaryAndCleanupFailures(
        Exception primaryFailure,
        IReadOnlyList<Exception> cleanupFailures,
        string aggregateMessage)
    {
        if (cleanupFailures.Count == 0)
        {
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }

        throw new AggregateException(
            aggregateMessage,
            new[] { primaryFailure }.Concat(cleanupFailures));
    }
}
