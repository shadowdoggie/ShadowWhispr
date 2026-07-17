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

    private readonly SettingsService _settingsService = new();
    private readonly ParakeetService _parakeet = new();
    private readonly AiProviderService _ai = new();
    private readonly GlobalHotkeyService _hotkey = new();
    private readonly AudioRecorderService _audio = new();
    private readonly TonePlayer _tones = new();
    private readonly TextInsertionService _inserter = new();
    private readonly OverlayWindow _overlay = new();

    private AppSettings _settings = new();
    private TextInsertionTarget _insertionTarget;
    private bool _uiReady;
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
            SetError($"Hotkey error: {ex.Message}");
        }

        await RefreshModelsAsync(_settings.ModelId);
        _ = WarmSpeechEngineAsync(_lifetime.Token);
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
            AppLog.Write($"Speech engine start failed: {ex.Message}");
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
        _uiReady = true;
    }

    private async void ProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady) return;
        ReadUiIntoSettings();
        SetAuthHint(_settings.Provider);
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
            AiProviderService.Kimi => "Kimi supports login; its tool has no logout",
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
            ? "Fix punctuation and obvious speech-to-text mistakes while preserving my meaning and tone."
            : InstructionBox.Text.Trim();
    }

    private void SaveClicked(object sender, RoutedEventArgs e)
    {
        ReadUiIntoSettings();
        _settingsService.Save(_settings);
        SaveButton.Content = "Saved";
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
        timer.Tick += (_, _) => { SaveButton.Content = "Save settings"; timer.Stop(); };
        timer.Start();
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
        ReadUiIntoSettings();
        _settingsService.Save(_settings);
        _lifetime?.Cancel();
        _modelRefresh?.Cancel();
        _hotkey.Dispose();
        _tones.Dispose();
        _audio.Dispose();
        _parakeet.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _overlay.Close();
        _modelRefresh?.Dispose();
        _lifetime?.Dispose();
    }
}
