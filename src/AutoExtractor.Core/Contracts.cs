namespace AutoExtractor.Core;

public enum ArchiveFormat
{
    Unknown, Zip, SevenZip, Rar
}
public enum VolumeKind
{
    Single, ByteSplit, RarParts, RarLegacy, ZipSplit
}
public enum CandidateStatus
{
    Ready, NeedsConfirmation, MissingVolumes, Ignored
}
public record ArchiveMember(string SourcePath, string RestoredName, int Order);
public sealed record ArchiveCandidate
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string DisplayName
    {
        get; init;
    }
    public ArchiveFormat Format
    {
        get; init;
    }
    public VolumeKind VolumeKind
    {
        get; init;
    }
    public IReadOnlyList<ArchiveMember> Members { get; init; } = [];
    public int EntryMemberIndex
    {
        get; init;
    }
    public CandidateStatus Status
    {
        get; init;
    }
    public string Explanation { get; init; } = "";
    public bool IsSelfExtracting
    {
        get; init;
    }
    public string EntryPath => Members[EntryMemberIndex].SourcePath;
}
public record ScanResult(IReadOnlyList<ArchiveCandidate> Candidates, IReadOnlyList<string> Messages);
public record SignatureInfo(ArchiveFormat Format, bool IsExecutable, bool IsSelfExtracting, bool IsVolume, int? VolumeNumber = null);
public interface IArchiveScanner
{
    Task<ScanResult> ScanAsync(IEnumerable<string> inputs, CancellationToken cancellationToken = default);
}
public record RenameResult(ArchiveCandidate Candidate, string? JournalPath);
public interface IRenameService
{
    Task<RenameResult> ApplyAsync(ArchiveCandidate candidate, CancellationToken cancellationToken = default);
    Task RestoreAsync(string journalPath, CancellationToken cancellationToken = default);
    IReadOnlyList<string> FindJournals(string directory);
}
public enum EngineFailure
{
    NeedsPassword, PasswordOrCorruption, MissingVolumes, CorruptArchive, Unsupported, DiskFull, AccessDenied, UnsafePath
}
public sealed class ArchiveException(EngineFailure failure, string message) : Exception(message)
{
    public EngineFailure Failure { get; } = failure;
}
public record ArchiveEntry(string Path, long Size, bool IsDirectory, bool IsLink = false);
public record ArchiveListing(ArchiveFormat Format, IReadOnlyList<ArchiveEntry> Entries, bool IsEncrypted);
public record ExtractionProgress(string Message, int? Percent = null, int Layer = 0);
public interface IArchiveEngine
{
    Task<ArchiveListing> ListAsync(string archivePath, string? password, CancellationToken cancellationToken = default);
    Task ExtractAsync(string archivePath, string outputDirectory, string? password, IProgress<ExtractionProgress>? progress = null, CancellationToken cancellationToken = default);
}
public record PasswordRequest(string ArchiveName, int Layer, bool PreviousFailed, string Message);
public record ExtractionOptions(string? OutputDirectory = null, int MaxDepth = 10, bool SmartStop = true);
public record ExtractionResult(string OutputDirectory, IReadOnlyList<string> Journals, IReadOnlyList<ArchiveCandidate> RemainingCandidates, IReadOnlyList<string> Messages, string? ContentDirectory = null)
{
    public string BrowseDirectory => ContentDirectory ?? OutputDirectory;
}
