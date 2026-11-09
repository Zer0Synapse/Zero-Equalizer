using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace ZeroEqualizer;

public partial class MainWindow : Window
{
    private readonly string settingsPath;
    private readonly DispatcherTimer updateTimer;
    private readonly List<(Slider Slider, TextBlock Readout)> sliders = [];
    private readonly UserSettings settings;
    private EqProfile profile = EqProfile.Flat;
    private ApoConfig? apo;
    private bool bypass;
    private bool loading = true;
    private bool needsElevation;
    private bool applyPending;

    public MainWindow()
    {
        settingsPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZeroEqualizer", "settings.json");
        settings = LoadSettings(settingsPath);
        updateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        updateTimer.Tick += (_, _) => CommitPending();
        InitializeComponent();

        AddSlider(ToneControls, "Bass", "Bass shelf");
        AddSlider(ToneControls, "Treble", "Treble shelf");
        foreach (var frequency in EqProfile.Frequencies)
        {
            var label = frequency < 1000 ? frequency.ToString(CultureInfo.InvariantCulture) : $"{frequency / 1000}k";
            AddSlider(BandControls, label, $"{frequency} hertz");
        }
        Loaded += (_, _) => RefreshDevices(settings.DeviceId);
    }

    private AudioDevice? SelectedDevice => DevicePicker.SelectedItem as AudioDevice;
    private bool IsEditable => !loading && IsLoaded;

    private void AddSlider(Panel panel, string label, string accessibleName)
    {
        var stack = new StackPanel();
        var readout = new TextBlock
        {
            Text = "0.0", FontSize = 11, FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("Accent"), HorizontalAlignment = HorizontalAlignment.Center
        };
        var slider = new Slider { Style = (Style)FindResource("BandSlider"), ToolTip = $"{accessibleName} · double-click to reset" };
        AutomationProperties.SetName(slider, accessibleName);
        slider.ValueChanged += (_, _) => SliderChanged();
        slider.PreviewMouseLeftButtonDown += (_, args) =>
        {
            if (args.ClickCount != 2) return;
            slider.Value = 0;
            args.Handled = true;
        };
        sliders.Add((slider, readout));
        stack.Children.Add(readout);
        stack.Children.Add(slider);
        stack.Children.Add(new TextBlock { Text = label, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center });
        panel.Children.Add(stack);
    }

    private void RefreshDevices(Guid? selectedId)
    {
        CommitPending();
        var config = ApoConfig.FindConfigPath();
        apo = config == null ? null : new ApoConfig(config);
        try
        {
            var devices = AudioDevices.List();
            loading = true;
            DevicePicker.ItemsSource = devices;
            DevicePicker.SelectedItem = devices.FirstOrDefault(device => device.Id == selectedId)
                ?? devices.FirstOrDefault(device => device.Id == apo?.ConnectedDevice)
                ?? devices.FirstOrDefault(device => device.IsDefault)
                ?? devices.FirstOrDefault();
            loading = false;
            LoadDevice();
            UpgradeLegacyGain();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            loading = false;
            ShowStatus($"Couldn’t read Windows audio setup. {error.Message}", error: true);
        }
    }

    private void LoadDevice()
    {
        settings.DeviceId = SelectedDevice?.Id;
        var state = settings.Outputs.GetValueOrDefault(SelectedDevice?.Id ?? Guid.Empty) ?? new OutputSettings();
        bypass = state.Bypass;
        var name = state.Preset;
        SetProfile((state.Profile ?? EqProfile.Flat).Normalize(), name);
        UpdateConnection();
    }

    private void SetProfile(EqProfile next, string? preset)
    {
        loading = true;
        profile = next.Normalize();
        PreampSlider.Value = profile.PreampDb;
        sliders[0].Slider.Value = profile.Bass;
        sliders[1].Slider.Value = profile.Treble;
        for (var index = 0; index < profile.Bands.Length; index++) sliders[index + 2].Slider.Value = profile.Bands[index];
        PresetPicker.ItemsSource = EqProfile.Presets.Keys.Concat(settings.Presets.Keys.Order(StringComparer.CurrentCultureIgnoreCase)).ToArray();
        PresetPicker.SelectedItem = preset;
        loading = false;
        UpdateReadouts();
    }

    private void SliderChanged()
    {
        if (!IsEditable) return;
        profile = new EqProfile(sliders[0].Slider.Value, sliders[1].Slider.Value,
            sliders.Skip(2).Select(band => band.Slider.Value).ToArray()) { PreampDb = profile.PreampDb };
        loading = true;
        PresetPicker.SelectedIndex = -1;
        loading = false;
        UpdateReadouts();
        ScheduleApply();
    }

    private void PreampChanged(object sender, RoutedPropertyChangedEventArgs<double> args)
    {
        if (!IsEditable) return;
        profile = profile with { PreampDb = args.NewValue };
        UpdateReadouts();
        ScheduleApply();
    }

    private void ResetPreampClicked(object sender, RoutedEventArgs args) => PreampSlider.Value = 0;

    private void UpdateReadouts()
    {
        foreach (var (slider, readout) in sliders) readout.Text = FormatDb(slider.Value);
        BypassButton.Content = bypass ? "Bypass on" : "Bypass off";
        BypassButton.Style = (Style)FindResource(bypass ? "Action" : typeof(Button));
        PreampValue.Text = $"{FormatDb(profile.PreampDb)} dB";
        DrawGraph();
    }

    private static string FormatDb(double value) => value.ToString("+0.0;−0.0;0.0", CultureInfo.CurrentCulture);

    private void DrawGraph()
    {
        if (Graph == null || Graph.ActualWidth < 1 || Graph.ActualHeight < 1) return;
        Graph.Children.Clear();
        var width = Graph.ActualWidth;
        var height = Graph.ActualHeight;
        const int resolution = 180;
        var values = Enumerable.Range(0, resolution + 1)
            .Select(index => bypass ? 0 : profile.ResponseDb(20 * Math.Pow(1000, index / (double)resolution))).ToArray();
        var peak = values.Max();
        ResponseStatus.Text = bypass ? "Bypassed" : peak > 0.1 ? $"Peak boost {FormatDb(peak)} dB" : "48 kHz reference";
        ResponseStatus.ToolTip = !bypass && peak > 0.1
            ? "Boosts can clip full-scale audio. Use the manual preamp if you hear distortion."
            : null;
        ResponseStatus.Foreground = (Brush)FindResource(!bypass && peak > 0.1 ? "Caution" : "Subtle");
        var range = Math.Max(12, Math.Ceiling(values.Max(value => Math.Abs(value)) / 6) * 6);
        double Y(double value) => height * (0.5 - value / (2 * range));
        GraphTop.Text = $"+{range:0}";
        GraphBottom.Text = $"−{range:0}";
        var muted = (Brush)FindResource("Edge");
        var accent = (Brush)FindResource("Accent");

        foreach (var frequency in EqProfile.Frequencies)
        {
            var x = width * Math.Log10(frequency / 20.0) / 3;
            Graph.Children.Add(new Line { X1 = x, X2 = x, Y2 = height, Stroke = muted, StrokeThickness = 1 });
        }
        foreach (var value in new[] { -range / 2, 0, range / 2 })
        {
            var y = Y(value);
            Graph.Children.Add(new Line { X2 = width, Y1 = y, Y2 = y, Stroke = muted, StrokeThickness = value == 0 ? 1.5 : 1 });
        }

        var line = new Polyline { Stroke = accent, StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
        var area = new Polygon { Fill = new SolidColorBrush(Color.FromArgb(32, 101, 232, 205)) };
        area.Points.Add(new Point(0, Y(0)));
        for (var index = 0; index <= resolution; index++)
        {
            var point = new Point(width * index / resolution, Y(values[index]));
            line.Points.Add(point);
            area.Points.Add(point);
        }
        area.Points.Add(new Point(width, Y(0)));
        Graph.Children.Add(area);
        Graph.Children.Add(line);
    }

    private void UpgradeLegacyGain()
    {
        if (apo?.IsConnected != true) return;
        try
        {
            apo.UpgradeLegacyGain();
        }
        catch (UnauthorizedAccessException)
        {
            needsElevation = true;
            UpdateConnection("Restart as admin to remove the old automatic gain from Equalizer APO.");
        }
        catch (Exception error) when (error is IOException or InvalidDataException)
        {
            UpdateConnection($"Couldn’t update the saved gain. {error.Message}");
        }
    }

    private void ScheduleApply()
    {
        applyPending = true;
        updateTimer.Stop();
        updateTimer.Start();
    }

    private void CommitPending()
    {
        updateTimer.Stop();
        if (!applyPending) return;
        applyPending = false;
        SaveState();
        if (SelectedDevice is { ApoPostMix: true, EnhancementsDisabled: false } device && apo?.ConnectedDevice == device.Id) Apply(device);
    }

    private void Apply(AudioDevice device)
    {
        try
        {
            apo!.Apply(device.Id, profile.ToApoFilters(bypass));
            needsElevation = false;
            UpdateConnection();
        }
        catch (UnauthorizedAccessException)
        {
            needsElevation = true;
            UpdateConnection("Windows denied access to the Equalizer APO configuration. Restart as admin to apply changes.");
        }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException)
        {
            UpdateConnection($"Couldn’t apply EQ. {error.Message}");
        }
    }

    private void UpdateConnection(string? error = null)
    {
        var device = SelectedDevice;
        var connected = apo?.IsConnected == true;
        var sameDevice = device != null && apo?.ConnectedDevice == device.Id;
        var ready = apo != null && device is { ApoRegistered: true, ApoPostMix: true, EnhancementsDisabled: false };
        ConnectionButton.Content = sameDevice ? "Disconnect EQ" : connected ? "Move EQ here" : "Connect EQ";
        ConnectionButton.IsEnabled = sameDevice || ready;
        SetupButton.Content = needsElevation ? "Restart as admin" : "Audio setup";
        var active = sameDevice && ready && error == null;
        ModeBadge.Text = active ? bypass ? "BYPASS ON" : "EQ ACTIVE" : "PREVIEW";
        ModeBadge.Foreground = (Brush)FindResource(active && !bypass ? "Accent" : "Subtle");

        if (error != null) ShowStatus(error, true);
        else if (device == null) ShowStatus("No playback output detected. Connect headphones or speakers and refresh.");
        else if (apo == null) ShowStatus("Preview · Open Audio setup to install Equalizer APO and connect this output.");
        else if (device.EnhancementsDisabled) ShowStatus("Audio enhancements are off for this output. Enable device effects in Windows Sound settings, then refresh.");
        else if (!device.ApoRegistered || !device.ApoPostMix) ShowStatus("Enable Equalizer APO’s post-mix effects for this output in Setup, then refresh.");
        else if (sameDevice) ShowStatus(bypass ? "Connected · Bypass is on for this output." : "Connected · EQ applies to shared audio on this output.");
        else if (connected) ShowStatus("EQ is connected to another output. Select Move EQ here to switch.");
        else ShowStatus("Ready · Select Connect EQ to apply these settings to this output.");
    }

    private void ShowStatus(string text, bool error = false)
    {
        ConnectionStatus.Text = text;
        ConnectionStatus.Foreground = error ? new SolidColorBrush(Color.FromRgb(255, 176, 153)) : (Brush)FindResource("Subtle");
    }

    private void ConnectClicked(object sender, RoutedEventArgs args)
    {
        CommitPending();
        if (SelectedDevice is not { } device || apo == null) return;
        if (apo.ConnectedDevice != device.Id)
        {
            if (!device.ApoRegistered || !device.ApoPostMix || device.EnhancementsDisabled) return;
            SaveState();
            Apply(device);
            return;
        }
        try
        {
            apo.Disconnect();
            needsElevation = false;
            UpdateConnection();
        }
        catch (UnauthorizedAccessException)
        {
            needsElevation = true;
            UpdateConnection("Windows denied access to the Equalizer APO configuration. Restart as admin to disconnect.");
        }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException)
        {
            UpdateConnection($"Couldn’t disconnect EQ. {error.Message}");
        }
    }

    private void SetupClicked(object sender, RoutedEventArgs args)
    {
        try
        {
            if (needsElevation && Environment.ProcessPath is { } executable)
            {
                CommitPending();
                var launch = new ProcessStartInfo(executable) { UseShellExecute = true, Verb = "runas" };
                if (System.IO.Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                    launch.ArgumentList.Add(typeof(MainWindow).Assembly.Location);
                Process.Start(launch)?.Dispose();
                Close();
            }
            else
            {
                var output = SelectedDevice;
                new AudioSetupDialog(output?.Name) { Owner = this }.ShowDialog();
                RefreshDevices(output?.Id);
            }
        }
        catch (Win32Exception error)
        {
            ShowStatus(error.NativeErrorCode == 1223 ? "Setup was canceled." : error.Message, true);
        }
    }

    private void RefreshClicked(object sender, RoutedEventArgs args) => RefreshDevices(SelectedDevice?.Id);

    private void DeviceChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!IsEditable) return;
        // The selection has already changed. Flush the old output's pending edits first.
        if (args.RemovedItems.OfType<AudioDevice>().FirstOrDefault() is { } previous && applyPending)
        {
            updateTimer.Stop();
            applyPending = false;
            SaveState(previous.Id);
            if (previous.ApoPostMix && !previous.EnhancementsDisabled && apo?.ConnectedDevice == previous.Id) Apply(previous);
        }
        LoadDevice();
        SaveState();
    }

    private void PresetChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!IsEditable || PresetPicker.SelectedItem is not string name) return;
        if (!EqProfile.Presets.TryGetValue(name, out var selected) && !settings.Presets.TryGetValue(name, out selected)) return;
        SetProfile(selected with { PreampDb = profile.PreampDb }, name);
        ScheduleApply();
    }

    private void SavePresetClicked(object sender, RoutedEventArgs args)
    {
        var dialog = new PresetNameDialog { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var name = dialog.PresetName;
        if (EqProfile.Presets.Keys.Any(existing => existing.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            ShowStatus("Choose a name that is different from the built-in presets.", true);
            return;
        }
        var existingName = settings.Presets.Keys.FirstOrDefault(existing => existing.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existingName != null && MessageBox.Show(this, $"Replace the preset “{existingName}”?", "Save preset", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        name = existingName ?? name;
        settings.Presets[name] = (profile with { PreampDb = 0 }).Normalize();
        SetProfile(profile, name);
        SaveState();
    }

    private void BypassClicked(object sender, RoutedEventArgs args)
    {
        bypass = !bypass;
        UpdateReadouts();
        UpdateConnection();
        applyPending = true;
        CommitPending();
    }

    private void ResetClicked(object sender, RoutedEventArgs args)
    {
        SetProfile(EqProfile.Flat with { PreampDb = profile.PreampDb }, "Flat");
        ScheduleApply();
    }

    private void GraphResized(object sender, SizeChangedEventArgs args) => DrawGraph();

    protected override void OnClosing(CancelEventArgs args)
    {
        CommitPending();
        base.OnClosing(args);
    }

    private void SaveState(Guid? deviceId = null)
    {
        var id = deviceId ?? SelectedDevice?.Id;
        settings.DeviceId = id;
        settings.Outputs[id ?? Guid.Empty] = new OutputSettings
        {
            Profile = profile,
            Bypass = bypass,
            Preset = PresetPicker.SelectedItem as string
        };
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(settingsPath)!);
            var temporary = settingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(settings));
                File.Move(temporary, settingsPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ShowStatus($"Couldn’t save your preferences. {error.Message}", true);
        }
    }

    private static UserSettings LoadSettings(string path)
    {
        try
        {
            var state = File.Exists(path) ? JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(path)) ?? new() : new UserSettings();
            state.Outputs ??= new();
            state.Presets ??= new();
            return state;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    private sealed class UserSettings
    {
        public Guid? DeviceId { get; set; }
        public Dictionary<Guid, OutputSettings> Outputs { get; set; } = new();
        public Dictionary<string, EqProfile> Presets { get; set; } = new();
    }

    private sealed class OutputSettings
    {
        public EqProfile? Profile { get; set; }
        public bool Bypass { get; set; }
        public string? Preset { get; set; } = "Flat";
    }
}
