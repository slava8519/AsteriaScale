using AsteriaScale.Models;

namespace AsteriaScale.Services;

internal static class SemanticRankerProfileResolver
{
    public static RankerCpuModelProfile FromSettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return SmartDictionaryRankerModels.Normalize(settings.SmartDictionaryRankerModel) switch
        {
            SmartDictionaryRankerModels.Consec => RankerCpuModelProfile.Consec,
            _ => RankerCpuModelProfile.GlossBert
        };
    }

    public static string DisplayName(RankerCpuModelProfile profile) => profile switch
    {
        RankerCpuModelProfile.Consec => "ConSeC",
        _ => "GlossBERT"
    };
}

/// <summary>
/// Applies the persisted semantic-ranker profile before a consumer can submit
/// work. The delegate constructor keeps cold-start ordering testable without
/// launching the isolated model worker.
/// </summary>
internal sealed class SemanticRankerProfileApplicator
{
    private readonly AppSettings _settings;
    private readonly Func<RankerCpuModelProfile, CancellationToken, Task> _configure;

    public SemanticRankerProfileApplicator(
        AppSettings settings,
        GlossBertDictionarySelectorService selector)
        : this(settings, selector.ConfigureCpuModelProfileAsync)
    {
    }

    internal SemanticRankerProfileApplicator(
        AppSettings settings,
        Func<RankerCpuModelProfile, CancellationToken, Task> configure)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(configure);
        _settings = settings;
        _configure = configure;
    }

    public Task ApplyAsync(CancellationToken cancellationToken = default) =>
        _configure(
            SemanticRankerProfileResolver.FromSettings(_settings),
            cancellationToken);

}
