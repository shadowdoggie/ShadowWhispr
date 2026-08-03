using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using ShadowWhispr.Services;

namespace ShadowWhispr.Linux.Services;

/// <summary>
/// What the tray icon's coloured dot is telling the user. Mirrors the Windows
/// TrayState so both apps read the same at a glance.
/// </summary>
public enum TrayState
{
    Starting,
    Ready,
    Listening,
    Working,
    Error
}

/// <summary>
/// The tray presence on Linux, drawn through the StatusNotifierItem DBus
/// protocol that Avalonia's TrayIcon speaks. On GNOME this needs the
/// AppIndicator extension; without it the icon simply does not appear, and the
/// app window remains reachable by launching ShadowWhispr again.
/// </summary>
public sealed class LinuxTrayIconService : IDisposable
{
    private readonly TrayIcon _icon;
    private readonly NativeMenuItem _statusItem;
    private readonly NativeMenuItem _pauseItem;
    private readonly Bitmap? _baseIcon;
    private TrayState _state = TrayState.Starting;
    private string _status = "Starting…";
    private bool _disposed;
    private bool _syncingPause;

    public LinuxTrayIconService(Application application)
    {
        _statusItem = new NativeMenuItem("Starting…") { IsEnabled = false };
        _pauseItem = new NativeMenuItem("Pause dictation") { ToggleType = NativeMenuItemToggleType.CheckBox };
        _pauseItem.Click += (_, _) =>
        {
            if (!_syncingPause) PauseToggled?.Invoke(this, _pauseItem.IsChecked);
        };

        var openItem = new NativeMenuItem("Open ShadowWhispr");
        openItem.Click += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
        var quitItem = new NativeMenuItem("Quit ShadowWhispr");
        quitItem.Click += (_, _) => QuitRequested?.Invoke(this, EventArgs.Empty);
        var updatesItem = new NativeMenuItem("Check for updates");
        updatesItem.Click += (_, _) => CheckUpdatesRequested?.Invoke(this, EventArgs.Empty);

        var menu = new NativeMenu();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(openItem);
        menu.Items.Add(_pauseItem);
        menu.Items.Add(quitItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(updatesItem);

        _baseIcon = LoadBaseIcon();

        _icon = new TrayIcon
        {
            ToolTipText = "ShadowWhispr",
            Menu = menu,
            IsVisible = false
        };
        _icon.Clicked += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
        ApplyIcon();

        TrayIcon.SetIcons(application, [_icon]);
    }

    public event EventHandler? OpenRequested;
    public event EventHandler? QuitRequested;
    public event EventHandler? CheckUpdatesRequested;

    /// <summary>Raised when the user ticks or unticks "Pause dictation" in the menu.</summary>
    public event EventHandler<bool>? PauseToggled;

    public bool Visible
    {
        get => _icon.IsVisible;
        set => _icon.IsVisible = value;
    }

    /// <summary>
    /// The "Pause dictation" tick. Setting it keeps the menu in sync when the
    /// pause was toggled from the main window instead, without re-raising
    /// <see cref="PauseToggled"/>.
    /// </summary>
    public bool Paused
    {
        get => _pauseItem.IsChecked;
        set
        {
            _syncingPause = true;
            try
            {
                _pauseItem.IsChecked = value;
            }
            finally
            {
                _syncingPause = false;
            }
        }
    }

    public void SetState(TrayState state)
    {
        if (_disposed || _state == state) return;
        _state = state;
        Dispatcher.UIThread.Post(ApplyIcon);
    }

    public void SetStatus(string status)
    {
        _status = status;
        Dispatcher.UIThread.Post(() =>
        {
            _statusItem.Header = status;
            _icon.ToolTipText = $"ShadowWhispr — {status}";
        });
    }

    /// <summary>
    /// Whether anything on the session bus can actually host a tray icon.
    /// Stock GNOME removed tray support, so without the AppIndicator extension
    /// the icon would silently never appear — callers use this to tell the
    /// user what to install instead of leaving them wondering.
    /// </summary>
    public static bool IsTrayHostAvailable()
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "busctl",
                ArgumentList = { "--user", "--no-pager", "status", "org.kde.StatusNotifierWatcher" },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = Process.Start(startInfo);
            if (process is null) return true; // cannot tell; assume the best
            process.WaitForExit(3000);
            return process.ExitCode == 0;
        }
        catch (Exception exception)
        {
            AppLog.Write("Could not probe for a tray host; assuming one exists", exception);
            return true;
        }
    }

    /// <summary>Shows a desktop notification through notify-send; GNOME has no balloon API.</summary>
    public void ShowMessage(string title, string body)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "notify-send",
                ArgumentList = { "--app-name=ShadowWhispr", title, body },
                UseShellExecute = false
            });
        }
        catch (Exception exception)
        {
            AppLog.Write("Showing a desktop notification failed", exception);
        }
    }

    private static Color ColourFor(TrayState state) => state switch
    {
        TrayState.Listening => Color.FromRgb(83, 211, 137),
        TrayState.Working => Color.FromRgb(231, 184, 92),
        TrayState.Starting => Color.FromRgb(231, 184, 92),
        _ => Color.FromRgb(223, 74, 74)
    };

    /// <summary>
    /// Draws the app icon with a status dot badged into its bottom-right corner.
    /// A failure here must never take the tray icon away, so the plain app icon
    /// stays as the fallback.
    /// </summary>
    private void ApplyIcon()
    {
        try
        {
            const int size = 64;
            const int dot = 30;
            var rendered = new RenderTargetBitmap(new PixelSize(size, size));
            using (var ctx = rendered.CreateDrawingContext())
            {
                if (_baseIcon is not null)
                {
                    ctx.DrawImage(
                        _baseIcon,
                        new Rect(_baseIcon.Size),
                        new Rect(0, 0, size, size));
                }

                var centre = new Point(size - (dot / 2.0) - 1, size - (dot / 2.0) - 1);
                // A dark ring keeps the dot readable over light and dark panels.
                ctx.DrawEllipse(new SolidColorBrush(Color.FromArgb(235, 12, 16, 22)), null,
                    centre, (dot / 2.0) + 3, (dot / 2.0) + 3);
                ctx.DrawEllipse(new SolidColorBrush(ColourFor(_state)), null,
                    centre, dot / 2.0, dot / 2.0);
            }

            using var stream = new MemoryStream();
            rendered.Save(stream);
            stream.Position = 0;
            _icon.Icon = new WindowIcon(stream);
        }
        catch (Exception exception)
        {
            AppLog.Write($"Drawing the {_state} tray icon failed; using the plain app icon", exception);
            if (_baseIcon is not null) _icon.Icon = new WindowIcon(_baseIcon);
        }
    }

    private static Bitmap? LoadBaseIcon()
    {
        try
        {
            return new Bitmap(AssetLoader.Open(new Uri("avares://shadowwhispr/Assets/icon.png")));
        }
        catch (Exception exception)
        {
            AppLog.Write("Could not load the tray icon asset", exception);
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _icon.IsVisible = false;
            _icon.Dispose();
            _baseIcon?.Dispose();
        }
        catch (Exception exception)
        {
            AppLog.Write("Disposing the tray icon failed", exception);
        }
    }
}
