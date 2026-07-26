using System.Diagnostics;

internal static class AtomicOutputSafetyTests
{
    public static async Task RunAsync(string tempRoot)
    {
        string repositoryRoot = FindRepositoryRoot();
        string root = Path.Combine(tempRoot, "atomic-output-safety");
        Directory.CreateDirectory(root);
        string destination = Path.Combine(root, "eng-senses.vec");
        await File.WriteAllTextAsync(destination, "old-index");

        const string program =
            """
            import pathlib
            import sys

            sys.path.insert(0, sys.argv[1])
            from atomic_output import (
                atomic_output_path,
                atomic_verified_copy,
                ensure_distinct_paths,
            )

            destination = pathlib.Path(sys.argv[2])
            try:
                with atomic_output_path(destination) as pending:
                    pending.write_bytes(b"partial-index")
                    raise RuntimeError("simulated generation failure")
            except RuntimeError:
                pass

            if destination.read_bytes() != b"old-index":
                raise SystemExit("a failed generation replaced the working index")

            with atomic_output_path(destination) as pending:
                pending.write_bytes(b"complete-index")

            if destination.read_bytes() != b"complete-index":
                raise SystemExit("a completed generation was not published")
            if list(destination.parent.glob(f".{destination.name}.*.tmp")):
                raise SystemExit("atomic output left temporary files behind")

            dependency = destination.parent / "tokenizer.json"
            dependency.write_bytes(b"old-tokenizer")
            source = destination.parent / "downloaded-tokenizer.json"
            source.write_bytes(b"new-tokenizer")
            try:
                atomic_verified_copy(
                    source,
                    dependency,
                    lambda path: (_ for _ in ()).throw(
                        RuntimeError("simulated digest mismatch")
                    ),
                )
            except RuntimeError:
                pass
            if dependency.read_bytes() != b"old-tokenizer":
                raise SystemExit("failed verification replaced a live dependency")

            for aliases in (
                {
                    "sense source": destination,
                    "requested output": destination,
                },
                {
                    "requested output": dependency,
                    "bundle tokenizer": dependency,
                },
            ):
                try:
                    ensure_distinct_paths(aliases)
                except ValueError:
                    continue
                raise SystemExit("an unsafe artifact path collision was accepted")
            """;

        ProcessStartInfo startInfo = new("python")
        {
            WorkingDirectory = repositoryRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-B");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(program);
        startInfo.ArgumentList.Add(Path.Combine(repositoryRoot, "tools"));
        startInfo.ArgumentList.Add(destination);

        using Process process = PythonTestProcess.Start(startInfo);
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
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

            throw new TimeoutException("The atomic-output regression test timed out.");
        }

        string outputText = await output;
        string errorText = await error;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Atomic-output regression failed: {errorText}{outputText}");
        }
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
            "Repository root was not found for atomic-output safety tests.");
    }
}
