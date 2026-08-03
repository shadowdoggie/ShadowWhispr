using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ShadowWhispr.Linux.Services;
using ShadowWhispr.Models;
using ShadowWhispr.Services;

namespace ShadowWhispr.Linux;

public partial class MainWindow : Window
{
    private static readonly IBrush ReadyGreen = new SolidColorBrush(Color.FromRgb(83, 211, 137));
    private static readonly IBrush WorkingGold = new SolidColorBrush(Color.FromRgb(231, 184, 92));
    private static readonly IBrush ErrorRed = new SolidColorBrush(Color.FromRgb(223, 74, 74));
    private static readonly IBrush MutedGray = new SolidColorBrush(Color.FromRgb(156, 165, 176));

    /// <summary>Shown in an optional hotkey field when no key is assigned.</summary>
    private const string OptionalHotkeyUnsetLabel = "Not set";

    private const string ReleasesUrl = "https://github.com/shadowdoggie/ShadowWhispr/releases/latest";

    private static readonly TimeSpan AutoSaveDelay = TimeSpan.FromMilliseconds(600);

    private readonly SettingsService _settingsService = new();
    private readonly ParakeetService _parakeet = new();
    private readonly AiProviderService _ai = new();
    private readonly LinuxHotkeyService _hotkey = new();
    private readonly LinuxAudioRecorderService _audio = new();
    private readonly LinuxTonePlayer _tones = new();
    private readonly LinuxTextInsertionService _inserter = new();
    private readonly UpdateService _updates = new();
    private readonly LinuxTrayIconService _tray;
    private readonly SpeechSetupService _speechSetup = new();

    private DispatcherTimer? _updatePollTimer;
    private DispatcherTimer? _autoSaveTimer;
    private bool _updateCheckInProgress;
    private bool _updatePromptOpen;
    private DateTime _suppressAutoPromptUntil = DateTime.MinValue;

    private AppSettings _settings = new();
    private bool _uiReady;
    private bool _startupComplete;
    private bool _busy;
    private CancellationTokenSource? _lifetime;
    private CancellationTokenSource? _modelRefresh;
    private int _modelRefreshGeneration;

    /// <summary>
    /// The provider whose settings the UI is currently showing; during a
    /// provider switch this still names the previous one, who is who the
    /// on-screen reasoning must be saved under.
    /// </summary>
    private string _activeProvider = AiProviderService.Claude;

    private Button? _capturingHotkeyButton;
    private string? _setupScriptPath;
    private bool _setupAttempted;
    private bool _setupRunning;

    /// <summary>Which hotkey started the recording, so its release applies the matching treatment.</summary>
    private HotkeyKind _activeHotkeyKind = HotkeyKind.Primary;

    /// <summary>True while the current recording is running hands-free after a quick tap.</summary>
    private bool _tapLatched;

    /// <summary>True when the selected provider's CLI says it is already signed in.</summary>
    private bool _providerLoggedIn;

    /// <summary>Counts login checks, so a slow answer for a provider you left cannot land.</summary>
    private int _loginStatusGeneration;

    /// <summary>Set only by a real quit request; a plain window close hides to the tray instead.</summary>
    private bool _exitRequested;
    private bool _dictationPaused;
    private bool _shuttingDown;

    private double _setupPercent;

    public MainWindow()
    {
        InitializeComponent();
        _tray = new LinuxTrayIconService(App.Current!);

        Closing += OnWindowClosing;
        _hotkey.Pressed += OnHotkeyPressed;
        _hotkey.Released += OnHotkeyReleased;
        _hotkey.Latched += OnHotkeyLatched;
        _audio.RecordingFailed += (_, ex) => AppLog.Write("Audio recording failed", ex);
        _tones.PlaybackFailed += (_, ex) => AppLog.Write("Cue tone playback failed", ex);
        _speechSetup.Progress += OnSetupProgress;
        _tray.OpenRequested += (_, _) => Dispatcher.UIThread.Post(ShowFromTray);
        _tray.QuitRequested += (_, _) => Dispatcher.UIThread.Post(RequestExit);
        _tray.PauseToggled += (_, paused) => Dispatcher.UIThread.Post(() => SetDictationPaused(paused));
        _tray.CheckUpdatesRequested += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            ShowFromTray();
            _ = RunUpdateCheckAsync(manual: true, _lifetime?.Token ?? default);
        });
        Program.ShowWindowRequested += (_, _) => Dispatcher.UIThread.Post(ShowFromTray);

        // Startup runs from here rather than from an Opened handler because a
        // --tray launch never opens the window at all.
        Dispatcher.UIThread.Post(() => _ = StartupAsync(), DispatcherPriority.Background);
    }

    private async Task StartupAsync()
    {
        AppLog.Write($"App started (version {typeof(MainWindow).Assembly.GetName().Version}, Linux)");
        _lifetime = new CancellationTokenSource();
        _settings = _settingsService.Load();
        ApplySettingsToUi();
        SetAuthHint(_settings.Provider);
        _tray.Visible = true;
        _tray.SetStatus("Starting…");
        _tray.SetState(TrayState.Starting);

        if (!LinuxTrayIconService.IsTrayHostAvailable())
        {
            AppLog.Write("No StatusNotifierWatcher on the session bus; the tray icon will not be shown by this desktop");
            _tray.ShowMessage(
                "ShadowWhispr's tray icon is hidden by your desktop",
                "GNOME hides tray icons by default. Install and enable the 'AppIndicator and KStatusNotifierItem Support' " +
                "extension (gnome-shell-extension-appindicator), then restart ShadowWhispr.");
        }

        try
        {
            _hotkey.Hotkey = ParseOptionalHotkey(_settings.Hotkey);
            _hotkey.RawHotkey = ParseOptionalHotkey(_settings.RawHotkey);
            _hotkey.Start();
        }
        catch (Exception ex)
        {
            AppLog.Write("Starting the global hotkey listener failed", ex);
            SetError($"Hotkey error: {ex.Message}");
        }

        await RefreshModelsAsync(_settings.ModelId);
        _ = RefreshLoginStatusAsync(_settings.Provider);
        _startupComplete = true;
        _ = WarmSpeechEngineAsync(_lifetime.Token);

        if (_settings.AutoUpdateEnabled)
        {
            StartUpdatePolling();
            _ = RunUpdateCheckAsync(manual: false, _lifetime.Token);
        }
    }

    // --- System tray and shutdown ----------------------------------------

    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>The only path that actually quits: the tray menu's Quit item.</summary>
    private void RequestExit()
    {
        AppLog.Write("Quit requested from the tray menu");
        _exitRequested = true;
        ShutdownNow();
    }

    private void TrayOptionToggled(object? sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;
        ReadUiIntoSettings();
        AppLog.Write($"Keep running in tray set to {_settings.KeepRunningInTray}");
        QueueAutoSave();
    }

    private void SoundCuesToggled(object? sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;
        ReadUiIntoSettings();
        AppLog.Write($"Dictation sound cues muted set to {_settings.SoundCuesMuted}");
        QueueAutoSave();
    }

    /// <summary>
    /// Writes (or removes) the XDG autostart entry. The checkbox is snapped
    /// back to the real on-disk state if the change failed, so it can never
    /// claim autostart is on when it isn't.
    /// </summary>
    private void StartAtLoginToggled(object? sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;

        var wanted = StartAtLoginCheck.IsChecked == true;
        if (LinuxAutostartService.Apply(wanted))
        {
            StartupStatus.Text = wanted
                ? "ShadowWhispr will start hidden in the tray when you log in."
                : string.Empty;
        }
        else
        {
            StartupStatus.Text = "The change failed — see app-log.txt";
            _uiReady = false;
            StartAtLoginCheck.IsChecked = LinuxAutostartService.IsEnabled();
            _uiReady = true;
        }

        ReadUiIntoSettings();
        QueueAutoSave();
    }

    // --- Automatic + manual update checking ------------------------------

    private void StartUpdatePolling()
    {
        _updatePollTimer ??= CreateTimer(TimeSpan.FromMinutes(30),
            () => _ = RunUpdateCheckAsync(manual: false, _lifetime?.Token ?? default));
        _updatePollTimer.Start();
    }

    private static DispatcherTimer CreateTimer(TimeSpan interval, Action onTick)
    {
        var timer = new DispatcherTimer { Interval = interval };
        timer.Tick += (_, _) => onTick();
        return timer;
    }

    /// <summary>
    /// Checks GitHub for a newer release. There is no auto-install on Linux —
    /// updates come through the package or tarball — so a newer version is
    /// announced with a link rather than an installer.
    /// </summary>
    private async Task RunUpdateCheckAsync(bool manual, CancellationToken cancellationToken)
    {
        if (!manual && !_settings.AutoUpdateEnabled) return;
        if (_updateCheckInProgress) return;
        _updateCheckInProgress = true;
        try
        {
            if (manual) SetUpdateStatus("Checking for updates…");
            var update = await _updates.CheckForUpdateAsync(cancellationToken);

            if (update is null)
            {
                if (manual) SetUpdateStatus("You're on the latest version.");
                return;
            }

            if (!manual)
            {
                if (_updatePromptOpen || _busy || _audio.IsRecording) return;
                if (DateTime.Now < _suppressAutoPromptUntil) return;
            }

            await PromptForUpdateAsync(update);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLog.Write("Update check failed", ex);
            if (manual) SetUpdateStatus("Update check failed — see app-log.txt");
        }
        finally
        {
            _updateCheckInProgress = false;
        }
    }

    private async Task PromptForUpdateAsync(UpdateInfo update)
    {
        if (_updatePromptOpen) return;
        _updatePromptOpen = true;
        try
        {
            AppLog.Write($"Showing update prompt for {update.Tag}");
            _tray.ShowMessage("ShadowWhispr update", $"Version {update.Tag} is available.");
            var install = await ConfirmAsync(
                $"ShadowWhispr {update.Tag} is available",
                "Update now opens a terminal where the new package is built and installed " +
                "(pacman asks for your password) and ShadowWhispr restarts itself.",
                "Update now", "Later");
            if (install)
            {
                if (LaunchSelfUpdater())
                {
                    SetUpdateStatus($"Updating to {update.Tag} in the terminal window…");
                    return;
                }

                // No terminal or no updater script (tarball install): the
                // releases page is the manual fallback.
                OpenInBrowser(ReleasesUrl);
            }
            else
            {
                _suppressAutoPromptUntil = DateTime.Now.AddHours(4);
            }

            SetUpdateStatus($"Update {update.Tag} available — {ReleasesUrl}");
        }
        finally
        {
            _updatePromptOpen = false;
        }
    }

    /// <summary>
    /// Opens the packaged Arch updater in a terminal, so the user performs the
    /// update themselves. Returns false when the script or a terminal is
    /// missing, in which case the caller falls back to the releases page.
    /// </summary>
    private static bool LaunchSelfUpdater()
    {
        var script = Path.Combine(AppContext.BaseDirectory, "scripts", "update-arch.sh");
        if (!File.Exists(script))
        {
            AppLog.Write($"Self-updater not found at {script}; falling back to the releases page");
            return false;
        }

        var startInfo = TerminalLauncher.TryCreate("bash", [script]);
        if (startInfo is null)
        {
            AppLog.Write("No terminal emulator found for the self-updater; falling back to the releases page");
            return false;
        }

        try
        {
            Process.Start(startInfo);
            AppLog.Write("Self-updater launched in a terminal");
            return true;
        }
        catch (Exception exception)
        {
            AppLog.Write("Launching the self-updater failed", exception);
            return false;
        }
    }

    private static void OpenInBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = "xdg-open", ArgumentList = { url }, UseShellExecute = false });
        }
        catch (Exception ex)
        {
            AppLog.Write($"Could not open {url}", ex);
        }
    }

    private void SetUpdateStatus(string text) =>
        Dispatcher.UIThread.Post(() => UpdateStatus.Text = text);

    private void CheckForUpdatesClicked(object? sender, RoutedEventArgs e) =>
        _ = RunUpdateCheckAsync(manual: true, _lifetime?.Token ?? default);

    private void AutoUpdateToggled(object? sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;
        ReadUiIntoSettings();
        QueueAutoSave();
        if (_settings.AutoUpdateEnabled)
        {
            AppLog.Write("Auto-update check enabled by user");
            StartUpdatePolling();
            _ = RunUpdateCheckAsync(manual: false, _lifetime?.Token ?? default);
        }
        else
        {
            AppLog.Write("Auto-update check turned off by user");
            _updatePollTimer?.Stop();
        }
    }

    // --- Speech engine ----------------------------------------------------

    private async Task WarmSpeechEngineAsync(CancellationToken cancellationToken)
    {
        try
        {
            SetEngine("Loading Parakeet…", WorkingGold);
            _tray.SetState(TrayState.Starting);
            await _parakeet.StartAsync(cancellationToken);
            AppLog.Write($"Speech engine ready on {_parakeet.Device}");
            SetupBanner.IsVisible = false;
            SetEngine("Parakeet ready · GPU", ReadyGreen);
            UpdateTrayStatus();
            if (!_audio.IsRecording && !_busy) _tray.SetState(TrayState.Ready);
        }
        catch (OperationCanceledException) { }
        catch (SpeechSetupRequiredException ex)
        {
            AppLog.Write($"Speech setup required (attempted before: {_setupAttempted})");
            _setupScriptPath = ex.SetupScriptPath;
            SetEngine("Speech setup needed", WorkingGold);
            _tray.SetState(TrayState.Ready);
            SetupRunButton.IsEnabled = true;
            SetupStatus.Text = _setupAttempted
                ? "Setup didn't finish. The full log is in setup-log.txt. Click to try again — it resumes where it left off."
                : string.Empty;
            SetupBanner.IsVisible = true;
            UpdateTrayStatus();
        }
        catch (Exception ex)
        {
            AppLog.Write("Speech engine start failed", ex);
            SetEngine("Parakeet needs attention", ErrorRed);
            _tray.SetStatus("Needs attention");
            _tray.SetState(TrayState.Error);
            SetError(ex.Message);
            if (SetupBanner.IsVisible)
            {
                SetupRunButton.IsEnabled = true;
                SetupStatus.Text = $"The speech engine could not start: {ex.Message}";
            }
        }
    }

    private async void SetupRunClicked(object? sender, RoutedEventArgs e)
    {
        if (_setupRunning) return;
        if (string.IsNullOrEmpty(_setupScriptPath) || !File.Exists(_setupScriptPath))
        {
            AppLog.Write($"Speech setup script not found at: {_setupScriptPath ?? "(no path)"}");
            SetupStatus.Text = "Setup script not found — please reinstall ShadowWhispr.";
            return;
        }

        _setupRunning = true;
        _setupAttempted = true;
        SetupRunButton.IsEnabled = false;
        SetupLogButton.IsVisible = true;
        SetupProgressPanel.IsVisible = true;
        SetupStatus.Text = string.Empty;
        SetSetupProgress(0, "Starting setup…");

        string? failure;
        try
        {
            failure = await _speechSetup.RunAsync(_setupScriptPath, _lifetime?.Token ?? default);
        }
        catch (OperationCanceledException)
        {
            _setupRunning = false;
            return;
        }
        catch (Exception ex)
        {
            AppLog.Write("The speech setup run threw", ex);
            failure = ex.Message;
        }

        _setupRunning = false;

        if (failure is not null)
        {
            SetSetupProgress(_setupPercent, "Setup stopped");
            SetupStatus.Text =
                $"{failure}\n\nThe most common cause is an unstable internet connection. " +
                "Click \"Set up speech now\" to try again — it resumes where it left off.";
            SetupRunButton.IsEnabled = true;
            return;
        }

        SetSetupProgress(100, "Starting the speech engine…");
        SetupStatus.Text = "Almost done — starting the speech engine (this can take a minute)…";
        await WarmSpeechEngineAsync(_lifetime?.Token ?? default);
    }

    private void OnSetupProgress(object? sender, SetupProgressEventArgs e) =>
        Dispatcher.UIThread.Post(() => SetSetupProgress(e.Percent, e.Message));

    private void SetSetupProgress(double percent, string message)
    {
        _setupPercent = Math.Clamp(percent, 0, 100);
        SetupStepText.Text = message;
        SetupPercentText.Text = $"{_setupPercent:0}%";
        SetupProgressBar.Value = _setupPercent;
    }

    private void OpenSetupLogClicked(object? sender, RoutedEventArgs e)
    {
        var logPath = Path.Combine(ParakeetService.LinuxDataDirectory, "setup-log.txt");
        if (!File.Exists(logPath))
        {
            SetupStatus.Text = $"No setup log yet at {logPath}";
            return;
        }
        OpenInBrowser(logPath);
    }

    // --- Dictation queue ---------------------------------------------------

    /// <summary>
    /// One finished recording waiting to be transcribed, cleaned and pasted.
    /// Unlike Windows there is no captured insertion target: Wayland cannot
    /// re-focus a window, so the text goes to whatever holds focus at paste
    /// time — in practice, the field the user is dictating into.
    /// </summary>
    private sealed record DictationJob(string RecordingPath, HotkeyKind Kind);

    private readonly Queue<DictationJob> _jobs = new();

    private async void OnHotkeyPressed(object? sender, HotkeyEventArgs e)
    {
        if (_audio.IsRecording) return;
        if (SetupBanner.IsVisible) return;
        try
        {
            _activeHotkeyKind = e.Kind;
            _tapLatched = false;
            _tones.PlayPressed();
            await _audio.StartAsync(_lifetime?.Token ?? default);
            RunStatus.Text = e.Kind == HotkeyKind.Raw ? "Listening… (raw)" : "Listening…";
            _tray.SetState(TrayState.Listening);
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
        }
    }

    private void OnHotkeyLatched(object? sender, HotkeyEventArgs e)
    {
        if (!_audio.IsRecording || e.Kind != _activeHotkeyKind) return;
        _tapLatched = true;
        AppLog.Write($"Tap dictation latched on ({e.Kind})");
        RunStatus.Text = e.Kind == HotkeyKind.Raw
            ? "Listening… (raw — press again to stop)"
            : "Listening… (press again to stop)";
    }

    private async void OnHotkeyReleased(object? sender, HotkeyEventArgs e)
    {
        if (!_audio.IsRecording) return;
        if (e.Kind != _activeHotkeyKind) return;
        try
        {
            if (_tapLatched)
            {
                _tapLatched = false;
                AppLog.Write($"Tap dictation stopped ({e.Kind})");
            }
            _tones.PlayReleased();
            var recording = await _audio.StopAsync(_lifetime?.Token ?? default);
            if (string.IsNullOrWhiteSpace(recording))
            {
                SetQueueStatus("No audio recorded");
                return;
            }

            _jobs.Enqueue(new DictationJob(recording, _activeHotkeyKind));
            if (_jobs.Count > 1 || _busy)
            {
                AppLog.Write($"Dictation queued behind the one being processed ({_jobs.Count} waiting)");
            }
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            SetError(ex.Message);
            return;
        }

        await ProcessQueueAsync();
    }

    private async Task ProcessQueueAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            while (_jobs.Count > 0)
            {
                var cancellationToken = _lifetime?.Token ?? default;
                if (cancellationToken.IsCancellationRequested) break;
                if (!_audio.IsRecording) _tray.SetState(TrayState.Working);
                await ProcessJobAsync(_jobs.Dequeue(), cancellationToken);
            }
        }
        finally
        {
            _busy = false;
            if (!_audio.IsRecording && _parakeet.IsReady) _tray.SetState(TrayState.Ready);
        }
    }

    private async Task ProcessJobAsync(DictationJob job, CancellationToken cancellationToken)
    {
        string? recording = job.RecordingPath;
        try
        {
            SetQueueStatus("Transcribing locally…");
            var text = await _parakeet.TranscribeAsync(recording, cancellationToken);
            _audio.DeleteRecording(recording);
            recording = null;
            if (string.IsNullOrWhiteSpace(text))
            {
                SetQueueStatus("No speech detected");
                return;
            }

            if (_settings.AiEnabled && job.Kind != HotkeyKind.Raw)
            {
                SetQueueStatus($"Cleaning with {_settings.Provider}…");
                text = await _ai.ProcessAsync(
                    _settings.Provider,
                    _settings.ModelId,
                    _settings.Reasoning,
                    _settings.CustomInstruction,
                    text,
                    cancellationToken,
                    _settings.CodexFastMode);
            }

            TranscriptBox.Text = text;

            // Never paste while the user is holding the hotkey dictating the
            // next message. Wait for the release.
            while (_audio.IsRecording)
            {
                await Task.Delay(100, cancellationToken);
            }

            SetQueueStatus("Pasting into the focused field…");
            await _inserter.InsertTextAsync(text, cancellationToken);
            SetQueueStatus("Pasted");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            SetError(ex.Message);
        }
        finally
        {
            if (recording is not null) _audio.DeleteRecording(recording);
        }
    }

    private void SetQueueStatus(string text)
    {
        if (_audio.IsRecording) return;
        RunStatus.Text = _jobs.Count > 0 ? $"{text} · {_jobs.Count} waiting" : text;
    }

    // --- Settings and UI --------------------------------------------------

    private void ApplySettingsToUi()
    {
        _uiReady = false;
        _activeProvider = string.IsNullOrWhiteSpace(_settings.Provider)
            ? AiProviderService.Claude
            : _settings.Provider;
        HotkeyCaptureButton.Content = string.IsNullOrWhiteSpace(_settings.Hotkey)
            ? OptionalHotkeyUnsetLabel
            : _settings.Hotkey;
        RawHotkeyCaptureButton.Content = string.IsNullOrWhiteSpace(_settings.RawHotkey)
            ? OptionalHotkeyUnsetLabel
            : _settings.RawHotkey;
        RefreshMicrophoneList(_settings.Microphone);
        _audio.PreferredDeviceName = _settings.Microphone;
        KeepRunningInTrayCheck.IsChecked = _settings.KeepRunningInTray;
        MuteSoundCuesCheck.IsChecked = _settings.SoundCuesMuted;
        _tones.Muted = _settings.SoundCuesMuted;
        // The autostart file is the truth: the saved setting could be stale if
        // the entry was removed outside ShadowWhispr.
        var autostartActive = LinuxAutostartService.IsEnabled();
        StartAtLoginCheck.IsChecked = autostartActive;
        _settings.StartWithWindows = autostartActive;
        StartupStatus.Text = autostartActive
            ? "ShadowWhispr will start hidden in the tray when you log in."
            : string.Empty;
        AiEnabledCheck.IsChecked = _settings.AiEnabled;
        SelectComboText(ProviderCombo, _settings.Provider);
        InstructionBox.Text = _settings.CustomInstruction;
        AiOptions.IsEnabled = _settings.AiEnabled;
        AutoUpdateCheck.IsChecked = _settings.AutoUpdateEnabled;
        _uiReady = true;
    }

    // --- Microphone selection ---------------------------------------------

    private void RefreshMicrophoneList(string preferredName)
    {
        var wasReady = _uiReady;
        _uiReady = false;
        try
        {
            var devices = LinuxAudioRecorderService.ListMicrophones().ToList();
            MicrophoneDevice? preferred = null;
            if (!string.IsNullOrWhiteSpace(preferredName))
            {
                preferred = devices.FirstOrDefault(device =>
                    !device.IsSystemDefault &&
                    string.Equals(device.Label, preferredName, StringComparison.OrdinalIgnoreCase));
                if (preferred is null)
                {
                    // A saved microphone that is unplugged right now still gets
                    // an entry, so opening the app cannot silently erase it.
                    preferred = new MicrophoneDevice(null, preferredName);
                    devices.Add(preferred);
                }
            }

            MicCombo.ItemsSource = devices;
            MicCombo.SelectedItem = preferred ?? devices[0];
        }
        finally
        {
            _uiReady = wasReady;
        }
    }

    private void MicComboOpened(object? sender, EventArgs e) =>
        RefreshMicrophoneList(_settings.Microphone);

    private void MicChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady) return;
        ReadUiIntoSettings();
        _audio.PreferredDeviceName = _settings.Microphone;
        AppLog.Write($"Microphone set to '{(_settings.Microphone.Length == 0 ? "system default" : _settings.Microphone)}'");
        QueueAutoSave();
    }

    // --- AI provider ------------------------------------------------------

    private async void ProviderChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady) return;

        ReadUiIntoSettings();
        var previousProvider = _activeProvider;
        _activeProvider = _settings.Provider;
        if (!string.Equals(previousProvider, _activeProvider, StringComparison.OrdinalIgnoreCase))
        {
            AppLog.Write(
                $"Provider switched {previousProvider} -> {_activeProvider} " +
                $"(remembered effort for {_activeProvider}: {_settings.GetReasoningFor(_activeProvider) ?? "none yet"})");
        }

        _ = RefreshLoginStatusAsync(_settings.Provider);
        QueueAutoSave();
        await RefreshModelsAsync(_settings.GetModelFor(_activeProvider));
    }

    private async void LoginClicked(object? sender, RoutedEventArgs e)
    {
        var provider = GetComboText(ProviderCombo) ?? AiProviderService.Claude;
        SetAuthButtonsEnabled(false);
        AuthStatus.Text = $"Complete {provider} login in the terminal that opens…";
        try
        {
            await _ai.LoginAsync(provider, _lifetime?.Token ?? default);
            AuthStatus.Text = $"{provider} login finished";
            await RefreshModelsAsync(null);
            await RefreshLoginStatusAsync(provider);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLog.Write($"{provider} login failed: {ex.Message}");
            AuthStatus.Text = ex.Message;
        }
        finally
        {
            SetAuthButtonsEnabled(true);
        }
    }

    private async void LogoutClicked(object? sender, RoutedEventArgs e)
    {
        var provider = GetComboText(ProviderCombo) ?? AiProviderService.Claude;
        if (!await ConfirmAsync("ShadowWhispr", $"Log out of {provider}?", "Log out", "Cancel")) return;

        SetAuthButtonsEnabled(false);
        AuthStatus.Text = provider == AiProviderService.Gemini
            ? "In Antigravity, type /logout, then /quit"
            : $"Logging out of {provider}…";
        try
        {
            await _ai.LogoutAsync(provider, _lifetime?.Token ?? default);
            AuthStatus.Text = provider == AiProviderService.Gemini
                ? "Antigravity window closed"
                : $"Logged out of {provider}";
            ModelCombo.ItemsSource = Array.Empty<AiModelOption>();
            ReasoningCombo.ItemsSource = Array.Empty<string>();
            UpdateFastModeChoice(null);
            _providerLoggedIn = false;
            await RefreshLoginStatusAsync(provider);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLog.Write($"{provider} logout failed: {ex.Message}");
            AuthStatus.Text = ex.Message;
        }
        finally
        {
            SetAuthButtonsEnabled(true);
        }
    }

    private void SetAuthButtonsEnabled(bool enabled)
    {
        LoginButton.IsEnabled = enabled && !_providerLoggedIn;
        LogoutButton.IsEnabled = enabled;
        ProviderCombo.IsEnabled = enabled;
    }

    private void SetAuthHint(string provider)
    {
        AuthStatus.Text = provider switch
        {
            AiProviderService.Gemini => "Login uses Google Antigravity",
            _ => $"Login uses your {provider} subscription"
        };
    }

    private async Task RefreshLoginStatusAsync(string provider)
    {
        var generation = ++_loginStatusGeneration;

        _providerLoggedIn = false;
        LoginButton.IsEnabled = false;
        AuthStatus.Text = "Checking login…";

        ProviderLoginStatus status;
        try
        {
            status = await _ai.GetLoginStatusAsync(provider, _lifetime?.Token ?? default);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            AppLog.Write($"Checking the {provider} login failed", ex);
            status = ProviderLoginStatus.Unknown;
        }

        if (generation != _loginStatusGeneration) return;
        if (!string.Equals(GetComboText(ProviderCombo), provider, StringComparison.OrdinalIgnoreCase)) return;

        _providerLoggedIn = status == ProviderLoginStatus.LoggedIn;
        if (_providerLoggedIn)
        {
            LoginButton.IsEnabled = false;
            AuthStatus.Text = "Already logged in";
        }
        else if (ProviderCombo.IsEnabled)
        {
            LoginButton.IsEnabled = true;
            SetAuthHint(provider);
        }
    }

    private async Task RefreshModelsAsync(string? preferredId)
    {
        var provider = GetComboText(ProviderCombo) ?? AiProviderService.Claude;
        var generation = ++_modelRefreshGeneration;
        _modelRefresh?.Cancel();
        var refresh = CancellationTokenSource.CreateLinkedTokenSource(_lifetime?.Token ?? default);
        _modelRefresh = refresh;
        var cancellationToken = refresh.Token;
        ModelCombo.IsEnabled = false;
        ReasoningCombo.IsEnabled = false;
        RunStatus.Text = $"Checking {provider} models…";
        try
        {
            var models = await _ai.DiscoverModelsAsync(provider, cancellationToken);
            if (generation != _modelRefreshGeneration || cancellationToken.IsCancellationRequested) return;
            ModelCombo.ItemsSource = models;
            ModelCombo.SelectedItem = models.FirstOrDefault(model => model.Id == preferredId) ?? models.FirstOrDefault();
            UpdateReasoningChoices();
            RunStatus.Text = models.Count == 0 ? $"{provider} is not available" : "Waiting for speech";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (generation != _modelRefreshGeneration) return;
            AppLog.Write($"Discovering {provider} models failed: {ex.Message}");
            ModelCombo.ItemsSource = Array.Empty<AiModelOption>();
            ReasoningCombo.ItemsSource = Array.Empty<string>();
            UpdateFastModeChoice(null);
            RunStatus.Text = ex.Message;
        }
        finally
        {
            if (generation == _modelRefreshGeneration) ModelCombo.IsEnabled = true;
            if (ReferenceEquals(_modelRefresh, refresh)) _modelRefresh = null;
            refresh.Dispose();
        }
    }

    private void ModelChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady) return;
        UpdateReasoningChoices();
        ReadUiIntoSettings();
        QueueAutoSave();
    }

    private void UpdateReasoningChoices()
    {
        if (ModelCombo.SelectedItem is not AiModelOption model)
        {
            ReasoningCombo.ItemsSource = Array.Empty<string>();
            ReasoningCombo.IsEnabled = false;
            UpdateFastModeChoice(null);
            return;
        }

        ReasoningCombo.ItemsSource = model.ReasoningLevels;

        var remembered = _settings.GetReasoningFor(_activeProvider);
        var preferred = remembered is not null && model.ReasoningLevels.Contains(remembered)
            ? remembered
            : model.DefaultReasoningLevel;
        ReasoningCombo.SelectedItem = preferred ?? model.ReasoningLevels.FirstOrDefault();
        ReasoningCombo.IsEnabled = model.ReasoningLevels.Count > 0;
        UpdateFastModeChoice(model);
    }

    private void UpdateFastModeChoice(AiModelOption? model)
    {
        var supported = model?.SupportsFastMode == true;
        FastModePanel.IsVisible = supported;
        FastModeCheck.IsChecked = supported && _settings.CodexFastMode;
    }

    private void AiToggleChanged(object? sender, RoutedEventArgs e)
    {
        if (AiOptions is null) return;
        AiOptions.IsEnabled = AiEnabledCheck.IsChecked == true;
        SettingsChanged(sender, e);
    }

    private void SettingsChanged(object? sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;
        ReadUiIntoSettings();
        _hotkey.Hotkey = ParseOptionalHotkey(_settings.Hotkey);
        QueueAutoSave();
    }

    // --- Hotkey capture ---------------------------------------------------

    /// <summary>
    /// Listens (through the same evdev reader as dictation) for the next key
    /// and stores it as this field's binding, with the Windows capture rules:
    /// Escape cancels, Delete clears, a modifier alone binds on release.
    /// </summary>
    private async void HotkeyCaptureClicked(object? sender, RoutedEventArgs e)
    {
        if (_capturingHotkeyButton is not null) return;
        if (sender is not Button button) return;

        _capturingHotkeyButton = button;
        var labelBefore = button.Content?.ToString() ?? OptionalHotkeyUnsetLabel;
        button.Content = "Press a key or combination…";

        HotkeyCaptureResult result;
        try
        {
            result = await _hotkey.CaptureNextAsync(_lifetime?.Token ?? default);
        }
        catch (Exception ex)
        {
            AppLog.Write("Hotkey capture failed", ex);
            button.Content = labelBefore;
            _capturingHotkeyButton = null;
            HotkeyHint.Text = $"Could not listen for a key: {ex.Message}";
            return;
        }

        _capturingHotkeyButton = null;

        var isRaw = ReferenceEquals(button, RawHotkeyCaptureButton);
        var fieldName = isRaw ? "raw" : "main";
        string OwnStoredValue() => isRaw ? _settings.RawHotkey : _settings.Hotkey;

        string text;
        if (result.Cancelled) text = labelBefore;
        else if (result.Cleared) text = string.Empty;
        else text = result.Hotkey?.ToString() ?? labelBefore;

        // Cancelling on an unassigned optional field restores its placeholder,
        // which is a label rather than a hotkey and must not be stored as one.
        if (text == OptionalHotkeyUnsetLabel) text = string.Empty;

        // One keypress must never mean two things, so a binding that duplicates
        // the other field is refused instead of silently shadowing it.
        if (text.Length > 0)
        {
            string[] all = [_settings.Hotkey, _settings.RawHotkey];
            var others = all.Where(other => !string.Equals(other, OwnStoredValue(), StringComparison.Ordinal));
            if (others.Any(other => string.Equals(text, other, StringComparison.OrdinalIgnoreCase)))
            {
                AppLog.Write($"Rejected duplicate hotkey '{text}' for the {fieldName} binding");
                HotkeyHint.Text = $"'{text}' is already used by another hotkey — pick a different key.";
                text = OwnStoredValue();
            }
            else
            {
                ResetHotkeyHint();
            }
        }
        else
        {
            ResetHotkeyHint();
        }

        button.Content = text.Length == 0 ? OptionalHotkeyUnsetLabel : text;

        if (isRaw)
        {
            _settings.RawHotkey = text;
            _hotkey.RawHotkey = ParseOptionalHotkey(text);
        }
        else
        {
            _settings.Hotkey = text;
            _hotkey.Hotkey = ParseOptionalHotkey(text);
        }
        AppLog.Write($"{char.ToUpper(fieldName[0])}{fieldName[1..]} hotkey set to '{(text.Length == 0 ? "(none)" : text)}'");

        _hotkey.Enabled = !_dictationPaused;
        UpdateTrayStatus();
        QueueAutoSave();
    }

    private void ResetHotkeyHint() => HotkeyHint.Text =
        "Hold a key while you speak, or tap it quickly to keep recording hands-free until you press it again. " +
        "Click a field, then press the key you want. Delete unbinds it; Escape cancels.";

    private void ReadUiIntoSettings()
    {
        // While a field is mid-capture its label is a prompt, not a hotkey.
        if (_capturingHotkeyButton is null)
        {
            var main = HotkeyCaptureButton.Content?.ToString() ?? string.Empty;
            _settings.Hotkey = main == OptionalHotkeyUnsetLabel ? string.Empty : main;
            var raw = RawHotkeyCaptureButton.Content?.ToString() ?? string.Empty;
            _settings.RawHotkey = raw == OptionalHotkeyUnsetLabel ? string.Empty : raw;
        }
        // The system-default entry is stored as empty; a disconnected saved mic
        // keeps its real name in the list, so its name (not "default") persists.
        if (MicCombo.SelectedItem is MicrophoneDevice mic)
        {
            _settings.Microphone =
                string.Equals(mic.Label, MicrophoneDevice.SystemDefaultLabel, StringComparison.OrdinalIgnoreCase)
                    ? string.Empty
                    : mic.Label;
        }
        _settings.KeepRunningInTray = KeepRunningInTrayCheck.IsChecked == true;
        _settings.StartWithWindows = StartAtLoginCheck.IsChecked == true;
        _settings.SoundCuesMuted = MuteSoundCuesCheck.IsChecked == true;
        _tones.Muted = _settings.SoundCuesMuted;
        _settings.AiEnabled = AiEnabledCheck.IsChecked == true;
        _settings.Provider = GetComboText(ProviderCombo) ?? AiProviderService.Claude;
        if (ModelCombo.SelectedItem is AiModelOption model)
        {
            _settings.ModelId = model.Id;
            _settings.SetModelFor(_activeProvider, model.Id);
        }
        var reasoning = ReasoningCombo.SelectedItem as string ?? string.Empty;
        _settings.Reasoning = reasoning;
        _settings.SetReasoningFor(_activeProvider, reasoning);
        // Only read while the box is actually on screen; it is hidden for every
        // non-Codex model, and reading it then would quietly clear the choice.
        if (FastModePanel.IsVisible)
        {
            _settings.CodexFastMode = FastModeCheck.IsChecked == true;
        }
        _settings.CustomInstruction = string.IsNullOrWhiteSpace(InstructionBox.Text)
            ? new AppSettings().CustomInstruction
            : InstructionBox.Text.Trim();
        _settings.AutoUpdateEnabled = AutoUpdateCheck.IsChecked == true;
    }

    // --- Automatic saving -------------------------------------------------

    private void QueueAutoSave()
    {
        if (!_uiReady || !_startupComplete) return;
        SetSaveStatus("Saving…", WorkingGold);
        _autoSaveTimer ??= CreateTimer(AutoSaveDelay, SaveSettingsNow);
        _autoSaveTimer.Stop();
        _autoSaveTimer.Start();
    }

    private void SaveSettingsNow()
    {
        _autoSaveTimer?.Stop();
        if (!_startupComplete)
        {
            AppLog.Write("Skipped a settings save: startup has not completed");
            return;
        }
        ReadUiIntoSettings();
        try
        {
            _settingsService.Save(_settings);
        }
        catch (Exception ex)
        {
            AppLog.Write("Auto-saving settings failed", ex);
            SetSaveStatus($"Not saved: {ex.Message}", ErrorRed);
            SetError($"Could not save settings: {ex.Message}");
            return;
        }
        AppLog.Write("Settings auto-saved");
        SetSaveStatus($"Saved at {DateTime.Now:HH:mm:ss}", ReadyGreen);
    }

    private void SetSaveStatus(string text, IBrush color)
    {
        SaveStatus.Text = text;
        SaveStatus.Foreground = color;
        SaveDot.Fill = color;
    }

    private void SetEngine(string text, IBrush color)
    {
        Dispatcher.UIThread.Post(() => { EngineStatus.Text = text; EngineDot.Fill = color; });
    }

    private void SetError(string message)
    {
        AppLog.Write($"ERROR shown to user: {message}");
        Dispatcher.UIThread.Post(() =>
        {
            RunStatus.Text = "Error";
            TranscriptBox.Text = message;
            _tray.SetState(TrayState.Error);
        });
    }

    private static string? GetComboText(ComboBox combo) =>
        combo.SelectedItem is ComboBoxItem item ? item.Content?.ToString() : combo.SelectedItem?.ToString();

    private static void SelectComboText(ComboBox combo, string value)
    {
        foreach (var candidate in combo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(candidate.Content?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = candidate;
                return;
            }
        }
        combo.SelectedIndex = 0;
    }

    private static HoldHotkey? ParseOptionalHotkey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == OptionalHotkeyUnsetLabel) return null;
        return HoldHotkey.TryParse(value, out var hotkey) ? hotkey : null;
    }

    // --- Pause ------------------------------------------------------------

    private void SetDictationPaused(bool paused)
    {
        if (_dictationPaused == paused) return;
        _dictationPaused = paused;
        AppLog.Write(paused ? "Dictation paused" : "Dictation resumed");

        // Capture mode owns Enabled while it is listening for a new binding;
        // the capture handler re-applies the paused state afterwards.
        if (_capturingHotkeyButton is null) _hotkey.Enabled = !paused;

        _tray.Paused = paused;
        PauseButton.Content = paused ? "Resume dictation" : "Pause dictation";
        UpdateTrayStatus();
    }

    private void PauseClicked(object? sender, RoutedEventArgs e) =>
        SetDictationPaused(!_dictationPaused);

    private void UpdateTrayStatus()
    {
        string status;
        if (SetupBanner.IsVisible) status = "Setup needed";
        else if (!_parakeet.IsReady) status = "Starting…";
        else if (_dictationPaused) status = "Paused";
        else
        {
            var parts = new List<string>(2);
            if (!string.IsNullOrWhiteSpace(_settings.Hotkey)) parts.Add($"Hold or tap {_settings.Hotkey}");
            if (!string.IsNullOrWhiteSpace(_settings.RawHotkey)) parts.Add($"raw {_settings.RawHotkey}");
            status = parts.Count == 0 ? "No hotkey set" : string.Join(" · ", parts);
        }

        _tray.SetStatus(status);
    }

    // --- Shutdown ---------------------------------------------------------

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        // Closing the window is not quitting: by default ShadowWhispr keeps
        // running in the tray so the hold hotkey still works. Only the tray's
        // Quit item (which sets _exitRequested) actually shuts things down.
        if (!_exitRequested && _settings.KeepRunningInTray)
        {
            e.Cancel = true;
            Hide();
            SaveSettingsNow();
            AppLog.Write("Main window closed to the tray; hotkeys stay active");
            _tray.ShowMessage(
                "ShadowWhispr is still running",
                "Your hotkey still works. Quit from this tray icon to stop it.");
            return;
        }

        e.Cancel = true; // ShutdownNow tears everything down and exits for real.
        ShutdownNow();
    }

    private void ShutdownNow()
    {
        if (_shuttingDown) return;
        _shuttingDown = true;

        AppLog.Write("App closing");
        // Quit must always quit: a hung dispose step used to leave a zombie
        // process holding the single-instance lock, so nothing could relaunch.
        new Thread(() =>
        {
            Thread.Sleep(TimeSpan.FromSeconds(10));
            AppLog.Write("Clean shutdown took too long; exiting forcefully");
            Environment.Exit(0);
        })
        { IsBackground = true, Name = "shutdown-watchdog" }.Start();
        // Each shutdown step is isolated and logged so one failure can neither
        // hide from the log nor prevent the remaining cleanup from running.
        if (_startupComplete)
            RunLogged("save settings on close", () => { ReadUiIntoSettings(); _settingsService.Save(_settings); });
        else
            AppLog.Write("Skipped saving settings on close: startup never completed");
        RunLogged("cancel pending work", () => { _lifetime?.Cancel(); _modelRefresh?.Cancel(); });
        RunLogged("stop update timers", () => { _updatePollTimer?.Stop(); _autoSaveTimer?.Stop(); });
        RunLogged("stop hotkey listener", _hotkey.Dispose);
        RunLogged("stop tone player", _tones.Dispose);
        RunLogged("stop audio recorder", _audio.Dispose);
        RunLogged("stop text inserter", _inserter.Dispose);
        RunLogged("stop speech engine", () => _parakeet.DisposeAsync().AsTask().GetAwaiter().GetResult());
        RunLogged("remove tray icon", _tray.Dispose);
        RunLogged("release token sources", () => { _modelRefresh?.Dispose(); _lifetime?.Dispose(); });

        RunLogged("shut down the application", () =>
        {
            if (App.Current?.ApplicationLifetime is
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown();
            }
        });
    }

    private static void RunLogged(string step, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            AppLog.Write($"Shutdown step failed: {step}", ex);
        }
    }

    /// <summary>A minimal two-button modal, standing in for WPF's MessageBox.</summary>
    private async Task<bool> ConfirmAsync(string title, string message, string yesLabel, string noLabel)
    {
        var result = false;
        var dialog = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = new SolidColorBrush(Color.FromRgb(21, 25, 31)),
            MaxWidth = 460
        };

        var yes = new Button { Content = yesLabel };
        var no = new Button { Content = noLabel, Classes = { "secondary" } };
        yes.Click += (_, _) => { result = true; dialog.Close(); };
        no.Click += (_, _) => dialog.Close();

        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(24),
            Spacing = 18,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 14 },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { no, yes }
                }
            }
        };

        if (IsVisible)
        {
            await dialog.ShowDialog(this);
        }
        else
        {
            // The main window can be hidden in the tray; a dialog needs to be
            // shown standalone then, and awaited by hand.
            var closed = new TaskCompletionSource();
            dialog.Closed += (_, _) => closed.TrySetResult();
            dialog.Show();
            await closed.Task;
        }

        return result;
    }
}
