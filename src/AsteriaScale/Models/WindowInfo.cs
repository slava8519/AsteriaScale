namespace AsteriaScale.Models;

public sealed class WindowInfo
{
    public required nint Handle { get; init; }
    public required string Title { get; init; }
    public required string ProcessName { get; init; }
    public string ProcessPath { get; init; } = string.Empty;
    public string ClassName { get; init; } = string.Empty;
    public int ProcessId { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }

    public string Details => string.Format("{0}  •  {1}×{2}", ProcessName, Width, Height);
    public string AutomationKey => !string.IsNullOrWhiteSpace(ProcessPath) ? ProcessPath : ProcessName;

    public override string ToString() => Title;
}
