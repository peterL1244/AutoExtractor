using System.Windows.Threading;
using AutoExtractor.Core;

namespace AutoExtractor.App;
// Coalescing avoids flooding the WPF dispatcher when an archive has many files.
internal sealed class BufferedUiProgress : IProgress<ExtractionProgress>, IDisposable
{
    private ExtractionProgress? latest;
    private readonly DispatcherTimer timer;
    public BufferedUiProgress(Action<ExtractionProgress> apply)
    {
        timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(80) };
        timer.Tick += (_, _) => { var value = Interlocked.Exchange(ref latest, null); if (value is not null) apply(value); };
        timer.Start();
    }
    public void Report(ExtractionProgress value) => Interlocked.Exchange(ref latest, value);
    public void Dispose()
    {
        timer.Stop();
        Interlocked.Exchange(ref latest, null);
    }
}
