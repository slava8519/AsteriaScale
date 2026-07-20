using System.Diagnostics;
using AsteriaScale.Interop;
using AsteriaScale.Models;

namespace AsteriaScale.Services;

public sealed class WindowCatalogService
{
    private readonly int _ownProcessId = Environment.ProcessId;

    public IReadOnlyList<WindowInfo> GetWindows()
    {
        List<WindowInfo> windows = [];
        NativeMethods.EnumWindows((hWnd, _) =>
        {
            WindowInfo? window = TryGetWindow(hWnd);
            if (window is not null)
            {
                windows.Add(window);
            }

            return true;
        }, nint.Zero);

        return windows
            .OrderBy(w => w.ProcessName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(w => w.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public WindowInfo? TryGetWindow(nint hWnd)
    {
        if (hWnd == nint.Zero || !NativeMethods.IsWindow(hWnd) || !NativeMethods.IsWindowVisible(hWnd) ||
            NativeMethods.GetWindow(hWnd, NativeMethods.GwOwner) != nint.Zero || NativeMethods.IsCloaked(hWnd))
        {
            return null;
        }

        string title = NativeMethods.ReadWindowTitle(hWnd).Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        NativeMethods.GetWindowThreadProcessId(hWnd, out uint processIdValue);
        int processId = unchecked((int)processIdValue);
        if (processId <= 0 || processId == _ownProcessId)
        {
            return null;
        }

        string className = NativeMethods.ReadClassName(hWnd);
        if (className.StartsWith("Magpie", StringComparison.Ordinal) ||
            className.StartsWith("Window_Magpie", StringComparison.Ordinal) ||
            string.Equals(className, "Progman", StringComparison.Ordinal) ||
            string.Equals(className, "WorkerW", StringComparison.Ordinal))
        {
            return null;
        }

        if (!NativeMethods.GetClientRect(hWnd, out NativeMethods.Rect clientRect) ||
            clientRect.Width < 240 || clientRect.Height < 160)
        {
            return null;
        }

        string processName;
        string processPath = string.Empty;
        try
        {
            using Process process = Process.GetProcessById(processId);
            processName = process.ProcessName;
            try
            {
                processPath = process.MainModule?.FileName ?? string.Empty;
            }
            catch
            {
                // Protected processes can deny access to MainModule; the process name still works for profiles.
            }
        }
        catch
        {
            return null;
        }

        return new WindowInfo
        {
            Handle = hWnd,
            Title = title,
            ProcessName = processName,
            ProcessPath = processPath,
            ClassName = className,
            ProcessId = processId,
            Width = clientRect.Width,
            Height = clientRect.Height
        };
    }

    public WindowInfo? GetForegroundEligibleWindow() => TryGetWindow(NativeMethods.GetForegroundWindow());
}
