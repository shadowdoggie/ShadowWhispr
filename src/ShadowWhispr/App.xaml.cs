using System.Windows;
using ShadowWhispr.Services;

namespace ShadowWhispr;

public partial class App : Application
{
    /// <remarks>
    /// The installer detects a running ShadowWhispr through this same mutex
    /// (AppMutex in installer\ShadowWhispr.iss). "Local\" is the session
    /// namespace, which is the name Inno Setup opens, so the two must be kept
    /// in step — otherwise an upgrade fails on a locked exe while the app is
    /// sitting invisibly in the tray.
    /// </remarks>
    private const string InstanceMutexName = @"Local\ShadowWhispr.SingleInstance";
    private const string ShowWindowEventName = @"Local\ShadowWhispr.ShowWindow";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _showWindowSignal;
    private CancellationTokenSource? _signalListener;

    /// <summary>
    /// True when Windows (or the user) started ShadowWhispr with --tray, meaning
    /// it should come up hidden in the system tray instead of opening a window.
    /// </summary>
    public bool StartHiddenInTray { get; private set; }

    /// <summary>Raised when a second launch asks the running copy to show itself.</summary>
    public event EventHandler? ShowWindowRequested;

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

        StartHiddenInTray = e.Args.Any(argument =>
            string.Equals(argument, StartupService.TrayArgument, StringComparison.OrdinalIgnoreCase));

        if (!ClaimSingleInstance())
        {
            // A second copy would install a second keyboard hook and fight the
            // first over the microphone, so hand focus back and exit quietly.
            // Nothing is created here on purpose: the window is built below only
            // once this process knows it is the one that owns the app.
            AppLog.Write("Another ShadowWhispr instance is already running; asking it to show and exiting");
            SignalRunningInstance();
            Shutdown();
            return;
        }

        base.OnStartup(e);

        // Built here rather than through StartupUri so that a duplicate launch
        // never flashes a second window or a second tray icon before exiting.
        var window = new MainWindow();
        MainWindow = window;

        // A window must be shown for its Loaded event — and therefore all of the
        // app's startup work — to run at all. When starting into the tray it is
        // shown minimized and off the taskbar so nothing appears on screen, then
        // hidden properly once loading has finished.
        if (StartHiddenInTray)
        {
            window.WindowState = WindowState.Minimized;
            window.ShowInTaskbar = false;
        }

        window.Show();
    }

    /// <summary>
    /// Takes ownership of the single-instance mutex. Returns false when another
    /// copy already owns it.
    /// </summary>
    private bool ClaimSingleInstance()
    {
        try
        {
            _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var createdNew);
            if (!createdNew)
            {
                _instanceMutex.Dispose();
                _instanceMutex = null;
                return false;
            }

            StartShowWindowListener();
            return true;
        }
        catch (Exception exception)
        {
            // Never let instance bookkeeping stop the app from running at all.
            AppLog.Write("Single-instance check failed; continuing anyway", exception);
            return true;
        }
    }

    private void StartShowWindowListener()
    {
        try
        {
            _showWindowSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);
            _signalListener = new CancellationTokenSource();
            var token = _signalListener.Token;
            var signal = _showWindowSignal;

            var thread = new Thread(() =>
            {
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        if (!signal.WaitOne(TimeSpan.FromMilliseconds(500))) continue;
                        if (token.IsCancellationRequested) return;
                        Dispatcher.Invoke(() => ShowWindowRequested?.Invoke(this, EventArgs.Empty));
                    }
                }
                catch (Exception exception)
                {
                    AppLog.Write("The show-window listener stopped", exception);
                }
            })
            {
                IsBackground = true,
                Name = "ShadowWhispr show-window signal"
            };
            thread.Start();
        }
        catch (Exception exception)
        {
            AppLog.Write("Could not start the show-window listener", exception);
        }
    }

    private static void SignalRunningInstance()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ShowWindowEventName, out var signal))
            {
                using (signal) signal.Set();
            }
        }
        catch (Exception exception)
        {
            AppLog.Write("Could not signal the running ShadowWhispr instance", exception);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _signalListener?.Cancel();
            _signalListener?.Dispose();
            _showWindowSignal?.Dispose();
            _instanceMutex?.ReleaseMutex();
            _instanceMutex?.Dispose();
        }
        catch (Exception exception)
        {
            AppLog.Write("Releasing single-instance handles failed", exception);
        }

        base.OnExit(e);
    }
}
