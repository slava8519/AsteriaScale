using AsteriaScale.RankerWorker;

internal static class RankerBenchmarkProvenanceTests
{
    public static async Task RunAsync(string tempRoot)
    {
        string goldenPath = RankerBenchmarkProvenance.ResolveConsecReferenceGoldenPath(
            AppContext.BaseDirectory);
        BenchmarkArtifactRecord golden =
            await RankerBenchmarkProvenance.DescribePinnedConsecReferenceGoldenAsync(
                goldenPath);

        Check(
            golden.Name == "Consec/Reference/" +
                ConsecArtifactBundle.OfficialReferenceGoldenFileName &&
            golden.Bytes == ConsecArtifactBundle.OfficialReferenceGoldenLength &&
            golden.Sha256.Equals(
                ConsecArtifactBundle.OfficialReferenceGoldenSha256,
                StringComparison.OrdinalIgnoreCase),
            "The copied official ConSeC golden was not included with its exact pinned identity.");

        List<BenchmarkArtifactRecord> completeArtifacts = CreatePinnedArtifactRecords();
        completeArtifacts.Add(golden);
        RankerBenchmarkProvenance.ValidatePinnedConsecArtifacts(completeArtifacts);

        await ExpectInvalidDataAsync(
            () => Task.Run(() =>
                RankerBenchmarkProvenance.ValidatePinnedConsecArtifacts(
                    completeArtifacts.Where(item => item != golden).ToArray())),
            "ConSeC provenance accepted a report without the official reference golden.");

        string tamperedPath = Path.Combine(
            tempRoot,
            "tampered-" + ConsecArtifactBundle.OfficialReferenceGoldenFileName);
        byte[] tampered = await File.ReadAllBytesAsync(goldenPath);
        tampered[^1] ^= 0x01;
        await File.WriteAllBytesAsync(tamperedPath, tampered);
        await ExpectInvalidDataAsync(
            () => RankerBenchmarkProvenance.DescribePinnedConsecReferenceGoldenAsync(
                tamperedPath),
            "ConSeC provenance accepted a tampered official reference golden.");
    }

    private static List<BenchmarkArtifactRecord> CreatePinnedArtifactRecords() =>
    [
        new(
            "Consec/model-manifest.json",
            ConsecArtifactBundle.ManifestLength,
            ConsecArtifactBundle.ManifestSha256),
        new(
            "Consec/tokenizer.json",
            ConsecArtifactBundle.TokenizerLength,
            ConsecArtifactBundle.TokenizerSha256),
        new(
            "Consec/Cpu/consec-wngt-fp32.onnx",
            ConsecArtifactBundle.GraphLength,
            ConsecArtifactBundle.GraphSha256),
        new(
            "Consec/Cpu/consec-wngt-fp32.onnx.data",
            ConsecArtifactBundle.ExternalDataLength,
            ConsecArtifactBundle.ExternalDataSha256)
    ];

    private static async Task ExpectInvalidDataAsync(
        Func<Task> action,
        string failureMessage)
    {
        try
        {
            await action();
        }
        catch (InvalidDataException)
        {
            return;
        }

        throw new InvalidOperationException(failureMessage);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
