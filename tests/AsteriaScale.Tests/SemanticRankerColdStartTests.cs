using AsteriaScale.Models;
using AsteriaScale.Services;

internal static class SemanticRankerColdStartTests
{
    public static async Task RunAsync()
    {
        AppSettings settings = new()
        {
            HoverTranslationMode = HoverTranslationModes.Smart,
            SmartDictionaryRankerModel = SmartDictionaryRankerModels.Consec,
            // An incompatible persisted provider must remain explicit. The
            // profile applicator may not silently rewrite it to CPU.
            SmartDictionaryBackend = SmartDictionaryBackends.Npu
        };
        RankerCpuModelProfile activeProfile = RankerCpuModelProfile.GlossBert;
        List<string> events = [];
        SemanticRankerProfileApplicator applicator = new(
            settings,
            (profile, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                events.Add($"configure:{profile}");
                activeProfile = profile;
                return Task.CompletedTask;
            });

        await applicator.ApplyAsync();
        Check(activeProfile == RankerCpuModelProfile.Consec &&
              events.SequenceEqual(["configure:Consec"]),
            "Cold Smart startup did not apply the persisted ConSeC profile.");
        Check(settings.SmartDictionaryBackend == SmartDictionaryBackends.Npu,
            "Applying the semantic-ranker profile silently changed the requested provider.");

        settings.SmartDictionaryRankerModel = "sandwich";
        Check(
            SemanticRankerProfileResolver.FromSettings(settings) ==
                RankerCpuModelProfile.GlossBert,
            "A persisted removed SANDWiCH profile did not resolve to the GlossBERT default.");

        SemanticRankerProfileApplicator failingApplicator = new(
            settings,
            (_, _) => Task.FromException(
                new InvalidOperationException("profile configuration failed")));
        await CheckThrowsAsync<InvalidOperationException>(() =>
            failingApplicator.ApplyAsync());
    }

    private static async Task CheckThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Expected {typeof(TException).Name}, but the operation completed successfully.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
