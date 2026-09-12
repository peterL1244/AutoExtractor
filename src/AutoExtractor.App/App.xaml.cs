using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AutoExtractor.App;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var window = new MainWindow();
            MainWindow = window;
            if (e.Args.Length == 2 && e.Args[0] == "--render-preview")
            {
                // Render this application's own WPF layout for reproducible visual QA.
                window.Show();
                window.UpdateLayout();
                await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(window);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var file = File.Create(Path.GetFullPath(e.Args[1])))
                    encoder.Save(file);
                Shutdown(0);
                return;
            }
            window.Show();
            if (e.Args.Length > 0)
                await window.ImportAsync(e.Args.Where(p => File.Exists(p) || Directory.Exists(p)).ToArray());
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "AutoExtractor 无法启动", MessageBoxButton.OK, MessageBoxImage.Error); Shutdown(1); }
    }
}
