namespace AsteriaScale.Services;

/// <summary>
/// Which ranker worker executable Core starts and which folder it passes as
/// --model-directory. The host supplies both, because they differ by platform:
/// the Windows app ships the worker and its models beside its own executable,
/// while a macOS app keeps the worker inside its bundle and reads the owner's
/// ConSeC bundle in place. That folder must be a real directory: the worker
/// rejects a reparse point at --model-directory and below, so a symlinked
/// layout beside the worker would not work there.
/// </summary>
public sealed class RankerWorkerLocation
{
    public RankerWorkerLocation(string workerExecutable, string modelDirectory)
    {
        WorkerExecutable = RequireFullyQualified(workerExecutable, nameof(workerExecutable));
        ModelDirectory = RequireFullyQualified(modelDirectory, nameof(modelDirectory));
    }

    public string WorkerExecutable { get; }

    public string ModelDirectory { get; }

    /// <summary>
    /// The Windows companion layout: &lt;base&gt;\Ranker\AsteriaScale.RankerWorker.exe
    /// with its models in &lt;base&gt;\Ranker\Models, exactly as the selector
    /// derived them before hosts could pass a location.
    /// </summary>
    public static RankerWorkerLocation BesideBaseDirectory(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        string rankerDirectory = Path.Combine(Path.GetFullPath(baseDirectory), "Ranker");
        return new RankerWorkerLocation(
            Path.Combine(rankerDirectory, "AsteriaScale.RankerWorker.exe"),
            Path.Combine(rankerDirectory, "Models"));
    }

    /// <summary>
    /// The companion layout beside the running host's physical executable
    /// (not the single-file extraction folder).
    /// </summary>
    public static RankerWorkerLocation ForCurrentProcess() =>
        BesideBaseDirectory(
            GlossBertDictionarySelectorService.ResolveCompanionBaseDirectory(
                Environment.ProcessPath,
                AppContext.BaseDirectory));

    // A relative path would be resolved against whatever the working directory
    // happens to be when the worker starts, so it is refused up front.
    private static string RequireFullyQualified(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (!Path.IsPathFullyQualified(value))
        {
            throw new ArgumentException(
                $"Путь ранжировщика должен быть абсолютным: '{value}'.",
                parameterName);
        }

        return value;
    }
}
