using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CallRecorder;

public partial class MainWindow : Window
{
    private readonly AppSettings settings = AppSettings.Load();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private RecordingEngine? engine;
    private bool busy, finishing, closeRequested, allowClose;
    private CancellationTokenSource? transcriptCancellation;
    private bool transcribing;

    public MainWindow()
    {
        InitializeComponent();
        FolderLabel.Text = settings.Folder;
        MicGain.Value = settings.MicGain;
        SystemGain.Value = settings.SystemGain;
        UseMic.IsChecked = settings.UseMic;
        UseSystem.IsChecked = settings.UseSystem;
        AutoTranscribe.IsChecked = settings.AutoTranscribe;
        foreach (ComboBoxItem item in TranscriptLanguage.Items)
            if (item.Tag as string == settings.Language) TranscriptLanguage.SelectedItem = item;
        RefreshModelLabel();
        PlayButton.IsEnabled = File.Exists(settings.LastFile);
        OpenTextButton.IsEnabled = File.Exists(settings.LastTranscript);
        UpdateControls();
        RefreshDevices();
        InitializeLive();
        timer.Tick += Timer_Tick;
        timer.Start();
    }

    private void RefreshDevices()
    {
        try
        {
            var micId = (MicDevices.SelectedItem as AudioDevice)?.Id ?? settings.MicId;
            var outputId = (OutputDevices.SelectedItem as AudioDevice)?.Id ?? settings.OutputId;
            var microphones = AudioDevices.List(DataFlow.Capture);
            var outputs = AudioDevices.List(DataFlow.Render);
            MicDevices.ItemsSource = microphones;
            OutputDevices.ItemsSource = outputs;
            string? defaultMic = AudioDevices.DefaultId(DataFlow.Capture), defaultOutput = AudioDevices.DefaultId(DataFlow.Render);
            MicDevices.SelectedItem = microphones.FirstOrDefault(d => d.Id == micId)
                ?? microphones.FirstOrDefault(d => d.Id == defaultMic) ?? microphones.FirstOrDefault();
            OutputDevices.SelectedItem = outputs.FirstOrDefault(d => d.Id == outputId)
                ?? outputs.FirstOrDefault(d => d.Id == defaultOutput) ?? outputs.FirstOrDefault();
            if (microphones.Count == 0 || outputs.Count == 0)
                ResultLabel.Text = "بعض أجهزة الصوت غير متاحة. وصّل الجهاز ثم اضغط تحديث الأجهزة، أو عطّل المصدر غير المستخدم.";
        }
        catch (Exception ex) { ResultLabel.Text = "تعذّر قراءة أجهزة الصوت: " + ex.Message; }
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (busy || engine is not null) return;
        bool useMic = UseMic.IsChecked == true, useSystem = UseSystem.IsChecked == true;
        string? micId = (MicDevices.SelectedItem as AudioDevice)?.Id;
        string? outputId = (OutputDevices.SelectedItem as AudioDevice)?.Id;
        if (!useMic && !useSystem) { ShowError("فعّل الميكروفون أو صوت الحاسوب أولًا."); return; }
        if (useMic && micId is null) { ShowError("لا يوجد ميكروفون متاح. وصّله أو عطّل تسجيل الميكروفون."); return; }
        if (useSystem && outputId is null) { ShowError("اختر جهاز إخراج صوت متاحًا."); return; }
        busy = true;
        UpdateControls();
        StatusLabel.Text = "جارٍ تجهيز التسجيل…";
        try
        {
            Directory.CreateDirectory(settings.Folder);
            var root = Path.GetPathRoot(Path.GetFullPath(settings.Folder));
            if (root is not null && new DriveInfo(root).AvailableFreeSpace < 100L * 1024 * 1024)
                throw new IOException("يلزم توفر 100 ميغابايت على الأقل لبدء التسجيل.");
            string path = Path.Combine(settings.Folder, $"Call_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_{Guid.NewGuid().ToString("N")[..6]}.wav");
            SaveSettings();
            engine = await Task.Run(() => CreateEngine(path, useMic ? micId : null, useSystem ? outputId : null));
            engine.MicGain = (float)MicGain.Value;
            engine.SystemGain = (float)SystemGain.Value;
            ResultLabel.Text = "جارٍ الحفظ: " + Path.GetFileName(path);
            StatusLabel.Text = "جارٍ التسجيل";
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(220, 67, 67));
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "تعذّر بدء التسجيل";
            ShowError("تعذّر تشغيل جهاز الصوت أو إنشاء الملف. تحقق من الجهاز ومن سماح ويندوز لتطبيقات سطح المكتب باستخدام الميكروفون.\n\n" + ex.Message);
        }
        finally
        {
            busy = false;
            UpdateControls();
            if (closeRequested)
            {
                if (engine is not null) await FinishAsync();
                TryClose();
            }
        }
    }

    private static RecordingEngine CreateEngine(string path, string? micId, string? outputId)
    {
        IWaveIn? mic = null, system = null;
        try
        {
            if (micId is not null) mic = AudioDevices.Open(micId, false);
            if (outputId is not null) system = AudioDevices.Open(outputId, true);
        }
        catch
        {
            mic?.Dispose();
            system?.Dispose();
            throw;
        }
        return RecordingEngine.Start(path, mic, system);
    }

    private async void Stop_Click(object sender, RoutedEventArgs e) => await FinishAsync();

    private async Task FinishAsync()
    {
        var current = engine;
        if (current is null || finishing) return;
        finishing = true;
        busy = true;
        UpdateControls();
        StatusLabel.Text = "جارٍ حفظ التسجيل…";
        current.RequestStop();
        var result = await current.Completion;
        engine = null;
        TimerLabel.Text = result.Duration.ToString(@"hh\:mm\:ss");
        MicMeter.Value = SystemMeter.Value = 0;
        StatusDot.Fill = new SolidColorBrush(Color.FromRgb(45, 109, 80));
        bool exists = File.Exists(result.FilePath);
        if (exists) settings.LastFile = result.FilePath;
        PlayButton.IsEnabled = exists;
        StatusLabel.Text = result.Error is null ? "تم حفظ التسجيل" : "توقف التسجيل";
        ResultLabel.Text = exists ? "تم الحفظ: " + result.FilePath : "تعذّر حفظ الملف.";
        SaveSettings();
        finishing = false;
        busy = false;
        UpdateControls();
        if (result.Error is not null)
            ShowError((exists ? "توقف التسجيل. تم الاحتفاظ بالجزء المتاح في:\n" + result.FilePath + "\n\n" : "تعذّر حفظ التسجيل.\n\n") + result.Error.Message);
        if (exists && result.Error is null && AutoTranscribe.IsChecked == true && !closeRequested && !LiveRunning)
            await ConvertAudioAsync(result.FilePath);
        else if (exists && LiveRunning && AutoTranscribe.IsChecked == true)
            ResultLabel.Text += "\nالترجمة المباشرة مستمرة. أوقفها ثم حوّل التسجيل كاملًا عند الحاجة.";
        TryClose();
    }

    private async void Timer_Tick(object? sender, EventArgs e)
    {
        await MonitorLiveAsync();
        var current = engine;
        if (current is null) return;
        TimerLabel.Text = current.Elapsed.ToString(@"hh\:mm\:ss");
        MicMeter.Value = current.MicPeak;
        SystemMeter.Value = current.SystemPeak;
        if (current.Completion.IsCompleted && !finishing) await FinishAsync();
    }

    private void UpdateControls()
    {
        bool idle = engine is null && !busy && !transcribing;
        StartButton.IsEnabled = idle;
        StopButton.IsEnabled = engine is not null && !busy;
        UseMic.IsEnabled = UseSystem.IsEnabled = idle;
        MicDevices.IsEnabled = OutputDevices.IsEnabled = idle;
        RefreshButton.IsEnabled = ChooseFolderButton.IsEnabled = idle;
        PlayButton.IsEnabled = idle && File.Exists(settings.LastFile);
        TranscribeLastButton.IsEnabled = idle && !LiveRunning && File.Exists(settings.LastFile);
        ImportAudioButton.IsEnabled = PrepareModelButton.IsEnabled = TranscriptLanguage.IsEnabled = idle && !LiveRunning;
        CancelTranscriptButton.IsEnabled = transcribing;
        AutoTranscribe.IsEnabled = !busy && !transcribing;
        UpdateLiveControls();
    }

    private void Gain_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (engine is null || MicGain is null || SystemGain is null) return;
        engine.MicGain = (float)MicGain.Value;
        engine.SystemGain = (float)SystemGain.Value;
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshDevices();
    private void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "اختر مجلد حفظ التسجيلات", InitialDirectory = Directory.Exists(settings.Folder) ? settings.Folder : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) };
        if (dialog.ShowDialog(this) == true)
        {
            settings.Folder = dialog.FolderName;
            FolderLabel.Text = settings.Folder;
            SaveSettings();
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try { Directory.CreateDirectory(settings.Folder); Process.Start(new ProcessStartInfo(settings.Folder) { UseShellExecute = true }); }
        catch (Exception ex) { ShowError("تعذّر فتح المجلد: " + ex.Message); }
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!File.Exists(settings.LastFile)) throw new FileNotFoundException("الملف غير موجود. افتح مجلد التسجيلات للتحقق منه.");
            Process.Start(new ProcessStartInfo(settings.LastFile) { UseShellExecute = true });
        }
        catch (Exception ex) { ShowError("تعذّر تشغيل الملف: " + ex.Message); }
    }

    private void SaveSettings()
    {
        settings.MicId = (MicDevices.SelectedItem as AudioDevice)?.Id;
        settings.OutputId = (OutputDevices.SelectedItem as AudioDevice)?.Id;
        settings.MicGain = MicGain.Value;
        settings.SystemGain = SystemGain.Value;
        settings.UseMic = UseMic.IsChecked == true;
        settings.UseSystem = UseSystem.IsChecked == true;
        settings.AutoTranscribe = AutoTranscribe.IsChecked == true;
        settings.Language = (TranscriptLanguage.SelectedItem as ComboBoxItem)?.Tag as string ?? "ar";
        settings.LiveSource = LiveSource;
        settings.LiveDeviceId = (LiveDevices.SelectedItem as AudioDevice)?.Id;
        settings.Save();
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (allowClose) return;
        if (engine is null && !busy && !transcribing && !LiveRunning) { SaveSettings(); timer.Stop(); return; }
        e.Cancel = true;
        closeRequested = true;
        transcriptCancellation?.Cancel();
        liveCancellation?.Cancel();
        if (liveSession is not null) { liveSession.Cancel(); await FinishLiveAsync(); }
        if (engine is not null) await FinishAsync();
        TryClose();
    }

    private void TryClose()
    {
        if (allowClose || !closeRequested || busy || engine is not null || transcribing || LiveRunning) return;
        allowClose = true;
        timer.Stop();
        Close();
    }

    private void ShowError(string message) => MessageBox.Show(this, message, "مسجل المكالمات", MessageBoxButton.OK, MessageBoxImage.Information, MessageBoxResult.OK, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);

    private void RefreshModelLabel() => ModelLabel.Text = TranscriptionService.HasModel
        ? "نموذج اللغة موجود على هذا الجهاز. التحويل محلي دون إنترنت."
        : "نموذج اللغة غير مُنزّل بعد. اضغط تجهيز النموذج أو ابدأ التحويل لتنزيله.";

    private async void PrepareModel_Click(object sender, RoutedEventArgs e) => await RunTranscriptionTaskAsync(null);

    private async void TranscribeLast_Click(object sender, RoutedEventArgs e)
    {
        if (File.Exists(settings.LastFile)) await ConvertAudioAsync(settings.LastFile);
        else ShowError("لا يوجد تسجيل محفوظ. سجّل مكالمة أو اختر ملفًا صوتيًا.");
    }

    private async void ImportAudio_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "اختر ملفًا صوتيًا لتحويله إلى نص",
            Filter = "ملفات صوتية|*.wav;*.mp3;*.m4a;*.aac;*.wma;*.aiff|جميع الملفات|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true) await ConvertAudioAsync(dialog.FileName);
    }

    private Task ConvertAudioAsync(string path) => RunTranscriptionTaskAsync(path);

    private async Task RunTranscriptionTaskAsync(string? audioPath)
    {
        if (transcribing || engine is not null || busy || LiveRunning) return;
        transcribing = true;
        transcriptCancellation = new CancellationTokenSource();
        MainTabs.SelectedItem = TranscriptTab;
        TranscriptProgress.Value = 0;
        TranscriptProgress.IsIndeterminate = true;
        TranscriptStatus.Text = audioPath is null ? "جارٍ تجهيز نموذج اللغة…" : "جارٍ تجهيز التحويل…";
        if (audioPath is not null)
        {
            TranscriptTextBox.Clear();
            TranscriptFileLabel.Text = "الملف الصوتي: " + audioPath;
            CopyTextButton.IsEnabled = false;
        }
        UpdateControls();
        SaveSettings();
        string language = settings.Language;
        TranscriptTextBox.FlowDirection = language is "en" or "de" ? FlowDirection.LeftToRight : FlowDirection.RightToLeft;
        var progress = new Progress<TranscriptionProgress>(update =>
        {
            if (!transcribing) return;
            TranscriptStatus.Text = update.Status;
            TranscriptProgress.IsIndeterminate = update.Percent <= 0;
            TranscriptProgress.Value = Math.Clamp(update.Percent, 0, 100);
            if (update.Text is not null)
            {
                TranscriptTextBox.AppendText(update.Text + Environment.NewLine);
                TranscriptTextBox.ScrollToEnd();
                CopyTextButton.IsEnabled = true;
            }
        });
        try
        {
            var token = transcriptCancellation.Token;
            if (audioPath is null)
            {
                await Task.Run(() => TranscriptionService.EnsureModelAsync(progress, token), token);
                TranscriptStatus.Text = "نموذج اللغة جاهز. يمكنك تحويل التسجيلات دون إنترنت.";
            }
            else
            {
                var result = await Task.Run(() => TranscriptionService.TranscribeAsync(audioPath, language, progress, token), token);
                settings.LastTranscript = result.TextPath;
                settings.Save();
                TranscriptFileLabel.Text = "تم حفظ النص: " + result.TextPath + "\nملف التوقيت: " + result.SubtitlePath;
                TranscriptStatus.Text = result.Segments == 0 ? "اكتمل التحويل؛ لم يُكتشف كلام واضح." : "اكتمل تحويل الصوت إلى نص.";
                OpenTextButton.IsEnabled = true;
            }
            TranscriptProgress.Value = 100;
        }
        catch (OperationCanceledException)
        {
            TranscriptStatus.Text = "أُلغي التحويل. التسجيل الصوتي محفوظ؛ أي نص مستخرج متاح في ملف partial.txt بجانبه.";
        }
        catch (Exception ex)
        {
            TranscriptStatus.Text = "تعذّر إكمال التحويل. التسجيل الصوتي محفوظ.";
            ShowError("تعذّر تحويل الصوت إلى نص. إذا كان هذا أول استخدام، تحقق من الإنترنت لتنزيل النموذج.\n\n" + ex.Message);
        }
        finally
        {
            TranscriptProgress.IsIndeterminate = false;
            transcribing = false;
            transcriptCancellation.Dispose();
            transcriptCancellation = null;
            RefreshModelLabel();
            UpdateControls();
            TryClose();
        }
    }

    private void CancelTranscript_Click(object sender, RoutedEventArgs e)
    {
        CancelTranscriptButton.IsEnabled = false;
        TranscriptStatus.Text = "جارٍ إلغاء التحويل…";
        transcriptCancellation?.Cancel();
    }

    private void CopyText_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(TranscriptTextBox.Text); }
        catch (Exception ex) { ShowError("تعذّر نسخ النص: " + ex.Message); }
    }

    private void OpenText_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(settings.LastTranscript) { UseShellExecute = true }); }
        catch (Exception ex) { ShowError("تعذّر فتح ملف النص: " + ex.Message); }
    }
}

public sealed class AppSettings
{
    private static readonly string SettingsFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoutCallRecorder", "settings.json");
    public string Folder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Call Recordings");
    public string? MicId { get; set; }
    public string? OutputId { get; set; }
    public double MicGain { get; set; } = 1;
    public double SystemGain { get; set; } = 1;
    public bool UseMic { get; set; } = true;
    public bool UseSystem { get; set; } = true;
    public string LastFile { get; set; } = "";
    public bool AutoTranscribe { get; set; } = true;
    public string Language { get; set; } = "ar";
    public string LastTranscript { get; set; } = "";
    public string LiveSource { get; set; } = "system";
    public string? LiveDeviceId { get; set; }

    public static AppSettings Load()
    {
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFile)) ?? new();
            if (string.IsNullOrWhiteSpace(settings.Folder) || !Path.IsPathFullyQualified(settings.Folder)) settings.Folder = new AppSettings().Folder;
            settings.MicGain = double.IsFinite(settings.MicGain) ? Math.Clamp(settings.MicGain, 0, 2) : 1;
            settings.SystemGain = double.IsFinite(settings.SystemGain) ? Math.Clamp(settings.SystemGain, 0, 2) : 1;
            return settings;
        }
        catch { return new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            string temp = SettingsFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, SettingsFile, true);
        }
        catch { /* Recording remains available even if settings cannot be saved. */ }
    }
}
