using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using AutoExtractor.Core;

namespace AutoExtractor.App;

public sealed class TaskRow(ArchiveCandidate candidate) : INotifyPropertyChanged
{
    private ArchiveCandidate candidateValue = candidate;
    public ArchiveCandidate Candidate
    {
        get => candidateValue; set
        {
            candidateValue = value;
            Changed();
            Changed(nameof(Name));
            Changed(nameof(Subtitle));
            Changed(nameof(FormatLabel));
        }
    }
    public string Name => Candidate.DisplayName;
    private bool included = true;
    public bool Included
    {
        get => included; set
        {
            included = value;
            Changed();
        }
    }
    public string Subtitle
    {
        get
        {
            int folders = Candidate.Members.Select(m => System.IO.Path.GetDirectoryName(m.SourcePath)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            return folders > 1 ? $"{Candidate.Members.Count} 个文件 · 来自 {folders} 个文件夹" : $"{Candidate.Members.Count} 个文件 · {System.IO.Path.GetDirectoryName(Candidate.EntryPath)}";
        }
    }
    public string FormatLabel => Candidate.Format == ArchiveFormat.SevenZip ? "7z" : Candidate.Format.ToString().ToUpperInvariant();
    private string status = candidate.Status switch { CandidateStatus.Ready => "待解压", CandidateStatus.MissingVolumes => "缺少分卷", _ => "待确认" };
    public string Status
    {
        get => status; set
        {
            status = value;
            Changed();
            Changed(nameof(StatusBrush));
        }
    }
    public Brush StatusBrush => Status == "完成" ? Brushes.SeaGreen : Status is "待解压" or "解压中" ? new SolidColorBrush(Color.FromRgb(36, 89, 224)) : Brushes.DarkGoldenrod;
    public ExtractionResult? Result
    {
        get; set;
    }
    public string Details { get; set; } = Describe(candidate);
    private static string Describe(ArchiveCandidate candidate)
    {
        bool acrossFolders = candidate.Members.Select(m => System.IO.Path.GetDirectoryName(m.SourcePath)).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;
        string destination = acrossFolders ? $"\n验证后集中到：{System.IO.Path.GetDirectoryName(candidate.EntryPath)}\n恢复原名时会同时恢复原文件夹位置。" : "";
        return candidate.Explanation + destination + Environment.NewLine + string.Join(Environment.NewLine,
            candidate.Members.Select(m => $"{(acrossFolders ? m.SourcePath : System.IO.Path.GetFileName(m.SourcePath))}  →  {m.RestoredName}"));
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
