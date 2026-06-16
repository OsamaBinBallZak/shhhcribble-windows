using System.Windows;
using Application = System.Windows.Application; // disambiguate from System.Windows.Forms.Application

namespace Shhhcribble.Windows;

/// <summary>
/// Tray-only application: no main window, lives in the system tray until the
/// user quits (ShutdownMode = OnExplicitShutdown).
/// </summary>
public partial class App : Application
{
    private AppController? _controller;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _controller = new AppController(Dispatcher);
        _controller.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _controller?.Dispose();
        base.OnExit(e);
    }
}
