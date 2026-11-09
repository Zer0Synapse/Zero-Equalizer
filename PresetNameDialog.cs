using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace ZeroEqualizer;

internal sealed class PresetNameDialog : Window
{
    private readonly TextBox name = new() { MaxLength = 48, MinHeight = 29, Padding = new Thickness(6, 4, 6, 4) };
    public string PresetName => name.Text.Trim();

    public PresetNameDialog()
    {
        Title = "Save preset";
        Width = 360;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.FromRgb(18, 27, 40));
        Foreground = Brushes.WhiteSmoke;
        var layout = new StackPanel { Margin = new Thickness(18) };
        layout.Children.Add(new TextBlock { Text = "Name this EQ curve", Margin = new Thickness(0, 0, 0, 7) });
        AutomationProperties.SetName(name, "Preset name");
        layout.Children.Add(name);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 13, 0, 0)
        };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 74, MinHeight = 29 };
        var save = new Button { Content = "Save", IsDefault = true, IsEnabled = false, MinWidth = 74, MinHeight = 29, Margin = new Thickness(8, 0, 0, 0) };
        save.Click += (_, _) => DialogResult = true;
        name.TextChanged += (_, _) => save.IsEnabled = !string.IsNullOrWhiteSpace(PresetName);
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        layout.Children.Add(buttons);
        Content = layout;
        Loaded += (_, _) => name.Focus();
    }
}
