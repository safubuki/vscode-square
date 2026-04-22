using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using TurtleAIQuartetHub.Panel.Services;

namespace TurtleAIQuartetHub.Panel;

public partial class App : Application
{
    private SingleInstanceCoordinator? _singleInstanceCoordinator;

    protected override async void OnStartup(StartupEventArgs e)
    {
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        DispatcherUnhandledException += App_DispatcherUnhandledException;

        _singleInstanceCoordinator = new SingleInstanceCoordinator();
        if (!_singleInstanceCoordinator.IsPrimary)
        {
            _ = await _singleInstanceCoordinator.SendToPrimaryAsync(e.Args, CancellationToken.None);
            Shutdown();
            return;
        }

        base.OnStartup(e);

        _singleInstanceCoordinator.CommandReceived += args =>
        {
            _ = Dispatcher.InvokeAsync(() =>
            {
                if (MainWindow is MainWindow mainWindow)
                {
                    mainWindow.ExecuteExternalCommand(args);
                }
            }, DispatcherPriority.Background);
        };

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        mainWindow.Show();

        if (e.Args.Length > 0)
        {
            _ = Dispatcher.InvokeAsync(
                () => mainWindow.ExecuteExternalCommand(e.Args),
                DispatcherPriority.Background);
        }
    }

    private static void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        DiagnosticLog.Write(e.Exception);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_singleInstanceCoordinator?.IsPrimary == true)
        {
            TaskbarJumpListService.SetInactiveMenu();
        }

        _singleInstanceCoordinator?.Dispose();
        base.OnExit(e);
    }
}
