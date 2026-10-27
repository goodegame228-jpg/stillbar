namespace Stillbar.App;

/// <summary>
/// Entry point. «--hook» is Claude Code calling: answer it without starting WPF (no window, no resources), so
/// each hook costs a fraction of a second. Anything else starts the bar.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--hook") return Services.HookClient.Run(args[1]);
        if (args.Length >= 1 && args[0] == "--install") return Services.Installer.Install();
        if (args.Length >= 1 && args[0] == "--uninstall") return Services.Installer.Uninstall();
        if (args.Length >= 1 && args[0] == "--uninstall-cleanup") return Services.Installer.Cleanup();

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
