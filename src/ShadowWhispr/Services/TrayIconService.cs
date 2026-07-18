using System.Drawing;
using Forms = System.Windows.Forms;

namespace ShadowWhispr.Services;

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
    private Icon? _ownedIcon;
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

        _icon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(out _ownedIcon),
            Text = "ShadowWhispr",
            ContextMenuStrip = menu,
            Visible = false
        };
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
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

    /// <summary>
    /// Uses the executable's own icon so the tray always matches the app icon.
    /// Falls back to a stock icon rather than leaving an invisible tray entry.
    /// </summary>
    private static Icon LoadIcon(out Icon? owned)
    {
        owned = null;
        try
        {
            var executable = Environment.ProcessPath;
            if (executable is not null)
            {
                var extracted = Icon.ExtractAssociatedIcon(executable);
                if (extracted is not null)
                {
                    owned = extracted;
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
            _ownedIcon?.Dispose();
            _ownedIcon = null;
        }
        catch (Exception exception)
        {
            AppLog.Write("Disposing the tray icon failed", exception);
        }
    }
}
