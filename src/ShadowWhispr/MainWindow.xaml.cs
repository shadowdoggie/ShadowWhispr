using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ShadowWhispr.Models;
using ShadowWhispr.Services;

namespace ShadowWhispr;

public partial class MainWindow : Window
{
    private static readonly Brush ReadyGreen = new SolidColorBrush(Color.FromRgb(83, 211, 137));
    private static readonly Brush WorkingGold = new SolidColorBrush(Color.FromRgb(231, 184, 92));
    private static readonly Brush ErrorRed = new SolidColorBrush(Color.FromRgb(223, 74, 74));

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
        _lifetime = new CancellationTokenSource();
        _settings = _settingsService.Load();
        ApplySettingsToUi();
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
            SetEngine("Parakeet ready · GPU", ReadyGreen);
            if (!_audio.IsRecording && !_busy) _overlay.SetReady();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            SetEngine("Parakeet needs attention", ErrorRed);
            SetError(ex.Message);
        }
    }

    private async void OnHotkeyPressed(object? sender, EventArgs e)
    {
        if (_busy || _audio.IsRecording) return;
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
        SelectComboText(HotkeyCombo, _settings.Hotkey);
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
        await RefreshModelsAsync(null);
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

    private void ReadUiIntoSettings()
    {
        _settings.Hotkey = GetComboText(HotkeyCombo) ?? "Right Ctrl";
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

    private static HoldHotkey ParseHotkey(string value) => value switch
    {
        "Right Alt" => HoldHotkey.RightAlt,
        "Ctrl + Space" => HoldHotkey.CtrlSpace,
        "Ctrl + Shift + Space" => HoldHotkey.CtrlShiftSpace,
        "Alt + Space" => HoldHotkey.AltSpace,
        "F8" => HoldHotkey.F8,
        "F9" => HoldHotkey.F9,
        _ => HoldHotkey.RightCtrl
    };

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
