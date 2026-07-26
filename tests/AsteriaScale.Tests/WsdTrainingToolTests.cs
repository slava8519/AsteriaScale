using System.Diagnostics;
using System.Text.Json;

internal static class WsdTrainingToolTests
{
    public static async Task RunAsync(string tempRoot)
    {
        string repositoryRoot = FindRepositoryRoot();
        string root = Path.Combine(tempRoot, "wsd-training-tool");
        Directory.CreateDirectory(root);

        string source = Path.Combine(root, "source.jsonl");
        string devSet = Path.Combine(root, "dev.json");
        string sidecar = Path.Combine(root, "missing-sidecar.tsv.gz");
        string output = Path.Combine(root, "training.jsonl");
        await File.WriteAllTextAsync(
            devSet,
            """
            {"cases":[{"target":"beaming"}]}
            """);
        await File.WriteAllLinesAsync(
            source,
            [
                """
                {"word":"beam","pos":"verb","forms":[{"form":"beaming"}],"senses":[{"glosses":["to emit rays of light"],"synonyms":[{"word":"shine"}],"examples":[{"text":"She was beaming throughout the entire ceremony."}]},{"glosses":["a long structural support"],"synonyms":[{"word":"girder"}],"examples":[{"text":"The workers lifted the beam before sunrise."}]}]}
                """,
                """
                {"word":"bank","pos":"noun","senses":[{"glosses":["Money."],"synonyms":[{"word":"cash"},{"word":"dough"}],"examples":[{"text":"They hid the bank cash before dawn."}]},{"glosses":["sloping land alongside a river or a lake"],"synonyms":[{"word":"riverside"},{"word":"embankment"}],"examples":[{"text":"They rested on the bank after dusk."}]}]}
                """
            ]);

        ProcessResult build = await RunToolAsync(
            repositoryRoot,
            "--source", source,
            "--sidecar", sidecar,
            "--exclude", devSet,
            "--output", output,
            "--skip-source-digest");
        Check(build.ExitCode == 0,
            $"The WSD training fixture failed: {build.StandardError}{build.StandardOutput}");

        JsonElement[] groups = (await File.ReadAllLinesAsync(output))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(ParseElement)
            .ToArray();
        Check(groups.Length > 0, "The WSD training fixture produced no groups.");
        Check(groups.All(group => group.GetProperty("lemma").GetString() != "beam"),
            "A dev target's inflected surface excluded only itself, not its head lemma.");

        JsonElement[] bankPairs = groups
            .Where(group => group.GetProperty("lemma").GetString() == "bank")
            .SelectMany(group => group.GetProperty("pairs").EnumerateArray())
            .ToArray();
        JsonElement[] tersePairs = bankPairs
            .Where(pair => pair.GetProperty("text_b").GetString()!.Contains(
                "Money.",
                StringComparison.Ordinal))
            .ToArray();
        JsonElement[] detailedPairs = bankPairs
            .Where(pair => pair.GetProperty("text_b").GetString()!.Contains(
                "sloping land",
                StringComparison.Ordinal))
            .ToArray();
        Check(tersePairs.Length > 0 && tersePairs.All(pair =>
                pair.GetProperty("text_b_enriched").GetString() !=
                pair.GetProperty("text_b").GetString()),
            "A group-relative terse gloss did not receive its non-leaking enrichment.");
        Check(detailedPairs.Length > 0 && detailedPairs.All(pair =>
                pair.GetProperty("text_b_enriched").GetString() ==
                pair.GetProperty("text_b").GetString()),
            "An already-detailed gloss was enriched contrary to the production group rule.");

        string malformed = Path.Combine(root, "malformed.jsonl");
        await File.WriteAllLinesAsync(
            malformed,
            [
                "{\"word\":\"valid\",\"senses\":[]}",
                "{broken"
            ]);
        ProcessResult malformedResult = await RunToolAsync(
            repositoryRoot,
            "--source", malformed,
            "--sidecar", sidecar,
            "--exclude", devSet,
            "--output", Path.Combine(root, "malformed-output.jsonl"),
            "--skip-source-digest");
        string malformedDiagnostic = malformedResult.StandardError + malformedResult.StandardOutput;
        Check(malformedResult.ExitCode != 0 &&
              malformedDiagnostic.Contains($"{malformed}:2", StringComparison.Ordinal) &&
              malformedDiagnostic.Contains("invalid JSONL", StringComparison.Ordinal),
            $"Malformed JSONL was skipped or failed without a useful location: {malformedDiagnostic}");

        // The path is deliberately nonexistent: rejection must happen from its
        // name before any attempt to open a frozen holdout.
        string forbidden = Path.Combine(root, "ranker-holdout-never-open.json");
        ProcessResult holdoutResult = await RunToolAsync(
            repositoryRoot,
            "--source", source,
            "--sidecar", sidecar,
            "--exclude", forbidden,
            "--output", Path.Combine(root, "forbidden-output.jsonl"),
            "--skip-source-digest");
        string holdoutDiagnostic = holdoutResult.StandardError + holdoutResult.StandardOutput;
        Check(holdoutResult.ExitCode != 0 &&
              holdoutDiagnostic.Contains("refusing to read frozen holdouts", StringComparison.Ordinal),
            "The training tool did not reject a frozen holdout before opening it.");
    }

    private static string FindRepositoryRoot()
    {
        string? current = AppContext.BaseDirectory;
        while (current is not null &&
               !File.Exists(Path.Combine(current, "AsteriaScale.slnx")))
        {
            current = Path.GetDirectoryName(current);
        }

        return current ?? throw new InvalidOperationException(
            "Repository root was not found above the test binary.");
    }

    private static JsonElement ParseElement(string line)
    {
        using JsonDocument document = JsonDocument.Parse(line);
        return document.RootElement.Clone();
    }

    private static async Task<ProcessResult> RunToolAsync(
        string repositoryRoot,
        params string[] arguments)
    {
        ProcessStartInfo startInfo = new("python")
        {
            WorkingDirectory = repositoryRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-B");
        startInfo.ArgumentList.Add(Path.Combine(
            repositoryRoot,
            "tools",
            "build-wsd-training-set.py"));
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = PythonTestProcess.Start(startInfo);
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw new TimeoutException("The WSD training tool exceeded 30 seconds.");
        }

        return new ProcessResult(
            process.ExitCode,
            await standardOutput,
            await standardError);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed record ProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}
