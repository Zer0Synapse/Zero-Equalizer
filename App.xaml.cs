using System.Windows;

namespace ZeroEqualizer;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs args)
    {
        base.OnStartup(args);

        var launchedByUpdater = args.Args is [AutoUpdates.LaunchArgument, var token] &&
            Guid.TryParseExact(token, "N", out _);
        if (!launchedByUpdater && await AutoUpdates.TryStartLatest())
        {
            Shutdown();
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.ContentRendered += (_, _) => AutoUpdates.MarkHealthy(launchedByUpdater ? args.Args[1] : null);
        window.Show();
    }
}
