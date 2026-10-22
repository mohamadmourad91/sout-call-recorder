using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using NAudio.CoreAudioApi;

namespace CallRecorder;

public partial class MainWindow
{
    private readonly ObservableCollection<LiveLine> liveLines = [];
    private LiveTranslationSession? liveSession;
    private CancellationTokenSource? liveCancellation;
    private bool liveStarting, liveFinishing;
    private string lastLiveLog = "";
    private bool LiveRunning => liveStarting || liveSession is not null;

    private void InitializeLive()
    {
        LiveTranscriptList.ItemsSource = liveLines;
        foreach (ComboBoxItem item in LiveSourceKind.Items)
            if (item.Tag as string == settings.LiveSource) LiveSourceKind.SelectedItem = item;
        RefreshLiveDevices();
    }

    private string LiveSource => (LiveSourceKind.SelectedItem as ComboBoxItem)?.Tag as string ?? "system";

    private void RefreshLiveDevices()
    {
        if (LiveDevices is null || LiveRunning) return;
        try
        {
            DataFlow flow = LiveSource == "mic" ? DataFlow.Capture : DataFlow.Render;
            var devices = AudioDevices.List(flow);
            string? id = (LiveDevices.SelectedItem as AudioDevice)?.Id ?? settings.LiveDeviceId;
            string? defaultId = AudioDevices.DefaultId(flow);
            LiveDevices.ItemsSource = devices;
            LiveDevices.SelectedItem = devices.FirstOrDefault(d => d.Id == id)
                ?? devices.FirstOrDefault(d => d.Id == (LiveSource == "mic" ? settings.MicId : settings.OutputId))
                ?? devices.FirstOrDefault(d => d.Id == defaultId) ?? devices.FirstOrDefault();
            if (devices.Count == 0) LiveStatus.Text = "لا يوجد جهاز صوت متاح لهذا المصدر. وصّل الجهاز ثم اضغط تحديث.";
        }
        catch (Exception ex) { if (LiveStatus is not null) LiveStatus.Text = "تعذّر قراءة الأجهزة: " + ex.Message; }
    }

    private void LiveSource_Changed(object sender, SelectionChangedEventArgs e) => RefreshLiveDevices();
    private void RefreshLive_Click(object sender, RoutedEventArgs e) => RefreshLiveDevices();

    private void UpdateLiveControls()
    {
        StartLiveButton.IsEnabled = !LiveRunning && !transcribing && !busy && !closeRequested;
        StopLiveButton.IsEnabled = LiveRunning;
        StopLiveButton.Content = liveFinishing ? "إلغاء الانتظار" : "■  إيقاف الترجمة";
        LiveSourceKind.IsEnabled = LiveDevices.IsEnabled = RefreshLiveButton.IsEnabled = !LiveRunning;
        OpenLiveLogButton.IsEnabled = File.Exists(lastLiveLog);
    }

    private async void StartLive_Click(object sender, RoutedEventArgs e)
    {
        if (LiveRunning || transcribing || busy) return;
        if (LiveDevices.SelectedItem is not AudioDevice device) { ShowError("اختر جهاز الصوت الذي يحتوي على الحديث الألماني."); return; }
        bool loopback = LiveSource != "mic";
        string id = device.Id;
        liveStarting = true;
        liveCancellation = new CancellationTokenSource();
        liveLines.Clear();
        LiveStatus.Text = "جارٍ تجهيز الاستماع والترجمة…";
        LiveSetupProgress.Visibility = Visibility.Visible;
        LiveSetupProgress.IsIndeterminate = true;
        MainTabs.SelectedItem = LiveTab;
        SaveSettings();
        UpdateControls();
        var progress = new Progress<LiveUpdate>(update =>
        {
            if (!LiveRunning) return;
            LiveStatus.Text = update.Status;
            if (liveStarting)
            {
                LiveSetupProgress.IsIndeterminate = update.Percent is null;
                LiveSetupProgress.Value = Math.Clamp(update.Percent ?? 0, 0, 100);
            }
            if (update.Line is not null)
            {
                if (liveLines.Count > 0 && liveLines[^1].Arabic == "جارٍ الترجمة…" && liveLines[^1].German == update.Line.German)
                    liveLines[^1] = update.Line;
                else liveLines.Add(update.Line);
                LiveTranscriptList.ScrollIntoView(liveLines[^1]);
            }
        });
        try
        {
            var token = liveCancellation.Token;
            liveSession = await Task.Run(() => LiveTranslationSession.StartAsync(() => AudioDevices.Open(id, loopback), settings.Folder, progress, token), token);
            lastLiveLog = liveSession.LogPath;
            LiveLogLabel.Text = "يُحفظ النص الألماني والعربي في: " + lastLiveLog;
            LiveStatus.Text = "الاستماع يعمل — تنتظر الترجمة مقطع كلام ألماني.";
        }
        catch (OperationCanceledException) { LiveStatus.Text = "أُلغي تجهيز الترجمة المباشرة."; }
        catch (Exception ex)
        {
            LiveStatus.Text = "تعذّر بدء الترجمة المباشرة.";
            ShowError("تعذّر بدء الاستماع والترجمة. تحقق من جهاز الصوت ومن وجود نموذج التعرف على الكلام.\n\n" + ex.Message);
        }
        finally
        {
            liveStarting = false;
            LiveSetupProgress.Visibility = Visibility.Collapsed;
            if (liveSession is null) { liveCancellation.Dispose(); liveCancellation = null; }
            UpdateControls();
            if (closeRequested && liveSession is not null)
            {
                liveSession.Cancel();
                await FinishLiveAsync();
            }
            TryClose();
        }
    }

    private async void StopLive_Click(object sender, RoutedEventArgs e)
    {
        if (liveStarting)
        {
            LiveStatus.Text = "جارٍ إلغاء تجهيز الترجمة…";
            liveCancellation?.Cancel();
            return;
        }
        if (liveSession is null) return;
        if (liveFinishing)
        {
            LiveStatus.Text = "جارٍ إلغاء الانتظار وحفظ النص المتاح…";
            liveSession.Cancel();
            return;
        }
        liveSession.RequestStop();
        await FinishLiveAsync();
    }

    private async Task FinishLiveAsync()
    {
        var current = liveSession;
        if (current is null || liveFinishing) return;
        liveFinishing = true;
        LiveStatus.Text = "جارٍ إكمال المقطع الأخير وحفظ النص…";
        UpdateControls();
        try
        {
            var result = await current.Completion;
            lastLiveLog = result.LogPath;
            LiveStatus.Text = result.Error is null ? "توقفت الترجمة المباشرة وحُفظ النص المتاح." : "توقفت الترجمة: " + result.Error.Message;
            LiveLogLabel.Text = "النص الألماني والعربي محفوظ في: " + result.LogPath;
        }
        catch (Exception ex) { LiveStatus.Text = "توقفت الترجمة: " + ex.Message; }
        finally
        {
            // Leave a pending German sentence visible if the user cancelled before translation finished.
            for (int i = 0; i < liveLines.Count; i++)
                if (liveLines[i].Arabic == "جارٍ الترجمة…") liveLines[i] = liveLines[i] with { Arabic = "لم تكتمل ترجمة هذا المقطع.", TranslationFailed = true };
            liveSession = null;
            liveFinishing = false;
            liveCancellation?.Dispose();
            liveCancellation = null;
            LiveMeter.Value = 0;
            UpdateControls();
            TryClose();
        }
    }

    private async Task MonitorLiveAsync()
    {
        if (liveSession is null) return;
        LiveMeter.Value = liveSession.Peak;
        OpenLiveLogButton.IsEnabled = File.Exists(lastLiveLog);
        if (liveSession.Completion.IsCompleted && !liveFinishing) await FinishLiveAsync();
    }

    private void OpenLiveLog_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(lastLiveLog) { UseShellExecute = true }); }
        catch (Exception ex) { ShowError("تعذّر فتح النص المحفوظ: " + ex.Message); }
    }
}
