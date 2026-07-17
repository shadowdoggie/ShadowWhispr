using System.Windows;
using System.Windows.Media;

namespace ShadowWhispr;

public partial class OverlayWindow : Window
{
    private static readonly SolidColorBrush Red = new(Color.FromRgb(223, 74, 74));
    private static readonly SolidColorBrush RedGlow = new(Color.FromArgb(85, 223, 74, 74));
    private static readonly SolidColorBrush Green = new(Color.FromRgb(83, 211, 137));
    private static readonly SolidColorBrush GreenGlow = new(Color.FromArgb(95, 83, 211, 137));
    private static readonly SolidColorBrush Amber = new(Color.FromRgb(231, 184, 92));
    private static readonly SolidColorBrush AmberGlow = new(Color.FromArgb(85, 231, 184, 92));

    public OverlayWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => PositionOnPrimaryScreen();
    }

    public void SetReady(string text = "Ready") => Set(text, Red, RedGlow);
    public void SetRecording() => Set("Listening", Green, GreenGlow);
    public void SetWorking(string text) => Set(text, Amber, AmberGlow);
    public void SetError() => Set("Check app", Red, RedGlow);

    private void Set(string text, Brush color, Brush glow)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => Set(text, color, glow));
            return;
        }
        StatusLabel.Text = text;
        Led.Fill = color;
        Glow.Fill = glow;
    }

    private void PositionOnPrimaryScreen()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 18;
        Top = workArea.Bottom - Height - 18;
    }
}
