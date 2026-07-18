using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace ShadowWhispr.Services;

/// <summary>
/// What the tray icon's coloured dot is telling the user. This replaced the
/// floating bottom-right overlay window, which users found disruptive.
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
/// The system-tray presence that lets ShadowWhispr keep listening for the hold
/// hotkey with no window on screen. WPF has no tray control, so this wraps the
/// Windows Forms NotifyIcon; every WinForms type here stays fully qualified to
/// avoid colliding with the WPF types of the same name.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _statusItem;
    private readonly Icon? _baseIcon;
    private readonly bool _ownsBaseIcon;
    private Icon? _renderedIcon;
    private IntPtr _renderedHandle;
    private TrayState _state = TrayState.Starting;
    private bool _disposed;

    public TrayIconService()
    {
        _statusItem = new Forms.ToolStripMenuItem("Starting…") { Enabled = false };

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Open ShadowWhispr", null, (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add("Check for updates", null, (_, _) => CheckUpdatesRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit ShadowWhispr", null, (_, _) => QuitRequested?.Invoke(this, EventArgs.Empty));

        _baseIcon = LoadIcon(out _ownsBaseIcon);

        _icon = new Forms.NotifyIcon
        {
            Text = "ShadowWhispr",
            ContextMenuStrip = menu,
            Visible = false
        };
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
        ApplyIcon();
    }

    public event EventHandler? OpenRequested;
    public event EventHandler? QuitRequested;
    public event EventHandler? CheckUpdatesRequested;

    public bool Visible
    {
        get => _icon.Visible;
        set => _icon.Visible = value;
    }

    /// <summary>
    /// Repaints the tray icon with the status colour. Green means ShadowWhispr is
    /// hearing you; red means idle or a problem; amber means it is busy. Called
    /// from background threads, so it must not touch WPF.
    /// </summary>
    public void SetState(TrayState state)
    {
        if (_disposed || _state == state) return;
        _state = state;
        ApplyIcon();
    }

    /// <summary>
    /// Updates the hover tooltip and the greyed-out first menu line, so the tray
    /// alone tells the user whether dictation is actually armed.
    /// </summary>
    public void SetStatus(string status)
    {
        _statusItem.Text = status;
        // NotifyIcon.Text throws above 63 characters; status strings are short
        // but user-supplied hotkey names make that worth guarding.
        var tooltip = $"ShadowWhispr — {status}";
        _icon.Text = tooltip.Length > 63 ? tooltip[..63] : tooltip;
    }

    public void ShowMessage(string title, string body)
    {
        try
        {
            _icon.BalloonTipTitle = title;
            _icon.BalloonTipText = body;
            _icon.ShowBalloonTip(4000);
        }
        catch (Exception exception)
        {
            AppLog.Write("Showing a tray notification failed", exception);
        }
    }

    private static Color ColourFor(TrayState state) => state switch
    {
        TrayState.Listening => Color.FromArgb(83, 211, 137),
        TrayState.Working => Color.FromArgb(231, 184, 92),
        TrayState.Starting => Color.FromArgb(231, 184, 92),
        _ => Color.FromArgb(223, 74, 74)
    };

    /// <summary>
    /// Draws the app icon with a status dot badged into its bottom-right corner
    /// and hands the result to the tray. A failure here must never take the tray
    /// icon away, so the plain app icon stays as the fallback.
    /// </summary>
    private void ApplyIcon()
    {
        Icon? fresh = null;
        IntPtr freshHandle = IntPtr.Zero;
        try
        {
            fresh = RenderBadgedIcon(_state, out freshHandle);
        }
        catch (Exception exception)
        {
            AppLog.Write($"Drawing the {_state} tray icon failed; using the plain app icon", exception);
        }

        var previous = _renderedIcon;
        var previousHandle = _renderedHandle;

        _icon.Icon = fresh ?? _baseIcon ?? SystemIcons.Application;
        _renderedIcon = fresh;
        _renderedHandle = freshHandle;

        // Only now that the tray is no longer showing it can the old bitmap icon
        // be released; GetHicon handles are not freed by Icon.Dispose.
        previous?.Dispose();
        if (previousHandle != IntPtr.Zero) DestroyIcon(previousHandle);
    }

    private Icon RenderBadgedIcon(TrayState state, out IntPtr handle)
    {
        const int size = 32;
        const int dot = 16;

        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;

            if (_baseIcon is not null)
            {
                using var art = _baseIcon.ToBitmap();
                graphics.DrawImage(art, new Rectangle(0, 0, size, size));
            }

            var badge = new Rectangle(size - dot, size - dot, dot - 1, dot - 1);
            // A dark ring keeps the dot readable over light and dark taskbars.
            using var ring = new SolidBrush(Color.FromArgb(235, 12, 16, 22));
            graphics.FillEllipse(ring, Rectangle.Inflate(badge, 2, 2));
            using var fill = new SolidBrush(ColourFor(state));
            graphics.FillEllipse(fill, badge);
        }

        handle = bitmap.GetHicon();
        return Icon.FromHandle(handle);
    }

    /// <summary>
    /// Uses the executable's own icon so the tray always matches the app icon.
    /// Falls back to a stock icon rather than leaving an invisible tray entry.
    /// </summary>
    private static Icon LoadIcon(out bool owned)
    {
        owned = false;
        try
        {
            var executable = Environment.ProcessPath;
            if (executable is not null)
            {
                var extracted = Icon.ExtractAssociatedIcon(executable);
                if (extracted is not null)
                {
                    owned = true;
                    return extracted;
                }
            }
        }
        catch (Exception exception)
        {
            AppLog.Write("Could not load the tray icon from the executable", exception);
        }

        return SystemIcons.Application;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _icon.Visible = false;
            _icon.ContextMenuStrip?.Dispose();
            _icon.Dispose();
            _renderedIcon?.Dispose();
            _renderedIcon = null;
            if (_renderedHandle != IntPtr.Zero)
            {
                DestroyIcon(_renderedHandle);
                _renderedHandle = IntPtr.Zero;
            }
            if (_ownsBaseIcon) _baseIcon?.Dispose();
        }
        catch (Exception exception)
        {
            AppLog.Write("Disposing the tray icon failed", exception);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);
}
