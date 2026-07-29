using AsteriaScale.Models;
using AsteriaScale.Services;

internal static class SemanticRankerColdStartTests
{
    public static async Task RunAsync()
    {
        AppSettings settings = new()
        {
            HoverTranslationMode = HoverTranslationModes.Screen,
            SmartDictionaryRankerModel = SmartDictionaryRankerModels.Sandwich,
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

        bool translated = await applicator.RunAfterApplyAsync(
            cancellationToken =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                events.Add("screen");
                Check(activeProfile == RankerCpuModelProfile.Sandwich,
                    "Screen translation ran before the persisted SANDWiCH profile was applied.");
                return Task.FromResult(true);
            });

        Check(translated && events.SequenceEqual(["configure:Sandwich", "screen"]),
            "Cold Screen startup did not configure SANDWiCH before the full-text scaffold.");
        Check(settings.SmartDictionaryBackend == SmartDictionaryBackends.Npu,
            "Applying the semantic-ranker profile silently changed the requested provider.");

        settings.SmartDictionaryRankerModel = SmartDictionaryRankerModels.Consec;
        Check(
            SemanticRankerProfileResolver.FromSettings(settings) ==
                RankerCpuModelProfile.Consec,
            "The persisted ConSeC profile did not resolve to the independent CPU runtime.");

        bool operationStarted = false;
        SemanticRankerProfileApplicator failingApplicator = new(
            settings,
            (_, _) => Task.FromException(
                new InvalidOperationException("profile configuration failed")));
        await CheckThrowsAsync<InvalidOperationException>(() =>
            failingApplicator.RunAfterApplyAsync(
                _ =>
                {
                    operationStarted = true;
                    return Task.FromResult(true);
                }));
        Check(!operationStarted,
            "Screen translation continued after semantic-ranker profile configuration failed.");
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
