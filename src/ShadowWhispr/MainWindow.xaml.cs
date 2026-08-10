using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ShadowWhispr.Models;
using ShadowWhispr.Services;

namespace ShadowWhispr;

public partial class MainWindow : Window
{
    private static readonly Brush ReadyGreen = new SolidColorBrush(Color.FromRgb(83, 211, 137));
    private static readonly Brush WorkingGold = new SolidColorBrush(Color.FromRgb(231, 184, 92));
    private static readonly Brush ErrorRed = new SolidColorBrush(Color.FromRgb(223, 74, 74));
    private static readonly Brush LineGray = new SolidColorBrush(Color.FromRgb(53, 65, 78));

    /// <summary>Shown in an optional hotkey field when no key is assigned.</summary>
    private const string OptionalHotkeyUnsetLabel = "Not set";

    private static readonly TimeSpan AutoSaveDelay = TimeSpan.FromMilliseconds(600);

    private readonly SettingsService _settingsService = new();
    private readonly ParakeetService _parakeet = new();
    private readonly AiProviderService _ai = new();
    private readonly GlobalHotkeyService _hotkey = new();
    private readonly AudioRecorderService _audio = new();
    private readonly TonePlayer _tones = new();
    private readonly TextInsertionService _inserter = new();
    private readonly UpdateService _updates = new();
    private readonly TrayIconService _tray = new();
    private readonly SpeechSetupService _speechSetup = new();
    private string? _pendingInstallerPath;
    private System.Windows.Threading.DispatcherTimer? _updatePollTimer;
    private System.Windows.Threading.DispatcherTimer? _updateRepromptTimer;
    private System.Windows.Threading.DispatcherTimer? _autoSaveTimer;
    private bool _updateCheckInProgress;
    private bool _updatePromptOpen;
    private DateTime _suppressAutoPromptUntil = DateTime.MinValue;

    private AppSettings _settings = new();
    private TextInsertionTarget _insertionTarget;
    private bool _uiReady;
    private bool _startupComplete;
    private bool _busy;
    private CancellationTokenSource? _lifetime;
    private CancellationTokenSource? _modelRefresh;
    private int _modelRefreshGeneration;
    /// <summary>
    /// The provider whose settings the UI is currently showing. This is not
    /// always what the provider combo says: when the user picks a new provider
    /// the combo changes first, while the model and reasoning lists still belong
    /// to the old one - and the old one is who that reasoning must be saved under.
    /// </summary>
    private string _activeProvider = AiProviderService.Claude;

    private Button? _capturingHotkeyButton;
    private string _hotkeyBeforeCapture = "Right Ctrl";
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

    private double _setupPercent;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closing += OnClosing;
        _hotkey.Pressed += OnHotkeyPressed;
        _hotkey.Released += OnHotkeyReleased;
        _hotkey.Latched += OnHotkeyLatched;
        _audio.RecordingFailed += (_, ex) => AppLog.Write("Audio recording failed", ex);
        _tones.PlaybackFailed += (_, ex) => AppLog.Write("Cue tone playback failed", ex);
        _speechSetup.Progress += OnSetupProgress;
        _tray.OpenRequested += (_, _) => ShowFromTray();
        _tray.QuitRequested += (_, _) => RequestExit();
        _tray.PauseToggled += (_, paused) => SetDictationPaused(paused);
        _tray.CheckUpdatesRequested += (_, _) =>
        {
            ShowFromTray();
            _ = RunUpdateCheckAsync(manual: true, _lifetime?.Token ?? default);
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        AppLog.Write($"App started (version {typeof(MainWindow).Assembly.GetName().Version})");
        _lifetime = new CancellationTokenSource();
        _settings = _settingsService.Load();
        ApplySettingsToUi();
        SetAuthHint(_settings.Provider);
        _tray.Visible = true;
        _tray.SetStatus("Starting…");
        _tray.SetState(TrayState.Starting);
        if (Application.Current is App app)
        {
            app.ShowWindowRequested += (_, _) => ShowFromTray();
            if (app.StartHiddenInTray)
            {
                // Launched by Windows at login: stay out of the way entirely.
                // App showed this minimized and off-taskbar to get here without
                // anything appearing on screen; restore both for when the user
                // later opens it from the tray.
                AppLog.Write("Started with --tray; hiding the main window");
                Hide();
                ShowInTaskbar = true;
                WindowState = WindowState.Normal;
            }
        }

        try
        {
            _hotkey.Hotkey = ParseOptionalHotkey(_settings.Hotkey);
            _hotkey.RawHotkey = ParseOptionalHotkey(_settings.RawHotkey);
            _hotkey.Start();
        }
        catch (Exception ex)
        {
            AppLog.Write("Starting the global hotkey hook failed", ex);
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

    /// <summary>Brings the window back from the tray and puts it in front.</summary>
    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    /// <summary>The only path that actually quits: the tray menu's Quit item.</summary>
    private void RequestExit()
    {
        AppLog.Write("Quit requested from the tray menu");
        _exitRequested = true;
        Close();
    }

    private void TrayOptionToggled(object sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;
        ReadUiIntoSettings();
        AppLog.Write($"Keep running in tray set to {_settings.KeepRunningInTray}");
    }

    private void SoundCuesToggled(object sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;
        ReadUiIntoSettings();
        AppLog.Write($"Dictation sound cues muted set to {_settings.SoundCuesMuted}");
    }

    /// <summary>
    /// Writes (or removes) the Windows startup entry. The checkbox is snapped
    /// back to the real registry state if Windows refuses the change, so it can
    /// never claim autostart is on when it isn't.
    /// </summary>
    private void StartWithWindowsToggled(object sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;

        var wanted = StartWithWindowsCheck.IsChecked == true;
        var minimized = StartMinimizedCheck.IsChecked == true;
        if (StartupService.Apply(wanted, minimized))
        {
            StartupStatus.Text = DescribeAutostart(wanted, minimized);
        }
        else
        {
            StartupStatus.Text = "Windows refused this change — see app-log.txt";
            _uiReady = false;
            StartWithWindowsCheck.IsChecked = StartupService.IsEnabled();
            _uiReady = true;
        }

        ReadUiIntoSettings();
    }

    /// <summary>
    /// The "start minimized" sub-option. It only ever rewrites the existing Run
    /// entry, so it cannot switch autostart on by itself; the checkbox is
    /// disabled in XAML while autostart is off.
    /// </summary>
    private void StartMinimizedToggled(object sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;

        var minimized = StartMinimizedCheck.IsChecked == true;
        if (StartWithWindowsCheck.IsChecked == true)
        {
            if (StartupService.Apply(enabled: true, startMinimized: minimized))
            {
                StartupStatus.Text = DescribeAutostart(true, minimized);
            }
            else
            {
                StartupStatus.Text = "Windows refused this change — see app-log.txt";
                _uiReady = false;
                StartMinimizedCheck.IsChecked = StartupService.StartsMinimized();
                _uiReady = true;
            }
        }

        ReadUiIntoSettings();
    }

    /// <summary>One sentence describing exactly what will happen at the next login.</summary>
    private static string DescribeAutostart(bool enabled, bool minimized) => enabled
        ? minimized
            ? "ShadowWhispr will start hidden in the tray when you log in."
            : "ShadowWhispr will open its window when you log in."
        : string.Empty;

    // --- Automatic + manual update checking ------------------------------

    private void StartUpdatePolling()
    {
        _updatePollTimer ??= CreateTimer(TimeSpan.FromMinutes(10),
            () => _ = RunUpdateCheckAsync(manual: false, _lifetime?.Token ?? default));
        _updatePollTimer.Start();
    }

    private static System.Windows.Threading.DispatcherTimer CreateTimer(TimeSpan interval, Action onTick)
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = interval };
        timer.Tick += (_, _) => onTick();
        return timer;
    }

    /// <summary>
    /// Checks GitHub for a newer release. Automatic checks stay silent unless a
    /// newer version is found, in which case the user is prompted — never
    /// auto-installed. Manual checks always report their result.
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
                // Don't interrupt a dictation in progress or stack a second
                // prompt; the next poll or the 5-minute reminder will catch it.
                if (_updatePromptOpen || _busy || _audio.IsRecording) return;
                if (DateTime.Now < _suppressAutoPromptUntil) return;
            }

            await PromptForUpdateAsync(update, cancellationToken);
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

    private async Task PromptForUpdateAsync(UpdateInfo update, CancellationToken cancellationToken)
    {
        if (_updatePromptOpen) return;
        _updatePromptOpen = true;
        try
        {
            AppLog.Write($"Showing update prompt for {update.Tag}");
            var prompt = new UpdatePromptWindow(update.Tag, update.Changelog) { Owner = this };
            prompt.ShowDialog();
            AppLog.Write($"Update prompt choice for {update.Tag}: {prompt.Choice}");

            switch (prompt.Choice)
            {
                case UpdateChoice.InstallNow:
                    await InstallUpdateAsync(update, restartAfter: true, cancellationToken);
                    break;
                case UpdateChoice.InstallOnClose:
                    await InstallUpdateAsync(update, restartAfter: false, cancellationToken);
                    break;
                default:
                    // Declined: remind again in 5 minutes (and on next launch).
                    if (_settings.AutoUpdateEnabled)
                    {
                        _suppressAutoPromptUntil = DateTime.Now.AddMinutes(5);
                        _updateRepromptTimer ??= CreateTimer(TimeSpan.FromMinutes(5), () =>
                        {
                            _updateRepromptTimer!.Stop();
                            _ = RunUpdateCheckAsync(manual: false, _lifetime?.Token ?? default);
                        });
                        _updateRepromptTimer.Stop();
                        _updateRepromptTimer.Start();
                        SetUpdateStatus($"Update {update.Tag} available — you'll be reminded in 5 minutes.");
                    }
                    else
                    {
                        SetUpdateStatus($"Update {update.Tag} available.");
                    }
                    break;
            }
        }
        finally
        {
            _updatePromptOpen = false;
        }
    }

    private async Task InstallUpdateAsync(UpdateInfo update, bool restartAfter, CancellationToken cancellationToken)
    {
        try
        {
            SetUpdateStatus($"Downloading update {update.Tag}…");
            var installerPath = await _updates.DownloadInstallerAsync(update, cancellationToken);
            if (cancellationToken.IsCancellationRequested) return;
            if (installerPath is null)
            {
                SetUpdateStatus("Update download failed — see app-log.txt");
                return;
            }

            if (restartAfter)
            {
                SetUpdateStatus($"Installing {update.Tag}… ShadowWhispr will reopen.");
                var appExe = Environment.ProcessPath;
                if (appExe is not null && UpdateService.InstallNowAndRestart(installerPath, appExe))
                {
                    // A real exit, not a hide-to-tray: the installer needs this
                    // process gone before it can replace the files.
                    _exitRequested = true;
                    Close();
                }
                else
                {
                    SetUpdateStatus("Could not start the installer — see app-log.txt");
                }
            }
            else
            {
                _pendingInstallerPath = installerPath;
                SetUpdateStatus(
                    $"Update {update.Tag} will install when you close this window — " +
                    "ShadowWhispr will fully close rather than stay in the tray.");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLog.Write("Installing the update failed", ex);
            SetUpdateStatus("Update failed — see app-log.txt");
        }
    }

    private void SetUpdateStatus(string text) => Dispatcher.Invoke(() => UpdateStatus.Text = text);

    private void CheckForUpdatesClicked(object sender, RoutedEventArgs e) =>
        _ = RunUpdateCheckAsync(manual: true, _lifetime?.Token ?? default);

    private void AutoUpdateToggled(object sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;
        ReadUiIntoSettings();
        QueueAutoSave();
        if (_settings.AutoUpdateEnabled)
        {
            AppLog.Write("Auto-update enabled by user");
            StartUpdatePolling();
            _ = RunUpdateCheckAsync(manual: false, _lifetime?.Token ?? default);
        }
        else
        {
            AppLog.Write("Auto-update turned off by user");
            _updatePollTimer?.Stop();
            _updateRepromptTimer?.Stop();
        }
    }

    private async Task WarmSpeechEngineAsync(CancellationToken cancellationToken)
    {
        try
        {
            SetEngine("Loading Parakeet…", WorkingGold);
            _tray.SetState(TrayState.Starting);
            await _parakeet.StartAsync(cancellationToken);
            AppLog.Write($"Speech engine ready on {_parakeet.Device}");
            SetupBanner.Visibility = Visibility.Collapsed;
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
                ? "Setup didn't finish. The error stays visible in the PowerShell window and is saved to setup-log.txt in the app folder. Click to try again — it resumes where it left off."
                : string.Empty;
            SetupBanner.Visibility = Visibility.Visible;
            UpdateTrayStatus();
        }
        catch (Exception ex)
        {
            AppLog.Write("Speech engine start failed", ex);
            SetEngine("Parakeet needs attention", ErrorRed);
            _tray.SetStatus("Needs attention");
            _tray.SetState(TrayState.Error);
            SetError(ex.Message);
            if (SetupBanner.Visibility == Visibility.Visible)
            {
                SetupRunButton.IsEnabled = true;
                SetupStatus.Text = $"The speech engine could not start: {ex.Message}";
            }
        }
    }

    private async void SetupRunClicked(object sender, RoutedEventArgs e)
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
        SetupLogButton.Visibility = Visibility.Visible;
        SetupProgressPanel.Visibility = Visibility.Visible;
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
                $"{failure}\n\nThe exact cause is shown above and saved in the setup log. " +
                "Click \"Set up speech now\" to try again — it resumes where it left off.";
            SetupRunButton.IsEnabled = true;
            return;
        }

        SetSetupProgress(100, "Starting the speech engine…");
        SetupStatus.Text = "Almost done — starting the speech engine (this can take a minute)…";
        await WarmSpeechEngineAsync(_lifetime?.Token ?? default);
    }

    private void OnSetupProgress(object? sender, SetupProgressEventArgs e) =>
        Dispatcher.Invoke(() => SetSetupProgress(e.Percent, e.Message));

    private void SetSetupProgress(double percent, string message)
    {
        _setupPercent = Math.Clamp(percent, 0, 100);
        SetupStepText.Text = message;
        SetupPercentText.Text = $"{_setupPercent:0}%";
        UpdateSetupProgressBarWidth();
    }

    private void SetupProgressTrackSizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateSetupProgressBarWidth();

    private void UpdateSetupProgressBarWidth()
    {
        var available = SetupProgressTrack.ActualWidth - SetupProgressTrack.BorderThickness.Left
                        - SetupProgressTrack.BorderThickness.Right;
        if (available <= 0) return;
        SetupProgressFill.Width = available * (_setupPercent / 100d);
    }

    private void OpenSetupLogClicked(object sender, RoutedEventArgs e)
    {
        // The setup script writes setup-log.txt beside the app, one level above
        // the scripts folder it lives in.
        var scriptDirectory = Path.GetDirectoryName(_setupScriptPath) ?? AppContext.BaseDirectory;
        var logPath = Path.GetFullPath(Path.Combine(scriptDirectory, "..", "setup-log.txt"));
        try
        {
            if (!File.Exists(logPath))
            {
                SetupStatus.Text = $"No setup log yet at {logPath}";
                return;
            }
            Process.Start(new ProcessStartInfo(logPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Write($"Could not open the setup log at {logPath}", ex);
            SetupStatus.Text = $"Could not open the setup log: {ex.Message}";
        }
    }

    // --- Dictation queue ---------------------------------------------------

    /// <summary>
    /// One finished recording waiting to be transcribed, cleaned and pasted.
    /// The insertion target is the one captured when its hotkey went down, so
    /// each queued message still lands in the field the user was dictating into.
    /// </summary>
    private sealed record DictationJob(string RecordingPath, HotkeyKind Kind, TextInsertionTarget Target);

    private readonly Queue<DictationJob> _jobs = new();

    private async void OnHotkeyPressed(object? sender, HotkeyEventArgs e)
    {
        // A recording can start while an earlier message is still being
        // processed; only an already-running recording blocks a new one.
        if (_audio.IsRecording) return;
        if (SetupBanner.Visibility == Visibility.Visible) return;
        try
        {
            _activeHotkeyKind = e.Kind;
            _tapLatched = false;
            _tones.PlayPressed();
            _insertionTarget = _inserter.CaptureTarget();
            await _audio.StartAsync(_lifetime?.Token ?? default);
            RunStatus.Text = e.Kind == HotkeyKind.Raw ? "Listening… (raw)" : "Listening…";
            _tray.SetState(TrayState.Listening);
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
        }
    }

    /// <summary>
    /// The key was let go too quickly to be a hold, so the recording keeps
    /// running until the next press. Only the status text changes; the recording
    /// itself started when the key went down.
    /// </summary>
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
        // Only the hotkey that started this recording may end it: a stray tap
        // release must not cut a hold dictation short, and the other way round.
        if (e.Kind != _activeHotkeyKind) return;
        try
        {
            if (_tapLatched)
            {
                _tapLatched = false;
                AppLog.Write($"Tap dictation stopped ({e.Kind})");
            }
            // Sounded before the recorder is closed, not after: stopping flushes
            // the recording to disk, and waiting for that put an audible lag
            // between letting the key go and hearing the cue.
            _tones.PlayReleased();
            var recording = await _audio.StopAsync(_lifetime?.Token ?? default);
            if (string.IsNullOrWhiteSpace(recording))
            {
                SetQueueStatus("No audio recorded");
                return;
            }

            _jobs.Enqueue(new DictationJob(recording, _activeHotkeyKind, _insertionTarget));
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

    /// <summary>
    /// Works through queued dictations one at a time. Re-entrant calls (a new
    /// recording finishing while an earlier one is still processing) just leave
    /// their job in the queue for the already-running loop to pick up.
    /// </summary>
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

    /// <summary>
    /// Transcribes, cleans and pastes one dictation. A failure here only skips
    /// this message; the rest of the queue still gets processed.
    /// </summary>
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

            // Voice command check: ONLY on regular Parakeet speech keybind!
            // Raw hotkey always qualifies, or primary hotkey when AI cleanup is disabled.
            bool isRegularSpeechKeybind = job.Kind == HotkeyKind.Raw || !_settings.AiEnabled;
            if (_settings.VoiceCommandsEnabled && isRegularSpeechKeybind)
            {
                if (VoiceCommandService.TryMatchCommand(text, out var command) && command is not null)
                {
                    TranscriptBox.Text = $"[Voice Command: Launch {command.MatchedName}]";
                    SetQueueStatus($"Launching {command.MatchedName}…");
                    VoiceCommandService.ExecuteCommand(command);
                    SetQueueStatus($"Launched {command.MatchedName}");
                    return;
                }
            }

            // The raw hotkey deliberately skips cleanup, so what Parakeet heard
            // is what gets typed.
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
            // next message: activating the target window mid-recording would
            // yank focus around under them. Wait for the release.
            while (_audio.IsRecording)
            {
                await Task.Delay(100, cancellationToken);
            }

            SetQueueStatus("Pasting into the selected field…");
            await _inserter.InsertTextAsync(text, job.Target, cancellationToken);
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

    /// <summary>
    /// Shows processing progress in the status pill, with a queue count when
    /// messages are waiting. While the user is recording, "Listening…" owns the
    /// pill and progress updates stay out of its way.
    /// </summary>
    private void SetQueueStatus(string text)
    {
        if (_audio.IsRecording) return;
        RunStatus.Text = _jobs.Count > 0 ? $"{text} · {_jobs.Count} waiting" : text;
    }

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
        VoiceCommandsCheck.IsChecked = _settings.VoiceCommandsEnabled;
        _tones.Muted = _settings.SoundCuesMuted;
        // The registry is the truth for autostart: the saved setting could be
        // stale if the entry was removed outside ShadowWhispr.
        var autostartActive = StartupService.IsEnabled();
        StartWithWindowsCheck.IsChecked = autostartActive;
        _settings.StartWithWindows = autostartActive;
        // While autostart is on the registry entry says whether it comes up in
        // the tray; with it off there is nothing to read, so the saved
        // preference is what the (disabled) checkbox shows.
        var startsMinimized = autostartActive ? StartupService.StartsMinimized() : _settings.StartMinimized;
        StartMinimizedCheck.IsChecked = startsMinimized;
        _settings.StartMinimized = startsMinimized;
        StartupStatus.Text = DescribeAutostart(autostartActive, startsMinimized);
        AiEnabledCheck.IsChecked = _settings.AiEnabled;
        SelectComboText(ProviderCombo, _settings.Provider);
        InstructionBox.Text = _settings.CustomInstruction;
        AiOptions.IsEnabled = _settings.AiEnabled;
        AutoUpdateCheck.IsChecked = _settings.AutoUpdateEnabled;
        _uiReady = true;
    }

    // --- Microphone selection ---------------------------------------------

    /// <summary>
    /// Fills the microphone dropdown with what Windows currently offers. A saved
    /// microphone that is unplugged right now still gets an entry, so opening the
    /// app without it connected cannot silently erase the choice.
    /// </summary>
    private void RefreshMicrophoneList(string preferredName)
    {
        var wasReady = _uiReady;
        _uiReady = false;
        try
        {
            var devices = AudioRecorderService.ListMicrophones().ToList();
            MicrophoneDevice? preferred = null;
            if (!string.IsNullOrWhiteSpace(preferredName))
            {
                preferred = devices.FirstOrDefault(device =>
                    !device.IsWindowsDefault &&
                    string.Equals(device.Name, preferredName, StringComparison.OrdinalIgnoreCase));
                if (preferred is null)
                {
                    preferred = new MicrophoneDevice(MicrophoneDevice.WindowsDefaultNumber, preferredName);
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

    /// <summary>Re-scans devices on open, so a freshly plugged-in mic shows up.</summary>
    private void MicComboOpened(object sender, EventArgs e) =>
        RefreshMicrophoneList(_settings.Microphone);

    private void MicChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady) return;
        ReadUiIntoSettings();
        _audio.PreferredDeviceName = _settings.Microphone;
        AppLog.Write($"Microphone set to '{(_settings.Microphone.Length == 0 ? "Windows default" : _settings.Microphone)}'");
        QueueAutoSave();
    }

    private async void ProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady) return;

        // Save first, while _activeProvider still names the provider the
        // reasoning combo belongs to, then hand over to the new one.
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

        // Restore the model this provider was last using, for the same reason
        // as its effort: coming back should look like you left it.
        await RefreshModelsAsync(_settings.GetModelFor(_activeProvider));
    }

    private async void LoginClicked(object sender, RoutedEventArgs e)
    {
        var provider = GetComboText(ProviderCombo) ?? AiProviderService.Claude;
        SetAuthButtonsEnabled(false);
        AuthStatus.Text = $"Complete {provider} login in the window that opens…";
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

    private async void LogoutClicked(object sender, RoutedEventArgs e)
    {
        var provider = GetComboText(ProviderCombo) ?? AiProviderService.Claude;
        if (MessageBox.Show(
                $"Log out of {provider}?",
                "ShadowWhispr",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

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
        // A provider that is already signed in keeps its Login button greyed out
        // even when the buttons are re-enabled after a login or logout run.
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

    /// <summary>
    /// Asks the selected provider whether it is already signed in and greys out
    /// its Login button if so. Runs in the background because it starts the
    /// provider's CLI, which takes a moment. A newer check always wins, so
    /// switching provider mid-check cannot leave the wrong answer on screen.
    /// </summary>
    private async Task RefreshLoginStatusAsync(string provider)
    {
        var generation = ++_loginStatusGeneration;

        // Starting the provider's CLI takes a moment. The button stays out of
        // action for that moment, so a fast click cannot start a second login
        // for a provider that turns out to be signed in already.
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
            // A failed check must never leave the button stuck: fall back to
            // "cannot tell", which keeps signing in possible.
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
            // Not signed in, or the CLI could not be asked: leave the button
            // usable so a sign-in is always possible.
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

    private void ModelChanged(object sender, SelectionChangedEventArgs e)
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

        // Restore what this provider was last set to, as long as the selected
        // model still offers it; otherwise fall back to the model's own default.
        var remembered = _settings.GetReasoningFor(_activeProvider);
        var preferred = remembered is not null && model.ReasoningLevels.Contains(remembered)
            ? remembered
            : model.DefaultReasoningLevel;
        ReasoningCombo.SelectedItem = preferred ?? model.ReasoningLevels.FirstOrDefault();
        ReasoningCombo.IsEnabled = model.ReasoningLevels.Count > 0;
        UpdateFastModeChoice(model);
    }

    /// <summary>
    /// Fast mode only exists for Codex models that advertise the tier, so the
    /// box stays hidden everywhere else rather than offering a switch that would
    /// do nothing.
    /// </summary>
    private void UpdateFastModeChoice(AiModelOption? model)
    {
        var supported = model?.SupportsFastMode == true;
        FastModePanel.Visibility = supported ? Visibility.Visible : Visibility.Collapsed;
        FastModeCheck.IsChecked = supported && _settings.CodexFastMode;
    }

    private void AiToggleChanged(object sender, RoutedEventArgs e)
    {
        if (AiOptions is null) return;
        AiOptions.IsEnabled = AiEnabledCheck.IsChecked == true;
        SettingsChanged(sender, e);
    }

    private void SettingsChanged(object sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;
        ReadUiIntoSettings();
        _hotkey.Hotkey = ParseOptionalHotkey(_settings.Hotkey);
        QueueAutoSave();
    }

    private void InstructionResizeDragged(object sender, DragDeltaEventArgs e)
    {
        InstructionBox.Height = Math.Clamp(
            InstructionBox.ActualHeight + e.VerticalChange,
            InstructionBox.MinHeight,
            InstructionBox.MaxHeight);
    }

    private void HotkeyCaptureMouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_capturingHotkeyButton is not null) return;
        if (sender is not Button button) return;

        _capturingHotkeyButton = button;
        _hotkeyBeforeCapture = button.Content?.ToString() ?? _settings.Hotkey;
        _hotkey.Enabled = false;
        button.Content = "Press a key or combination…";
        button.BorderBrush = WorkingGold;
        button.Focus();
        Keyboard.Focus(button);
    }

    private void HotkeyCaptureKeyDown(object sender, KeyEventArgs e)
    {
        if (_capturingHotkeyButton is null) return;
        e.Handled = true;

        var key = GetActualKey(e);
        if (key == Key.Escape)
        {
            FinishHotkeyCapture(null);
            return;
        }

        // Delete unbinds either hotkey; both are optional.
        if (key is Key.Delete or Key.Back)
        {
            FinishHotkeyCapture(null, clear: true);
            return;
        }

        if (IsModifierKey(key)) return;

        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0) return;

        var modifiers = Keyboard.Modifiers;
        var hotkey = HoldHotkey.FromVirtualKey(
            virtualKey,
            modifiers.HasFlag(ModifierKeys.Control),
            modifiers.HasFlag(ModifierKeys.Shift),
            modifiers.HasFlag(ModifierKeys.Alt),
            modifiers.HasFlag(ModifierKeys.Windows));
        FinishHotkeyCapture(hotkey);
    }

    private void HotkeyCaptureKeyUp(object sender, KeyEventArgs e)
    {
        if (_capturingHotkeyButton is null) return;
        e.Handled = true;

        var key = GetActualKey(e);
        if (!IsModifierKey(key)) return;

        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey != 0)
            FinishHotkeyCapture(HoldHotkey.FromVirtualKey(virtualKey));
    }

    private void HotkeyCaptureLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_capturingHotkeyButton is not null) FinishHotkeyCapture(null);
    }

    /// <summary>
    /// Ends hotkey capture for whichever field was being edited. A null hotkey
    /// keeps the previous binding, unless <paramref name="clear"/> is set, which
    /// unassigns that field. Either hotkey may be left unbound.
    /// </summary>
    private void FinishHotkeyCapture(HoldHotkey? hotkey, bool clear = false)
    {
        var button = _capturingHotkeyButton;
        _capturingHotkeyButton = null;
        if (button is null) return;

        var isRaw = ReferenceEquals(button, RawHotkeyCaptureButton);
        var fieldName = isRaw ? "raw" : "main";
        string OwnStoredValue() => isRaw ? _settings.RawHotkey : _settings.Hotkey;
        var text = clear ? string.Empty : hotkey?.ToString() ?? _hotkeyBeforeCapture;

        // Cancelling on an unassigned optional field restores its placeholder,
        // which is a label rather than a hotkey and must not be stored as one.
        if (text == OptionalHotkeyUnsetLabel) text = string.Empty;

        // One keypress must never mean two things, so a binding that duplicates
        // any other field is refused instead of silently shadowing it.
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
        button.BorderBrush = LineGray;

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

    private static Key GetActualKey(KeyEventArgs e) => e.Key switch
    {
        Key.System => e.SystemKey,
        Key.ImeProcessed => e.ImeProcessedKey,
        Key.DeadCharProcessed => e.DeadCharProcessedKey,
        _ => e.Key
    };

    private static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or
        Key.LeftAlt or Key.RightAlt or
        Key.LeftShift or Key.RightShift or
        Key.LWin or Key.RWin;

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
        // The Windows-default entry is stored as empty; a disconnected saved mic
        // keeps its real name in the list, so its name (not "default") persists.
        if (MicCombo.SelectedItem is MicrophoneDevice mic)
        {
            _settings.Microphone =
                string.Equals(mic.Name, MicrophoneDevice.WindowsDefaultName, StringComparison.OrdinalIgnoreCase)
                    ? string.Empty
                    : mic.Name;
        }
        _settings.KeepRunningInTray = KeepRunningInTrayCheck.IsChecked == true;
        _settings.StartWithWindows = StartWithWindowsCheck.IsChecked == true;
        _settings.StartMinimized = StartMinimizedCheck.IsChecked == true;
        _settings.SoundCuesMuted = MuteSoundCuesCheck.IsChecked == true;
        _settings.VoiceCommandsEnabled = VoiceCommandsCheck.IsChecked == true;
        _tones.Muted = _settings.SoundCuesMuted;
        _settings.AiEnabled = AiEnabledCheck.IsChecked == true;
        _settings.Provider = GetComboText(ProviderCombo) ?? AiProviderService.Claude;
        if (ModelCombo.SelectedItem is AiModelOption model)
        {
            _settings.ModelId = model.Id;
            _settings.SetModelFor(_activeProvider, model.Id);
        }
        // Filed under the provider the reasoning list actually belongs to, which
        // during a provider switch is still the previous one. Blank is ignored by
        // SetReasoningFor, so the momentarily empty list during model discovery
        // cannot erase a remembered choice.
        var reasoning = ReasoningCombo.SelectedItem as string ?? string.Empty;
        _settings.Reasoning = reasoning;
        _settings.SetReasoningFor(_activeProvider, reasoning);
        // Only read while the box is actually on screen. It is hidden for every
        // non-Codex model, and reading it then would quietly clear the choice the
        // user made for Codex - the same trap the reasoning memory above avoids.
        if (FastModePanel.Visibility == Visibility.Visible)
        {
            _settings.CodexFastMode = FastModeCheck.IsChecked == true;
        }
        _settings.CustomInstruction = string.IsNullOrWhiteSpace(InstructionBox.Text)
            ? new AppSettings().CustomInstruction
            : InstructionBox.Text.Trim();
        _settings.AutoUpdateEnabled = AutoUpdateCheck.IsChecked == true;
    }

    private void VoiceCommandsToggled(object sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;
        ReadUiIntoSettings();
        QueueAutoSave();
        AppLog.Write($"Voice commands enabled set to {_settings.VoiceCommandsEnabled}");
    }

    // --- Automatic saving -------------------------------------------------

    /// <summary>
    /// Every settings control calls this instead of a Save button. Writes are
    /// debounced so that typing in the instruction box (one event per keystroke)
    /// results in a single write once the user pauses.
    /// </summary>
    private void QueueAutoSave()
    {
        // _startupComplete keeps the initial model discovery — which selects a
        // model and so raises the same change events a user would — from
        // reporting a save the user never made.
        if (!_uiReady || !_startupComplete) return;
        SetSaveStatus("Saving…", WorkingGold);
        _autoSaveTimer ??= CreateTimer(AutoSaveDelay, SaveSettingsNow);
        _autoSaveTimer.Stop();
        _autoSaveTimer.Start();
    }

    /// <summary>
    /// Reads the UI into settings (which applies the same validation the Save
    /// button used to) and writes them to disk, reporting the outcome in the
    /// status pill. Also used to flush a pending debounced save on shutdown.
    /// </summary>
    private void SaveSettingsNow()
    {
        _autoSaveTimer?.Stop();
        // Never write settings read from controls that were never populated.
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

    private void SetSaveStatus(string text, Brush color)
    {
        SaveStatus.Text = text;
        SaveStatus.Foreground = color;
        SaveDot.Fill = color;
    }

    private void SetEngine(string text, Brush color)
    {
        Dispatcher.Invoke(() => { EngineStatus.Text = text; EngineDot.Fill = color; });
    }

    private void SetError(string message)
    {
        AppLog.Write($"ERROR shown to user: {message}");
        Dispatcher.Invoke(() =>
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

    /// <summary>Parses an optional hotkey; blank or invalid means "not set".</summary>
    private static HoldHotkey? ParseOptionalHotkey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == OptionalHotkeyUnsetLabel) return null;
        return HoldHotkey.TryParse(value, out var hotkey) ? hotkey : null;
    }

    /// <summary>
    /// Pauses or resumes the global hotkeys from the tray, so the dictation keys
    /// reach games and other apps untouched while paused. Not persisted: a fresh
    /// start is always armed.
    /// </summary>
    private void SetDictationPaused(bool paused)
    {
        if (_dictationPaused == paused) return;
        _dictationPaused = paused;
        AppLog.Write(paused ? "Dictation paused" : "Dictation resumed");

        // Capture mode owns Enabled while it is listening for a new binding;
        // FinishHotkeyCapture re-applies the paused state afterwards.
        if (_capturingHotkeyButton is null) _hotkey.Enabled = !paused;

        _tray.Paused = paused;
        PauseButton.Content = paused ? "Resume dictation" : "Pause dictation";
        UpdateTrayStatus();
    }

    private void PauseClicked(object sender, RoutedEventArgs e) =>
        SetDictationPaused(!_dictationPaused);

    /// <summary>
    /// Keeps the tray tooltip honest about whether dictation is actually armed,
    /// since that is all the user can see when the window is hidden.
    /// </summary>
    private void UpdateTrayStatus()
    {
        string status;
        if (SetupBanner.Visibility == Visibility.Visible) status = "Setup needed";
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

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // Closing the window is not quitting: by default ShadowWhispr keeps
        // running in the tray so the hold hotkey still works. Only the tray's
        // Quit item (which sets _exitRequested) actually shuts things down.
        //
        // The exception is a downloaded update the user asked to install on
        // close. That install needs this process gone, so honour their choice
        // and exit for real instead of hiding.
        if (_pendingInstallerPath is not null && !_exitRequested)
        {
            AppLog.Write("Closing for real rather than to the tray: an update is waiting to install");
            _exitRequested = true;
        }

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

        AppLog.Write("App closing");
        // Each shutdown step is isolated and logged so one failure can neither
        // hide from the log nor prevent the remaining cleanup from running.
        // Flushes a debounced auto-save that has not fired yet, so a change made
        // in the last moment before closing is still written.
        // Only save if the UI ever finished loading. Reading half-initialised
        // controls back into settings would overwrite the user's real choices
        // with defaults — exactly what a process that exits early would do.
        if (_startupComplete)
            RunLogged("save settings on close", () => { ReadUiIntoSettings(); _settingsService.Save(_settings); });
        else
            AppLog.Write("Skipped saving settings on close: startup never completed");
        RunLogged("cancel pending work", () => { _lifetime?.Cancel(); _modelRefresh?.Cancel(); });
        RunLogged("stop update timers", () => { _updatePollTimer?.Stop(); _updateRepromptTimer?.Stop(); _autoSaveTimer?.Stop(); });
        RunLogged("stop hotkey hook", _hotkey.Dispose);
        RunLogged("stop tone player", _tones.Dispose);
        RunLogged("stop audio recorder", _audio.Dispose);
        RunLogged("stop speech engine", () => _parakeet.DisposeAsync().AsTask().GetAwaiter().GetResult());
        RunLogged("remove tray icon", _tray.Dispose);
        RunLogged("release token sources", () => { _modelRefresh?.Dispose(); _lifetime?.Dispose(); });

        // Last of all, once our own files are no longer in use, kick off a
        // downloaded update. The silent installer performs the in-place upgrade.
        if (_pendingInstallerPath is not null)
        {
            RunLogged("launch pending update installer", () => UpdateService.InstallOnClose(_pendingInstallerPath));
        }

        // ShutdownMode is OnExplicitShutdown so that hiding to the tray does not
        // end the process; that makes this the one place that ends it for real.
        RunLogged("shut down the application", () => Application.Current.Shutdown());
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
}
