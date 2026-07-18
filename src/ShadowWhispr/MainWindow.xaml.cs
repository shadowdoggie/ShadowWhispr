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

    private static readonly TimeSpan AutoSaveDelay = TimeSpan.FromMilliseconds(600);

    private readonly SettingsService _settingsService = new();
    private readonly ParakeetService _parakeet = new();
    private readonly AiProviderService _ai = new();
    private readonly GlobalHotkeyService _hotkey = new();
    private readonly AudioRecorderService _audio = new();
    private readonly TonePlayer _tones = new();
    private readonly TextInsertionService _inserter = new();
    private readonly OverlayWindow _overlay = new();
    private readonly UpdateService _updates = new();
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
    private bool _capturingHotkey;
    private string _hotkeyBeforeCapture = "Right Ctrl";
    private string? _setupScriptPath;
    private bool _setupAttempted;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closing += OnClosing;
        _hotkey.Pressed += OnHotkeyPressed;
        _hotkey.Released += OnHotkeyReleased;
        _audio.RecordingFailed += (_, ex) => AppLog.Write("Audio recording failed", ex);
        _tones.PlaybackFailed += (_, ex) => AppLog.Write("Cue tone playback failed", ex);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        AppLog.Write($"App started (version {typeof(MainWindow).Assembly.GetName().Version})");
        _lifetime = new CancellationTokenSource();
        _settings = _settingsService.Load();
        ApplySettingsToUi();
        SetAuthHint(_settings.Provider);
        _overlay.Owner = null;
        _overlay.Show();

        try
        {
            _hotkey.Hotkey = ParseHotkey(_settings.Hotkey);
            _hotkey.Start();
        }
        catch (Exception ex)
        {
            AppLog.Write("Starting the global hotkey hook failed", ex);
            SetError($"Hotkey error: {ex.Message}");
        }

        await RefreshModelsAsync(_settings.ModelId);
        _startupComplete = true;
        _ = WarmSpeechEngineAsync(_lifetime.Token);

        if (_settings.AutoUpdateEnabled)
        {
            StartUpdatePolling();
            _ = RunUpdateCheckAsync(manual: false, _lifetime.Token);
        }
    }

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
                SetUpdateStatus($"Update {update.Tag} will install when you close ShadowWhispr.");
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
            _overlay.SetWorking("Loading speech");
            await _parakeet.StartAsync(cancellationToken);
            AppLog.Write($"Speech engine ready on {_parakeet.Device}");
            SetupBanner.Visibility = Visibility.Collapsed;
            SetEngine("Parakeet ready · GPU", ReadyGreen);
            if (!_audio.IsRecording && !_busy) _overlay.SetReady();
        }
        catch (OperationCanceledException) { }
        catch (SpeechSetupRequiredException ex)
        {
            AppLog.Write($"Speech setup required (attempted before: {_setupAttempted})");
            _setupScriptPath = ex.SetupScriptPath;
            SetEngine("Speech setup needed", WorkingGold);
            _overlay.SetReady();
            SetupRunButton.IsEnabled = true;
            SetupStatus.Text = _setupAttempted
                ? "Setup didn't finish. The error stays visible in the PowerShell window and is saved to setup-log.txt in the app folder. Click to try again — it resumes where it left off."
                : string.Empty;
            SetupBanner.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            AppLog.Write("Speech engine start failed", ex);
            SetEngine("Parakeet needs attention", ErrorRed);
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
        if (string.IsNullOrEmpty(_setupScriptPath) || !File.Exists(_setupScriptPath))
        {
            AppLog.Write($"Speech setup script not found at: {_setupScriptPath ?? "(no path)"}");
            SetupStatus.Text = "Setup script not found — please reinstall ShadowWhispr.";
            return;
        }

        _setupAttempted = true;
        AppLog.Write($"Launching speech setup script: {_setupScriptPath}");
        SetupRunButton.IsEnabled = false;
        SetupStatus.Text = "Setting up… follow the PowerShell window (this can take several minutes).";

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = true,
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{_setupScriptPath}\"",
                WorkingDirectory = Path.GetDirectoryName(_setupScriptPath) ?? AppContext.BaseDirectory
            };
            using var process = Process.Start(startInfo);
            if (process is not null)
                await process.WaitForExitAsync(_lifetime?.Token ?? default);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            AppLog.Write("Could not launch the speech setup script", ex);
            SetupStatus.Text = $"Could not start setup: {ex.Message}";
            SetupRunButton.IsEnabled = true;
            return;
        }

        AppLog.Write("Speech setup script window closed; checking the engine");
        SetupStatus.Text = "Almost done — starting the speech engine (this can take a minute)…";
        await WarmSpeechEngineAsync(_lifetime?.Token ?? default);
    }

    private async void OnHotkeyPressed(object? sender, EventArgs e)
    {
        if (_busy || _audio.IsRecording) return;
        if (SetupBanner.Visibility == Visibility.Visible) return;
        try
        {
            _insertionTarget = _inserter.CaptureTarget();
            _tones.PlayPressed();
            await _audio.StartAsync(_lifetime?.Token ?? default);
            RunStatus.Text = "Listening…";
            _overlay.SetRecording();
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
        }
    }

    private async void OnHotkeyReleased(object? sender, EventArgs e)
    {
        if (!_audio.IsRecording || _busy) return;
        _busy = true;
        string? recording = null;
        try
        {
            _overlay.SetWorking("Transcribing");
            RunStatus.Text = "Transcribing locally…";
            recording = await _audio.StopAsync(_lifetime?.Token ?? default);
            _tones.PlayReleased();
            if (string.IsNullOrWhiteSpace(recording)) return;

            var text = await _parakeet.TranscribeAsync(recording, _lifetime?.Token ?? default);
            _audio.DeleteRecording(recording);
            recording = null;
            if (string.IsNullOrWhiteSpace(text))
            {
                RunStatus.Text = "No speech detected";
                return;
            }

            if (_settings.AiEnabled)
            {
                _overlay.SetWorking("AI cleanup");
                RunStatus.Text = $"Cleaning with {_settings.Provider}…";
                text = await _ai.ProcessAsync(
                    _settings.Provider,
                    _settings.ModelId,
                    _settings.Reasoning,
                    _settings.CustomInstruction,
                    text,
                    _lifetime?.Token ?? default);
            }

            TranscriptBox.Text = text;
            RunStatus.Text = "Pasting into the selected field…";
            await _inserter.InsertTextAsync(text, _insertionTarget, _lifetime?.Token ?? default);
            RunStatus.Text = "Pasted";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            SetError(ex.Message);
        }
        finally
        {
            if (recording is not null) _audio.DeleteRecording(recording);
            _busy = false;
            if (_parakeet.IsReady) _overlay.SetReady();
        }
    }

    private void ApplySettingsToUi()
    {
        _uiReady = false;
        HotkeyCaptureButton.Content = _settings.Hotkey;
        AiEnabledCheck.IsChecked = _settings.AiEnabled;
        SelectComboText(ProviderCombo, _settings.Provider);
        InstructionBox.Text = _settings.CustomInstruction;
        AiOptions.IsEnabled = _settings.AiEnabled;
        AutoUpdateCheck.IsChecked = _settings.AutoUpdateEnabled;
        _uiReady = true;
    }

    private async void ProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady) return;
        ReadUiIntoSettings();
        SetAuthHint(_settings.Provider);
        QueueAutoSave();
        await RefreshModelsAsync(null);
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
        LoginButton.IsEnabled = enabled;
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
            return;
        }

        ReasoningCombo.ItemsSource = model.ReasoningLevels;
        var preferred = model.ReasoningLevels.Contains(_settings.Reasoning)
            ? _settings.Reasoning
            : model.DefaultReasoningLevel;
        ReasoningCombo.SelectedItem = preferred ?? model.ReasoningLevels.FirstOrDefault();
        ReasoningCombo.IsEnabled = model.ReasoningLevels.Count > 0;
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
        _hotkey.Hotkey = ParseHotkey(_settings.Hotkey);
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
        if (_capturingHotkey) return;

        _capturingHotkey = true;
        _hotkeyBeforeCapture = HotkeyCaptureButton.Content?.ToString() ?? _settings.Hotkey;
        _hotkey.Enabled = false;
        HotkeyCaptureButton.Content = "Press a key or combination…";
        HotkeyCaptureButton.BorderBrush = WorkingGold;
        HotkeyCaptureButton.Focus();
        Keyboard.Focus(HotkeyCaptureButton);
    }

    private void HotkeyCaptureKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturingHotkey) return;
        e.Handled = true;

        var key = GetActualKey(e);
        if (key == Key.Escape)
        {
            FinishHotkeyCapture(null);
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
        if (!_capturingHotkey) return;
        e.Handled = true;

        var key = GetActualKey(e);
        if (!IsModifierKey(key)) return;

        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey != 0)
            FinishHotkeyCapture(HoldHotkey.FromVirtualKey(virtualKey));
    }

    private void HotkeyCaptureLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_capturingHotkey) FinishHotkeyCapture(null);
    }

    private void FinishHotkeyCapture(HoldHotkey? hotkey)
    {
        var text = hotkey?.ToString() ?? _hotkeyBeforeCapture;
        _capturingHotkey = false;
        HotkeyCaptureButton.Content = text;
        HotkeyCaptureButton.BorderBrush = LineGray;

        _settings.Hotkey = text;
        _hotkey.Hotkey = ParseHotkey(text);
        _hotkey.Enabled = true;
        QueueAutoSave();
    }

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
        if (!_capturingHotkey)
            _settings.Hotkey = HotkeyCaptureButton.Content?.ToString() ?? "Right Ctrl";
        _settings.AiEnabled = AiEnabledCheck.IsChecked == true;
        _settings.Provider = GetComboText(ProviderCombo) ?? AiProviderService.Claude;
        if (ModelCombo.SelectedItem is AiModelOption model) _settings.ModelId = model.Id;
        _settings.Reasoning = ReasoningCombo.SelectedItem as string ?? string.Empty;
        _settings.CustomInstruction = string.IsNullOrWhiteSpace(InstructionBox.Text)
            ? new AppSettings().CustomInstruction
            : InstructionBox.Text.Trim();
        _settings.AutoUpdateEnabled = AutoUpdateCheck.IsChecked == true;
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
            _overlay.SetError();
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

    private static HoldHotkey ParseHotkey(string value) =>
        HoldHotkey.TryParse(value, out var hotkey) ? hotkey : HoldHotkey.Default;

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        AppLog.Write("App closing");
        // Each shutdown step is isolated and logged so one failure can neither
        // hide from the log nor prevent the remaining cleanup from running.
        // Flushes a debounced auto-save that has not fired yet, so a change made
        // in the last moment before closing is still written.
        RunLogged("save settings on close", () => { ReadUiIntoSettings(); _settingsService.Save(_settings); });
        RunLogged("cancel pending work", () => { _lifetime?.Cancel(); _modelRefresh?.Cancel(); });
        RunLogged("stop update timers", () => { _updatePollTimer?.Stop(); _updateRepromptTimer?.Stop(); _autoSaveTimer?.Stop(); });
        RunLogged("stop hotkey hook", _hotkey.Dispose);
        RunLogged("stop tone player", _tones.Dispose);
        RunLogged("stop audio recorder", _audio.Dispose);
        RunLogged("stop speech engine", () => _parakeet.DisposeAsync().AsTask().GetAwaiter().GetResult());
        RunLogged("close overlay", _overlay.Close);
        RunLogged("release token sources", () => { _modelRefresh?.Dispose(); _lifetime?.Dispose(); });

        // Last of all, once our own files are no longer in use, kick off a
        // downloaded update. The silent installer performs the in-place upgrade.
        if (_pendingInstallerPath is not null)
        {
            RunLogged("launch pending update installer", () => UpdateService.InstallOnClose(_pendingInstallerPath));
        }
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
