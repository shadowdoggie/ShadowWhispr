using System.Windows;
using ShadowWhispr.Services;

namespace ShadowWhispr;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Global crash handlers: every unhandled failure lands in app-log.txt
        // with a full stack trace before anything else happens.
        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Write("Unhandled UI exception", args.Exception);
            MessageBox.Show(args.Exception.Message, "ShadowWhispr", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
                AppLog.Write($"Fatal unhandled exception (app terminating: {args.IsTerminating})", exception);
            else
                AppLog.Write($"Fatal unhandled non-exception error: {args.ExceptionObject}");
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Write("Unobserved background task exception", args.Exception);
            args.SetObserved();
        };

        base.OnStartup(e);
    }
}
