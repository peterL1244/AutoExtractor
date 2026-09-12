using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using AutoExtractor.Core;
using AutoExtractor.Engine;
using Microsoft.Win32;

namespace AutoExtractor.App;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<TaskRow> rows = [];
    private readonly IArchiveScanner scanner = new ArchiveScanner();
    private readonly RenameService renamer = new();
    private readonly ExtractionCoordinator coordinator;
    private CancellationTokenSource? active;
    private bool closeWhenIdle;
    private string? lastOutput;
    public MainWindow()
    {
        InitializeComponent();
        coordinator = new ExtractionCoordinator(scanner, renamer, new SevenZipEngine(Path.Combine(AppContext.BaseDirectory, "tools", "7zip", "7z.exe")));
        TaskList.ItemsSource = rows;
    }
    private void SetBusy(bool busy)
    {
        AddFilesButton.IsEnabled = AddFolderButton.IsEnabled = StartButton.IsEnabled = ClearButton.IsEnabled = GroupButton.IsEnabled = ContinueButton.IsEnabled = RestoreButton.IsEnabled = OutputBrowseButton.IsEnabled = OutputBox.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
        ProgressBar.IsIndeterminate = busy;
        if (!busy)
        {
            active?.Dispose();
            active = null;
            ProgressBar.IsIndeterminate = false;
            if (closeWhenIdle)
                Close();
        }
    }
    private void RefreshCount()
    {
        CountLabel.Text = $"任务列表  /  {rows.Count}";
        EmptyState.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void AddRows(IEnumerable<ArchiveCandidate> candidates)
    {
        var known = rows.SelectMany(r => r.Candidate.Members).Select(m => Path.GetFullPath(m.SourcePath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates.Where(c => c.Status != CandidateStatus.Ignored && c.Members.Count > 0))
        {
            if (candidate.Members.Any(m => known.Contains(Path.GetFullPath(m.SourcePath))))
                continue;
            rows.Add(new TaskRow(candidate));
            foreach (var member in candidate.Members)
                known.Add(Path.GetFullPath(member.SourcePath));
        }
        RefreshCount();
        if (TaskList.SelectedItem is null && rows.Count > 0)
            TaskList.SelectedIndex = 0;
    }
    public async Task ImportAsync(string[] inputs)
    {
        if (active is not null)
            return;
        active = new CancellationTokenSource();
        SetBusy(true);
        StatusLabel.Text = "正在识别文件内容…";
        try
        {
            var scan = await Task.Run(() => scanner.ScanAsync(inputs, active.Token));
            AddRows(scan.Candidates);
            StatusLabel.Text = scan.Candidates.Count == 0 ? "没有发现可处理的压缩包" : "扫描完成 · 点击开始解压";
            if (scan.Messages.Count > 0)
                DetailsBox.Text = string.Join(Environment.NewLine, scan.Messages);
        }
        catch (OperationCanceledException) { StatusLabel.Text = "已取消扫描"; }
        catch (Exception ex) { ShowError("扫描失败", ex); }
        finally { SetBusy(false); }
    }
    private async void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Multiselect = true, Title = "选择压缩包或分卷", Filter = "所有文件|*.*" };
        if (dialog.ShowDialog(this) == true)
            await ImportAsync(dialog.FileNames);
    }
    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Multiselect = true, Title = "选择下载文件夹" };
        if (dialog.ShowDialog(this) == true)
            await ImportAsync(dialog.FolderNames);
    }
    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            await ImportAsync(paths);
    }
    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = active is null && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }
    private void OutputBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择解压结果保存位置" };
        if (dialog.ShowDialog(this) == true)
            OutputBox.Text = dialog.FolderName;
    }
    private void TaskList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (TaskList.SelectedItem is TaskRow row)
            DetailsBox.Text = row.Details;
    }
    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        rows.Clear();
        RefreshCount();
        DetailsBox.Text = "添加文件后，这里会显示识别结果和拟还原名称。";
    }
    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (active is not null)
            return;
        var tasks = rows.Where(r => r.Included && r.Candidate.Status == CandidateStatus.Ready && r.Result is null).ToList();
        if (tasks.Count == 0)
        {
            StatusLabel.Text = "请先添加文件，或选中待确认任务调整分卷";
            return;
        }
        string? output = string.IsNullOrWhiteSpace(OutputBox.Text) ? null : OutputBox.Text.Trim();
        if (output is not null && !Path.IsPathFullyQualified(output))
        {
            StatusLabel.Text = "保存位置需要完整目录路径";
            return;
        }
        active = new CancellationTokenSource();
        SetBusy(true);
        int completed = 0;
        try
        {
            foreach (var row in tasks)
            {
                if (active.IsCancellationRequested)
                    break;
                TaskList.SelectedItem = row;
                row.Status = "解压中";
                using var progress = new BufferedUiProgress(p => { StatusLabel.Text = p.Layer > 0 ? $"第 {p.Layer} 层 · {p.Message}" : p.Message; ProgressBar.IsIndeterminate = p.Percent is null; if (p.Percent.HasValue) ProgressBar.Value = p.Percent.Value; });
                try
                {
                    var attemptCandidate = await Task.Run(() => renamer.ResolveAppliedCandidateAsync(row.Candidate, active.Token));
                    row.Result = await Task.Run(() => coordinator.RunAsync(attemptCandidate, new ExtractionOptions(output), RequestPasswordAsync, progress, active.Token));
                    row.Status = row.Result.RemainingCandidates.Count > 0 || row.Result.Messages.Any(m => m.Contains("失败") || m.Contains("未完成")) ? "已解压 · 待处理" : "完成";
                    row.Details += Environment.NewLine + "结果目录：" + row.Result.OutputDirectory + Environment.NewLine + string.Join(Environment.NewLine, row.Result.Messages);
                    lastOutput = row.Result.OutputDirectory;
                    completed++;
                }
                catch (OperationCanceledException) { row.Status = "已取消"; row.Details += "\n任务已取消；已还原的文件名可通过恢复原名撤销。"; }
                catch (Exception ex)
                {
                    row.Status = "未完成 · 可重试";
                    row.Details += Environment.NewLine + ex.Message;
                }
                DetailsBox.Text = row.Details;
            }
            StatusLabel.Text = active.IsCancellationRequested ? "已取消 · 原压缩包保留" : $"本次处理 {completed}/{tasks.Count} 个任务 · 查看任务状态";
        }
        finally { SetBusy(false); }
    }
    private async Task<string?> RequestPasswordAsync(PasswordRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return await Dispatcher.InvokeAsync(() =>
        {
            token.ThrowIfCancellationRequested();
            var dialog = new PasswordDialog(request) { Owner = this };
            using var registration = token.Register(() => Dispatcher.BeginInvoke(new Action(() => { if (dialog.IsVisible) dialog.Close(); })));
            return dialog.ShowDialog() == true ? dialog.Password : null;
        });
    }
    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        active?.Cancel();
        StatusLabel.Text = "正在取消，请等待当前文件操作结束…";
    }
    private void Open_Click(object sender, RoutedEventArgs e)
    {
        var output = (TaskList.SelectedItem as TaskRow)?.Result?.OutputDirectory ?? lastOutput;
        if (output is not null && Directory.Exists(output))
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { output }, UseShellExecute = false });
        else
            StatusLabel.Text = "暂无解压结果";
    }
    private void Group_Click(object sender, RoutedEventArgs e)
    {
        var row = TaskList.SelectedItem as TaskRow;
        var dialog = new GroupDialog(row?.Candidate) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Candidate is null)
            return;
        if (row is not null)
            rows.Remove(row);
        // The user can combine a group from several previously detected candidates.
        var paths = dialog.Candidate.Members.Select(m => m.SourcePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var overlap in rows.Where(r => r.Candidate.Members.Any(m => paths.Contains(m.SourcePath))).ToList())
            rows.Remove(overlap);
        AddRows([dialog.Candidate]);
        TaskList.SelectedItem = rows.LastOrDefault();
    }
    private async void Continue_Click(object sender, RoutedEventArgs e)
    {
        if (TaskList.SelectedItem is not TaskRow row || row.Result is null)
        {
            StatusLabel.Text = "选中已解压的任务后可继续处理内层";
            return;
        }
        if (row.Result.RemainingCandidates.Count > 0)
            AddRows(row.Result.RemainingCandidates);
        else
            await ImportAsync([row.Result.OutputDirectory]);
        StatusLabel.Text = "内层候选已加入列表 · 可调整分卷后开始解压";
    }
    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (active is not null)
            return;
        var row = TaskList.SelectedItem as TaskRow;
        var initial = row?.Candidate.Members.FirstOrDefault()?.SourcePath;
        var dialog = new OpenFileDialog { Title = "选择改名记录以恢复原名", Filter = "改名记录|*.json", Multiselect = true, InitialDirectory = initial is null ? "" : Path.Combine(Path.GetDirectoryName(initial)!, ".autoextractor-journals") };
        if (row?.Result?.Journals.FirstOrDefault() is string recent)
            dialog.InitialDirectory = Path.GetDirectoryName(recent);
        if (dialog.ShowDialog(this) != true)
            return;
        active = new CancellationTokenSource();
        SetBusy(true);
        try
        {
            foreach (var journal in dialog.FileNames)
                await Task.Run(() => renamer.RestoreAsync(journal, active.Token));
            StatusLabel.Text = "已恢复原名 · 重新扫描后可再次解压";
        }
        catch (OperationCanceledException) { StatusLabel.Text = "已取消恢复"; }
        catch (Exception ex) { ShowError("无法恢复原名", ex); }
        finally { SetBusy(false); }
    }
    private void ShowError(string title, Exception ex)
    {
        StatusLabel.Text = title;
        DetailsBox.Text = ex.Message;
        MessageBox.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (active is not null)
        {
            e.Cancel = true;
            closeWhenIdle = true;
            active.Cancel();
        }
    }
}
