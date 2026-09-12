using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AutoExtractor.App;
using AutoExtractor.Core;

internal static class Program
{
    static readonly string Root = Path.Combine(Path.GetTempPath(), "AutoExtractor-ui-tests-" + Guid.NewGuid().ToString("N"));
    static int outcome = 1;
    [STAThread]
    static int Main()
    {
        Directory.CreateDirectory(Root);
        var app = new AutoExtractor.App.App();
        app.InitializeComponent();
        app.Startup += (_, _) => app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                await Tests((MainWindow)app.MainWindow);
                outcome = 0;
                Console.WriteLine("PASS WPF smoke: import, extract, password retry, manual continuation, group correction, cancel/retry, missing-input identity, modified-target identity, failure colors");
            }
            catch (Exception ex) { Console.WriteLine("FAIL WPF smoke: " + ex); }
            finally { app.Shutdown(outcome); }
        }), DispatcherPriority.ApplicationIdle);
        app.Run();
        // Test-owned isolated directory only; never user input.
        if (Path.GetFullPath(Root).StartsWith(Path.GetFullPath(Path.GetTempPath()) + "AutoExtractor-ui-tests-", StringComparison.OrdinalIgnoreCase))
            Directory.Delete(Root, true);
        return outcome;
    }
    static T Control<T>(MainWindow window, string name) where T : FrameworkElement => (T)window.FindName(name);
    static void Click(MainWindow window, string name) => Control<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    static void Check(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
    static string Zip(string name, params (string Name, byte[] Data)[] entries)
    {
        string path = Path.Combine(Root, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var entry in entries)
        {
            using var stream = zip.CreateEntry(entry.Name).Open();
            stream.Write(entry.Data);
        }
        return path;
    }
    static async Task Idle(MainWindow window)
    {
        var timeout = Stopwatch.StartNew();
        while (!Control<Button>(window, "StartButton").IsEnabled)
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(40))
                throw new Exception("UI task timed out");
            await Task.Delay(25);
        }
    }
    static List<TaskRow> Rows(MainWindow window) => Control<ListView>(window, "TaskList").Items.Cast<TaskRow>().ToList();
    static async Task Tests(MainWindow window)
    {
        Control<TextBox>(window, "OutputBox").Text = Path.Combine(Root, "output");
        await RetryIdentityRegressions(window);
        var simple = Zip("测试包.tmp", ("hello.txt", "中文内容"u8.ToArray()));
        await window.ImportAsync([simple]);
        Check(Rows(window).Count == 1, "Import should produce one task");
        var skipped = Zip("skip-this.tmp", ("skip.txt", "untouched"u8.ToArray()));
        await window.ImportAsync([skipped]);
        var skipRow = Rows(window).Last();
        var includeProperty = typeof(TaskRow).GetProperty("Included");
        Check(includeProperty is not null, "A task needs a selection toggle to skip ready files");
        includeProperty!.SetValue(skipRow, false);
        Click(window, "StartButton");
        await Idle(window);
        Check(skipRow.Result is null && File.Exists(skipped), "Unselected ready task must remain untouched");
        var row = Rows(window).First();
        Check(row.Status == "完成", row.Details);
        Check(!File.Exists(simple), "Disguised source should be renamed");
        var text = Directory.GetFiles(row.Result!.OutputDirectory, "hello.txt", SearchOption.AllDirectories).Single();
        Check(File.ReadAllText(text) == "中文内容", "Extracted content mismatch");
        Click(window, "ClearButton");
        // Real encrypted-header fixture and two UI password submissions, wrong then correct.
        string source = Path.Combine(Root, "secret.txt");
        File.WriteAllText(source, "public test fixture");
        string encrypted = Path.Combine(Root, "encrypted.7z");
        var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "tools", "7zip", "7z.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "a", encrypted, source, "-pSmokeFixtureOnly", "-mhe=on" })
            info.ArgumentList.Add(arg);
        using (var process = Process.Start(info)!)
        {
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            await stdout;
            await stderr;
            Check(process.ExitCode == 0, "Fixture creation failed");
        }
        await window.ImportAsync([encrypted]);
        int attempts = 0;
        var dialogs = new HashSet<Window>();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        timer.Tick += (_, _) => { foreach (var dialog in Application.Current.Windows.OfType<PasswordDialog>().ToArray()) { if (!dialogs.Add(dialog)) continue; attempts++; Find<PasswordBox>(dialog).Single().Password = attempts == 1 ? "WrongFixturePassword" : "SmokeFixtureOnly"; Find<Button>(dialog).Single(b => Equals(b.Content, "继续解压")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); } };
        timer.Start();
        Click(window, "StartButton");
        await Idle(window);
        timer.Stop();
        Check(attempts == 2, "Password retry dialog missing");
        Check(Rows(window).Single().Status == "完成", Rows(window).Single().Details);
        Click(window, "ClearButton");
        var nested = Zip("nested.zip", ("final.txt", "ready"u8.ToArray()));
        var outer = Zip("outer.zip", ("app.exe", File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "tools", "7zip", "7z.exe"))), ("resources/data.tmp", File.ReadAllBytes(nested)));
        await window.ImportAsync([outer]);
        Click(window, "StartButton");
        await Idle(window);
        row = Rows(window).Single();
        Check(row.Result!.RemainingCandidates.Count == 1, "Smart stop should retain inner candidate");
        Click(window, "ContinueButton");
        Check(Rows(window).Count == 2, "Continue should enqueue inner archive");
        Click(window, "StartButton");
        await Idle(window);
        Check(Rows(window).Last().Status == "完成", Rows(window).Last().Details);
        // Manual dialog must reject inconsistent group selection, and accept a valid one.
        var group = new GroupDialog(null);
        group.Loaded += (_, _) => group.Dispatcher.BeginInvoke(new Action(() =>
        {
            var list = Find<ListBox>(group).Single();
            var collection = (System.Collections.ObjectModel.ObservableCollection<string>)list.ItemsSource;
            collection.Add(nested);
            var combos = Find<ComboBox>(group).ToArray();
            combos[0].SelectedIndex = 0;
            combos[1].SelectedIndex = 0;
            Find<Button>(group).Single(b => Equals(b.Content, "确认并加入任务")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }));
        group.ShowDialog();
        Check(group.Candidate?.Members.Count == 1, "Manual group did not create candidate");
        Click(window, "ClearButton");
        string cancelFile = Zip("cancel-me.tmp", ("large.bin", System.Security.Cryptography.RandomNumberGenerator.GetBytes(64 * 1024 * 1024)));
        await window.ImportAsync([cancelFile]);
        bool canceled = false;
        var cancelTimer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(1) };
        cancelTimer.Tick += (_, _) => { if (!canceled && !File.Exists(cancelFile) && Control<Button>(window, "CancelButton").IsEnabled) { canceled = true; Click(window, "CancelButton"); } };
        cancelTimer.Start();
        Click(window, "StartButton");
        await Idle(window);
        cancelTimer.Stop();
        Check(canceled && Rows(window).Single().Status == "已取消", "Cancellation must occur after source rename; canceled=" + canceled + "; status=" + Rows(window).Single().Status + "; " + Rows(window).Single().Details);
        Click(window, "StartButton");
        await Idle(window);
        Check(Rows(window).Single().Status == "完成", "First retry after rename/cancel must work: " + Rows(window).Single().Details);
    }
    static async Task RetryIdentityRegressions(MainWindow window)
    {
        var original = Zip("missing-input.tmp", ("selected.txt", "selected archive"u8.ToArray()));
        var unrelated = Zip("missing-input.zip", ("unrelated.txt", "must stay untouched"u8.ToArray()));
        var unrelatedBytes = File.ReadAllBytes(unrelated);
        await window.ImportAsync([original]);
        Check(Rows(window).Count == 1, "Only explicitly imported input should be queued");
        File.Delete(original);
        Click(window, "StartButton");
        await Idle(window);
        var row = Rows(window).Single();
        Check(row.Result is null && row.Status == "未完成 · 可重试", "Missing initial source was silently substituted by an unrelated target: " + row.Details);
        Check(row.Candidate.EntryPath == original, "Missing input candidate identity must remain unchanged");
        Check(File.ReadAllBytes(unrelated).SequenceEqual(unrelatedBytes), "Unrelated target changed");
        Check(!Directory.Exists(Path.Combine(Root, "output")), "Missing initial input must fail before creating extraction output");
        Check(row.StatusBrush is SolidColorBrush failedBrush && failedBrush.Color == Colors.DarkGoldenrod, "Retryable failure must use warning color");
        row.Status = "完成";
        Check(row.StatusBrush is SolidColorBrush successBrush && successBrush.Color == Colors.SeaGreen, "Explicit completion must stay green");
        Click(window, "ClearButton");

        original = Zip("modified-target.tmp", ("original.txt", "initial contents"u8.ToArray()));
        await window.ImportAsync([original]);
        row = Rows(window).Single();
        var committed = await new RenameService().ApplyAsync(row.Candidate);
        var replacement = Zip("replacement-fixture.zip", ("unrelated.txt", "valid but unrelated archive"u8.ToArray()));
        var replacementBytes = File.ReadAllBytes(replacement);
        File.WriteAllBytes(committed.Candidate.EntryPath, replacementBytes);
        Click(window, "StartButton");
        await Idle(window);
        Check(row.Result is null && row.Status == "未完成 · 可重试", "Modified committed target was processed without identity verification: " + row.Details);
        Check(row.Candidate.EntryPath == original, "Modified target must not replace selected candidate");
        Check(File.ReadAllBytes(committed.Candidate.EntryPath).SequenceEqual(replacementBytes), "Modified target unexpectedly mutated");
        Check(!Directory.Exists(Path.Combine(Root, "output")), "Modified target must fail before creating extraction output");
        Click(window, "ClearButton");
    }

    static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                yield return match;
            foreach (var nested in Find<T>(child))
                yield return nested;
        }
    }
}
