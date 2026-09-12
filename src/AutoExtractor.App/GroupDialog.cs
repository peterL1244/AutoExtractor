using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using AutoExtractor.Core;
using Microsoft.Win32;

namespace AutoExtractor.App;

public sealed class GroupDialog : Window
{
    private readonly ObservableCollection<string> files = [];
    private readonly ListBox list = new() { MinHeight = 170 };
    private readonly ComboBox format = new() { ItemsSource = new[] { "ZIP", "7z", "RAR" }, SelectedIndex = 0 };
    private readonly ComboBox kind = new() { ItemsSource = new[] { "单个压缩包", "按字节切分（001 / 002）", "RAR 分卷（part01 / part02）", "RAR 旧分卷（rar / r00）", "ZIP 分卷（z01 / z02 / zip）" }, SelectedIndex = 1 };
    private readonly TextBox stem = new() { Text = "archive", Margin = new Thickness(0, 6, 0, 12) };
    public ArchiveCandidate? Candidate
    {
        get; private set;
    }
    public GroupDialog(ArchiveCandidate? candidate)
    {
        Title = "确认分卷与顺序";
        Width = 660;
        Height = 680;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(24) };
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var cancel = new Button { Content = "取消", IsCancel = true };
        var confirm = new Button { Content = "确认并加入任务", Style = (Style)FindResource("PrimaryButton") };
        confirm.Click += Confirm;
        bottom.Children.Add(cancel);
        bottom.Children.Add(confirm);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        var panel = new StackPanel();
        var scroll = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        root.Children.Add(scroll);
        panel.Children.Add(new TextBlock { Text = "将同一压缩包的分卷按顺序排列", FontSize = 19, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "首卷在上，末卷在下。ZIP 原生分卷的 .zip 主文件放最后。\n软件会先验证组合，验证成功后才原地还原名称。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 16), Foreground = System.Windows.Media.Brushes.SlateGray });
        list.ItemsSource = files;
        panel.Children.Add(list);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 18) };
        foreach (var (label, action) in new (string, Action)[] { ("添加文件", Add), ("移除", Remove), ("上移", () => Move(-1)), ("下移", () => Move(1)) })
        {
            var button = new Button { Content = label, Padding = new Thickness(13, 7, 13, 7) };
            button.Click += (_, _) => action();
            actions.Children.Add(button);
        }
        panel.Children.Add(actions);
        panel.Children.Add(new TextBlock { Text = "真实压缩格式" });
        panel.Children.Add(format);
        panel.Children.Add(new TextBlock { Text = "分卷类型" });
        panel.Children.Add(kind);
        panel.Children.Add(new TextBlock { Text = "还原后的基础名称（不含后缀）" });
        panel.Children.Add(stem);
        if (candidate is not null)
        {
            foreach (var member in candidate.Members.OrderBy(m => m.Order))
                files.Add(member.SourcePath);
            format.SelectedIndex = candidate.Format switch
            {
                ArchiveFormat.SevenZip => 1,
                ArchiveFormat.Rar => 2,
                _ => 0
            };
            kind.SelectedIndex = (int)candidate.VolumeKind;
            stem.Text = Path.GetFileNameWithoutExtension(candidate.DisplayName);
        }
        Content = root;
    }
    private void Add()
    {
        var dialog = new OpenFileDialog { Multiselect = true, Filter = "所有文件|*.*" };
        if (dialog.ShowDialog(this) == true)
        foreach (var path in dialog.FileNames)
        if (!files.Contains(path, StringComparer.OrdinalIgnoreCase))
            files.Add(path);
    }
    private void Remove()
    {
        if (list.SelectedIndex >= 0)
            files.RemoveAt(list.SelectedIndex);
    }
    private void Move(int offset)
    {
        int index = list.SelectedIndex;
        if (index < 0 || index + offset < 0 || index + offset >= files.Count)
            return;
        files.Move(index, index + offset);
        list.SelectedIndex = index + offset;
    }
    private void Confirm(object sender, RoutedEventArgs e)
    {
        var name = stem.Text.Trim();
        if (files.Count == 0 || files.Any(p => !File.Exists(p)))
        {
            MessageBox.Show(this, "请选择仍然存在的分卷文件。");
            return;
        }
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith('.') || name == "..")
        {
            MessageBox.Show(this, "请输入有效的基础文件名。");
            return;
        }
        if (files.Select(Path.GetDirectoryName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1)
        {
            MessageBox.Show(this, "请先将同一组分卷放在同一个文件夹。");
            return;
        }
        var type = (VolumeKind)kind.SelectedIndex;
        var fmt = format.SelectedIndex switch
        {
            1 => ArchiveFormat.SevenZip,
            2 => ArchiveFormat.Rar,
            _ => ArchiveFormat.Zip
        };
        if (type == VolumeKind.Single && files.Count != 1)
        {
            MessageBox.Show(this, "单个压缩包只能包含一个文件；多个独立包请分别加入任务。");
            return;
        }
        if ((type is VolumeKind.RarParts or VolumeKind.RarLegacy) && fmt != ArchiveFormat.Rar || type == VolumeKind.ZipSplit && fmt != ArchiveFormat.Zip)
        {
            MessageBox.Show(this, "分卷类型与真实格式不一致。");
            return;
        }
        var extension = fmt switch
        {
            ArchiveFormat.SevenZip => "7z",
            ArchiveFormat.Rar => "rar",
            _ => "zip"
        };
        var members = files.Select((path, index) => new ArchiveMember(path, type switch
        {
            VolumeKind.Single => $"{name}.{extension}",
            VolumeKind.ByteSplit => $"{name}.{extension}.{index + 1:D3}",
            VolumeKind.RarParts => $"{name}.part{index + 1:D2}.rar",
            VolumeKind.RarLegacy => index == 0 ? $"{name}.rar" : $"{name}.r{index - 1:D2}",
            VolumeKind.ZipSplit => index == files.Count - 1 ? $"{name}.zip" : $"{name}.z{index + 1:D2}",
            _ => throw new InvalidOperationException()
        }, index + 1)).ToArray();
        Candidate = new ArchiveCandidate { DisplayName = name, Format = fmt, VolumeKind = type, Members = members, EntryMemberIndex = type == VolumeKind.ZipSplit ? members.Length - 1 : 0, Status = CandidateStatus.Ready, Explanation = "用户已确认分组及顺序，解压前将验证内容。" };
        DialogResult = true;
    }
}
