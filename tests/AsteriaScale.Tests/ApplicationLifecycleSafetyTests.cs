using System.Globalization;
using System.Xml.Linq;
using AsteriaScale.Services;

internal static class ApplicationLifecycleSafetyTests
{
    public static Task RunAsync()
    {
        VerifyShowRequestSurvivesStartup();
        VerifyAutoResetSignalSurvivesListenerDelay();
        VerifyPrimaryButtonHoverContrast();
        VerifyWindowsAppSdkSingleFileDeployment();
        return Task.CompletedTask;
    }

    private static void VerifyShowRequestSurvivesStartup()
    {
        PendingShowRequestState state = new();
        Check(!state.TryConsume(canShow: true),
            "A show request appeared before another process requested it.");

        state.Request();
        Check(!state.TryConsume(canShow: false),
            "A show request was consumed before the main window existed.");
        Check(state.TryConsume(canShow: true),
            "A show request received during startup was not delivered once the window became ready.");
        Check(!state.TryConsume(canShow: true),
            "One show request was delivered more than once.");

        state.Request();
        state.Request();
        Check(state.TryConsume(canShow: true) && !state.TryConsume(canShow: true),
            "Concurrent show requests were not safely coalesced.");
    }

    private static void VerifyAutoResetSignalSurvivesListenerDelay()
    {
        string name = "Local\\AsteriaScale-Test-Show-" + Guid.NewGuid().ToString("N");
        using EventWaitHandle signal = new(false, EventResetMode.AutoReset, name);

        // The production endpoint is created before the single-instance mutex.
        // A racing secondary launch can therefore signal before the listener
        // starts, and AutoResetEvent must retain that notification.
        signal.Set();
        Check(signal.WaitOne(0),
            "A show signal sent before listener startup was lost.");
        Check(!signal.WaitOne(0),
            "The auto-reset show signal was delivered more than once.");
    }

    private static void VerifyPrimaryButtonHoverContrast()
    {
        string root = FindRepositoryRoot();
        string appXamlPath = Path.Combine(root, "src", "AsteriaScale", "App.xaml");
        XDocument document = XDocument.Load(appXamlPath, LoadOptions.PreserveWhitespace);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        XElement hoverColor = document
            .Descendants(presentation + "Color")
            .Single(element => (string?)element.Attribute(xaml + "Key") == "AccentHoverColor");
        XElement primaryStyle = document
            .Descendants(presentation + "Style")
            .Single(element => (string?)element.Attribute(xaml + "Key") == "PrimaryButtonStyle");
        string foreground = SetterValue(primaryStyle, presentation, "Foreground");
        string fontSize = SetterValue(primaryStyle, presentation, "FontSize");
        XElement hoverSetter = primaryStyle
            .Descendants(presentation + "Trigger")
            .Single(element =>
                (string?)element.Attribute("Property") == "IsMouseOver" &&
                (string?)element.Attribute("Value") == "True")
            .Elements(presentation + "Setter")
            .Single(element => (string?)element.Attribute("Property") == "Background");

        Check((string?)hoverSetter.Attribute("Value") == "{StaticResource AccentHoverBrush}",
            "The primary button hover no longer uses the audited accent-hover resource.");
        Check(string.Equals(foreground, "#FFFFFF", StringComparison.OrdinalIgnoreCase),
            "The primary button contrast check expects its current white text.");
        Check(double.Parse(fontSize, CultureInfo.InvariantCulture) == 14d,
            "The primary button text size changed; re-evaluate its contrast threshold.");

        double contrast = ContrastRatio(foreground, hoverColor.Value.Trim());
        Check(contrast >= 4.5d,
            $"Primary button hover contrast is {contrast:F3}:1; 14 DIP text requires at least 4.5:1.");
    }

    private static void VerifyWindowsAppSdkSingleFileDeployment()
    {
        string root = FindRepositoryRoot();
        string projectPath = Path.Combine(
            root,
            "src",
            "AsteriaScale",
            "AsteriaScale.csproj");
        XDocument project = XDocument.Load(projectPath);
        XElement selfContained = project
            .Descendants("WindowsAppSDKSelfContained")
            .Single();
        Check(string.Equals(
                selfContained.Value.Trim(),
                "true",
                StringComparison.OrdinalIgnoreCase),
            "Single-file publish no longer bundles the Windows App SDK runtime.");
    }

    private static string SetterValue(XElement style, XNamespace presentation, string property) =>
        (string?)style
            .Elements(presentation + "Setter")
            .Single(element => (string?)element.Attribute("Property") == property)
            .Attribute("Value")
        ?? throw new InvalidDataException($"Style setter '{property}' has no value.");

    private static double ContrastRatio(string first, string second)
    {
        double firstLuminance = RelativeLuminance(first);
        double secondLuminance = RelativeLuminance(second);
        double lighter = Math.Max(firstLuminance, secondLuminance);
        double darker = Math.Min(firstLuminance, secondLuminance);
        return (lighter + 0.05d) / (darker + 0.05d);
    }

    private static double RelativeLuminance(string color)
    {
        if (color.Length != 7 || color[0] != '#')
        {
            throw new InvalidDataException($"Expected #RRGGBB colour, got '{color}'.");
        }

        static double Linear(byte component)
        {
            double value = component / 255d;
            return value <= 0.04045d
                ? value / 12.92d
                : Math.Pow((value + 0.055d) / 1.055d, 2.4d);
        }

        byte red = byte.Parse(color.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        byte green = byte.Parse(color.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        byte blue = byte.Parse(color.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return (0.2126d * Linear(red)) +
               (0.7152d * Linear(green)) +
               (0.0722d * Linear(blue));
    }

    private static string FindRepositoryRoot()
    {
        string? current = AppContext.BaseDirectory;
        while (current is not null && !File.Exists(Path.Combine(current, "AsteriaScale.slnx")))
        {
            current = Path.GetDirectoryName(current);
        }

        return current ?? throw new InvalidOperationException(
            "Repository root was not found for application lifecycle safety tests.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
