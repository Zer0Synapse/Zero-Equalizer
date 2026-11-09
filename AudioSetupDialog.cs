using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace ZeroEqualizer;

internal sealed class AudioSetupDialog : Window
{
    private readonly TextBlock state;
    private readonly Button download;
    private readonly Button chooseFile;
    private readonly Button selector;
    private readonly Button done;
    private bool working;

    public AudioSetupDialog(string? output)
    {
        Title = "Set up Windows audio";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.FromRgb(18, 27, 40));
        Foreground = Brushes.WhiteSmoke;

        var layout = new StackPanel { Margin = new Thickness(22) };
        layout.Children.Add(new TextBlock { Text = "Set up Windows audio", FontSize = 21, FontWeight = FontWeights.SemiBold });
        AddText(layout, "1. Install Equalizer APO using its verified installer. Windows will ask for administrator approval. The installer allows unsigned effects in the Windows audio engine.");
        var installActions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        download = AddButton(installActions, "Install Equalizer APO", async () => await Install(ApoInstaller.Download));
        chooseFile = AddButton(installActions, "Use downloaded installer…", PickInstaller);
        AddButton(installActions, "Project page", () => Open(ApoInstaller.ProjectPage));
        layout.Children.Add(installActions);

        AddText(layout, "2. In Device Selector, enable the post-mix effect on the playback output you use. Restart Windows if Equalizer APO asks you to.");
        if (!string.IsNullOrWhiteSpace(output)) AddText(layout, "Selected output · " + output);
        var deviceActions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        selector = AddButton(deviceActions, "Open Device Selector", OpenSelector);
        layout.Children.Add(deviceActions);
        AddText(layout, "3. Return to Zero Equalizer, select your output, and choose Connect EQ.");

        state = AddText(layout, "");
        state.Foreground = new SolidColorBrush(Color.FromRgb(101, 232, 205));
        var footer = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        done = AddButton(footer, "Done", Close);
        done.IsCancel = true;
        layout.Children.Add(footer);
        Content = layout;
        Refresh();
    }

    private static TextBlock AddText(Panel parent, string value)
    {
        var text = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
        parent.Children.Add(text);
        return text;
    }

    private static Button AddButton(Panel parent, string caption, Action action)
    {
        var button = new Button
        {
            Content = caption, Padding = new Thickness(9, 5, 9, 5), MinHeight = 30,
            Margin = new Thickness(0, 0, 6, 0)
        };
        button.Click += (_, _) => action();
        parent.Children.Add(button);
        return button;
    }

    private static Button AddButton(Panel parent, string caption, Func<Task> action)
    {
        var button = AddButton(parent, caption, () => { });
        button.Click += async (_, _) => await action();
        return button;
    }

    private void Refresh(string? message = null)
    {
        var installed = ApoConfig.FindConfigPath() != null;
        download.IsEnabled = chooseFile.IsEnabled = !installed && !working;
        selector.IsEnabled = !working && AudioDevices.FindConfigurator() != null;
        done.IsEnabled = !working;
        state.Text = message ?? (installed ? "Equalizer APO is installed." : "Equalizer APO is ready to download.");
    }

    private async Task PickInstaller()
    {
        var picker = new OpenFileDialog { Filter = "Equalizer APO installer (*.exe)|*.exe", CheckFileExists = true };
        if (picker.ShowDialog(this) == true)
            await Install(token => ApoInstaller.UseDownloadedFile(picker.FileName, token));
    }

    private async Task Install(Func<CancellationToken, Task<string>> prepare)
    {
        working = true;
        Refresh("Verifying the Equalizer APO installer…");
        string message;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(70));
            var verified = await prepare(timeout.Token);
            state.Text = "Finish the Equalizer APO setup in the installer window.";
            var result = await ApoInstaller.Run(verified);
            message = ApoConfig.FindConfigPath() != null
                ? "Equalizer APO is installed. Check the selected playback output in Device Selector."
                : $"Installation did not finish (code {result}). You can try again.";
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or
            UnauthorizedAccessException or OperationCanceledException or InvalidOperationException or NotSupportedException or Win32Exception)
        {
            message = error is Win32Exception { NativeErrorCode: 1223 } ? "Installation canceled." : error.Message;
        }
        working = false;
        Refresh(message);
    }

    private void OpenSelector()
    {
        if (AudioDevices.FindConfigurator() is { } path) Open(path);
    }

    private void Open(string target)
    {
        try
        {
            var request = new ProcessStartInfo(target) { UseShellExecute = true };
            if (Path.IsPathFullyQualified(target)) request.WorkingDirectory = Path.GetDirectoryName(target)!;
            Process.Start(request)?.Dispose();
        }
        catch (Win32Exception error)
        {
            state.Text = error.NativeErrorCode == 1223 ? "Setup canceled." : error.Message;
        }
    }

    protected override void OnClosing(CancelEventArgs args)
    {
        if (working) args.Cancel = true;
        base.OnClosing(args);
    }
}
