using System.Net.Sockets;
using System.Text;
using Avalonia;
using ShadowWhispr.Services;

namespace ShadowWhispr.Linux;

internal static class Program
{
    private static FileStream? _instanceLock;
    private static Socket? _showListener;

    /// <summary>Raised when a second launch asks the running copy to show itself.</summary>
    public static event EventHandler? ShowWindowRequested;

    public static bool StartHiddenInTray { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception exception)
                AppLog.Write($"Fatal unhandled exception (app terminating: {e.IsTerminating})", exception);
            else
                AppLog.Write($"Fatal unhandled non-exception error: {e.ExceptionObject}");
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Write("Unobserved background task exception", e.Exception);
            e.SetObserved();
        };

        StartHiddenInTray = args.Any(a => string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase));

        if (!ClaimSingleInstance())
        {
            AppLog.Write("Another ShadowWhispr instance is already running; asking it to show and exiting");
            SignalRunningInstance();
            return 0;
        }

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            _showListener?.Dispose();
            _instanceLock?.Dispose();
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static string RuntimeDirectory =>
        Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? Path.GetTempPath();

    private static string LockPath => Path.Combine(RuntimeDirectory, "shadowwhispr.lock");
    private static string SocketPath => Path.Combine(RuntimeDirectory, "shadowwhispr.sock");

    /// <summary>
    /// Takes the single-instance file lock. Named mutexes do not exist on Linux,
    /// so an exclusively opened lock file in the runtime directory plays that
    /// role; the kernel releases it automatically however the process dies.
    /// </summary>
    private static bool ClaimSingleInstance()
    {
        try
        {
            _instanceLock = new FileStream(
                LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return false;
        }
        catch (Exception exception)
        {
            // Never let instance bookkeeping stop the app from running at all.
            AppLog.Write("Single-instance check failed; continuing anyway", exception);
            return true;
        }

        StartShowWindowListener();
        return true;
    }

    private static void StartShowWindowListener()
    {
        try
        {
            if (File.Exists(SocketPath)) File.Delete(SocketPath);
            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
            listener.Listen(1);
            _showListener = listener;

            _ = Task.Run(async () =>
            {
                while (true)
                {
                    try
                    {
                        using var client = await listener.AcceptAsync();
                        ShowWindowRequested?.Invoke(null, EventArgs.Empty);
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }
                    catch (Exception exception)
                    {
                        AppLog.Write("The show-window listener stopped", exception);
                        return;
                    }
                }
            });
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
            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            client.Connect(new UnixDomainSocketEndPoint(SocketPath));
            client.Send(Encoding.ASCII.GetBytes("show"));
        }
        catch (Exception exception)
        {
            AppLog.Write("Could not signal the running ShadowWhispr instance", exception);
        }
    }
}
