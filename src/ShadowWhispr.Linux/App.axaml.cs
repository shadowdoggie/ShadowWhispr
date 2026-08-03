using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ShadowWhispr.Services;

namespace ShadowWhispr.Linux;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Hiding to the tray must not end the process; quitting is explicit
            // (the tray menu), mirroring the Windows app's OnExplicitShutdown.
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;

            var window = new MainWindow();
            desktop.MainWindow = window;

            if (Program.StartHiddenInTray)
            {
                AppLog.Write("Started with --tray; keeping the main window hidden");
            }
            else
            {
                window.Show();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
