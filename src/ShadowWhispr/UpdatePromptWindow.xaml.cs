using System.Windows;
using ShadowWhispr.Services;

namespace ShadowWhispr;

/// <summary>
/// Confirmation pop-up shown when a newer release is found. It shows the new
/// version and its changelog and lets the user install now, install on close,
/// or decline. The choice is read from <see cref="Choice"/> after ShowDialog.
/// </summary>
public partial class UpdatePromptWindow : Window
{
    public UpdateChoice Choice { get; private set; } = UpdateChoice.Decline;

    public UpdatePromptWindow(string version, string changelog)
    {
        InitializeComponent();
        TitleText.Text = $"ShadowWhispr {version}";
        ChangelogBox.Text = string.IsNullOrWhiteSpace(changelog)
            ? "No changelog was provided for this release."
            : changelog;
    }

    private void InstallNowClicked(object sender, RoutedEventArgs e) => Finish(UpdateChoice.InstallNow);

    private void InstallOnCloseClicked(object sender, RoutedEventArgs e) => Finish(UpdateChoice.InstallOnClose);

    private void NotNowClicked(object sender, RoutedEventArgs e) => Finish(UpdateChoice.Decline);

    private void Finish(UpdateChoice choice)
    {
        Choice = choice;
        Close();
    }
}
