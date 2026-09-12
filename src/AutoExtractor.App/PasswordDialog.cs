using System.Windows;
using System.Windows.Controls;
using AutoExtractor.Core;

namespace AutoExtractor.App;

public sealed class PasswordDialog : Window
{
    private readonly PasswordBox passwordInput = new() { Padding = new Thickness(10), Margin = new Thickness(0, 15, 0, 8) };
    public string Password => passwordInput.Password;
    public PasswordDialog(PasswordRequest request)
    {
        Title = "输入解压密码";
        Width = 440;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(26) };
        panel.Children.Add(new TextBlock { Text = "这个压缩包需要密码", FontSize = 21, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = $"第 {request.Layer} 层 · {request.ArchiveName}", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) });
        if (request.PreviousFailed)
            panel.Children.Add(new TextBlock { Text = request.Message, TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.DarkGoldenrod, Margin = new Thickness(0, 12, 0, 0) });
        panel.Children.Add(passwordInput);
        panel.Children.Add(new TextBlock { Text = "仅用于本次任务，不保存密码。", FontSize = 11, Foreground = System.Windows.Media.Brushes.SlateGray });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        var cancel = new Button { Content = "跳过此任务", IsCancel = true };
        var submit = new Button { Content = "继续解压", IsDefault = true, Style = (Style)FindResource("PrimaryButton") };
        submit.Click += (_, _) => { if (passwordInput.Password.Length == 0) { passwordInput.Focus(); return; } DialogResult = true; };
        buttons.Children.Add(cancel);
        buttons.Children.Add(submit);
        panel.Children.Add(buttons);
        Content = panel;
        Loaded += (_, _) => passwordInput.Focus();
    }
}
