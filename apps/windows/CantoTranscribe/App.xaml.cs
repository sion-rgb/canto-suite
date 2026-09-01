using Microsoft.UI.Xaml;

namespace CantoTranscribe;

public partial class App : Application
{
    private Window? _window;
    private static readonly string CrashLog = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CantoSuite", "CantoTranscribe", "startup-error.log");

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, args) =>
        {
            WriteCrash(args.Exception);
            args.Handled = false;
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            var commandLine = Environment.GetCommandLineArgs().Skip(1).ToArray();
            var startupMedia = commandLine.FirstOrDefault(File.Exists);
            var cancelAfterMs = commandLine
                .FirstOrDefault(value => value.StartsWith("--cancel-after-ms=", StringComparison.OrdinalIgnoreCase))?
                .Split('=', 2).LastOrDefault();
            var options = new StartupOptions(
                startupMedia,
                commandLine.Contains("--auto-start", StringComparer.OrdinalIgnoreCase),
                commandLine.Any(value => value.Equals("--format=srt", StringComparison.OrdinalIgnoreCase)),
                commandLine.Any(value => value.Equals("--script=simplified", StringComparison.OrdinalIgnoreCase)),
                commandLine.Any(value => value.Equals("--text=clean", StringComparison.OrdinalIgnoreCase)),
                int.TryParse(cancelAfterMs, out var milliseconds) && milliseconds > 0 ? milliseconds : null);
            _window = new MainWindow(options);
            _window.Activate();
        }
        catch (Exception error)
        {
            WriteCrash(error);
            throw;
        }
    }

    public sealed record StartupOptions(string? MediaPath, bool AutoStart, bool Srt,
        bool Simplified, bool Clean, int? CancelAfterMs = null);

    private static void WriteCrash(Exception error)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLog)!);
            File.WriteAllText(CrashLog, error.ToString());
        }
        catch { }
    }
}
