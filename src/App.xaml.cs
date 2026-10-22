using System.Configuration;
using System.Data;
using System.Windows;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Text.Json;

namespace CallRecorder;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length == 3 && e.Args[0] == "--translate-german")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                var result = await Task.Run(async () =>
                {
                    using var translator = await GermanArabicTranslator.CreateAsync(null, CancellationToken.None);
                    return translator.Translate(e.Args[1], CancellationToken.None);
                });
                await File.WriteAllTextAsync(e.Args[2], JsonSerializer.Serialize(new { success = true, german=e.Args[1], arabic=result }));
                Shutdown(0);
            }
            catch (Exception ex)
            {
                await File.WriteAllTextAsync(e.Args[2], JsonSerializer.Serialize(new { success=false, error=ex.ToString() }));
                Shutdown(1);
            }
            return;
        }
        if (e.Args.Length == 4 && e.Args[0] == "--transcribe")
        {
            // A command-line path also verifies the packaged EXE without using desktop automation.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                var result = await Task.Run(() => TranscriptionService.TranscribeAsync(e.Args[1], e.Args[2], null, CancellationToken.None));
                await File.WriteAllTextAsync(e.Args[3], JsonSerializer.Serialize(new { success = true, result, runtime = Whisper.net.LibraryLoader.RuntimeOptions.LoadedLibrary.ToString() }));
                Shutdown(0);
            }
            catch (Exception ex)
            {
                await File.WriteAllTextAsync(e.Args[3], JsonSerializer.Serialize(new { success = false, error = ex.ToString() }));
                Shutdown(1);
            }
            return;
        }
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        if (e.Args.Length >= 2 && e.Args[0] == "--snapshot")
        {
            if (e.Args.Length > 2 && e.Args[2] == "transcript")
                window.MainTabs.SelectedItem = window.TranscriptTab;
            if (e.Args.Length > 2 && e.Args[2] == "live")
                window.MainTabs.SelectedItem = window.LiveTab;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(e.Args[1])) encoder.Save(file);
            window.Close();
        }
    }
}
