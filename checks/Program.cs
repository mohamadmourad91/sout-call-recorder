using System.Diagnostics;
using System.Text.Json;
using CallRecorder;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

string output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../test-output"));
Directory.CreateDirectory(output);
if (args.Length >= 2 && args[0] == "--translate")
{
    using var translator = await GermanArabicTranslator.CreateAsync(null, CancellationToken.None);
    Console.WriteLine("Input tokens: " + string.Join(",", translator.EncodeGerman(args[1])));
    var watch = Stopwatch.StartNew();
    string arabic = translator.Translate(args[1], CancellationToken.None);
    Console.WriteLine(arabic);
    Console.WriteLine($"Translation time: {watch.Elapsed.TotalSeconds:F3}s");
    return;
}
if (args.Contains("--download-model"))
{
    int lastPercent = -10;
    var progress = new Progress<TranscriptionProgress>(p =>
    {
        if (p.Percent >= lastPercent + 10 || p.Percent >= 100)
        {
            lastPercent = (int)p.Percent;
            Console.WriteLine(p.Status);
        }
    });
    await TranscriptionService.EnsureModelAsync(progress, CancellationToken.None);
    Console.WriteLine("Model verified: " + TranscriptionService.ModelPath);
    return;
}
if (args.Length >= 3 && args[0] == "--transcribe")
{
    var progress = new Progress<TranscriptionProgress>(p => Console.WriteLine(p.Text ?? p.Status));
    var transcriptResult = await Task.Run(() => TranscriptionService.TranscribeAsync(Path.GetFullPath(args[1]), args[2], progress, CancellationToken.None));
    Console.WriteLine(JsonSerializer.Serialize(transcriptResult));
    Console.WriteLine("Loaded runtime: " + Whisper.net.LibraryLoader.RuntimeOptions.LoadedLibrary);
    return;
}
var passed = new List<string>();
if (args.Length >= 2 && args[0] == "--live-loopback")
{
    string id = AudioDevices.DefaultId(DataFlow.Render) ?? AudioDevices.List(DataFlow.Render).First().Id;
    using var enumerator = new MMDeviceEnumerator();
    using var device = enumerator.GetDevice(id);
    var liveEvents = new List<LiveLine>();
    var liveWatch = Stopwatch.StartNew();
    var liveProgress = new LiveInlineProgress(update =>
    {
        Console.WriteLine($"[{liveWatch.Elapsed.TotalSeconds:F2}s] {update.Status}");
        if (update.Line is not null) { liveEvents.Add(update.Line); Console.WriteLine(update.Line.German + " => " + update.Line.Arabic); }
    });
    var live = await Task.Run(() => LiveTranslationSession.StartAsync(() => AudioDevices.Open(id,true), output, liveProgress, CancellationToken.None));
    // Verify an independent recorder can use the same endpoint during live translation.
    string simultaneousAudio = Path.Combine(output,"simultaneous-"+Guid.NewGuid().ToString("N")+".wav");
    var simultaneous = RecordingEngine.Start(simultaneousAudio, null, AudioDevices.Open(id,true));
    using var audio = new AudioFileReader(Path.GetFullPath(args[1]));
    using var player = new WasapiOut(device, AudioClientShareMode.Shared,false,50);
    player.Init(audio);
    await Task.Delay(300);
    double playbackStarted = liveWatch.Elapsed.TotalSeconds;
    player.Play();
    await Task.Delay(audio.TotalTime + TimeSpan.FromSeconds(1));
    player.Stop();
    double playbackEnded = liveWatch.Elapsed.TotalSeconds;
    live.RequestStop();
    var liveResult = await live.Completion.WaitAsync(TimeSpan.FromSeconds(60));
    simultaneous.RequestStop();
    var simultaneousResult = await simultaneous.Completion.WaitAsync(TimeSpan.FromSeconds(10));
    if (liveResult.Error is not null) throw liveResult.Error;
    if (simultaneousResult.Error is not null) throw simultaneousResult.Error;
    if (liveResult.Lines == 0 || !liveEvents.Any(e => !e.TranslationFailed && e.Arabic!="جارٍ الترجمة…"))
        throw new Exception("Live loopback did not produce a translation");
    File.WriteAllText(Path.Combine(output,"live-loopback-verification.json"),JsonSerializer.Serialize(new {device=device.FriendlyName,playbackStarted,playbackEnded,finished=liveWatch.Elapsed.TotalSeconds,result=liveResult,recording=simultaneousResult,lines=liveEvents}, new JsonSerializerOptions {WriteIndented=true}));
    Console.WriteLine("PASS: Real WASAPI audio -> German text -> Arabic translation while recording the same output");
    return;
}
if (args.Length >= 1 && args[0] == "--live-lifecycle")
{
    var silentInput = new FakeCapture(16000,1,440,suppressAllData:true);
    var silentLive = await Task.Run(() => LiveTranslationSession.StartAsync(() => silentInput,output,null,CancellationToken.None));
    await Task.Delay(700);
    silentLive.RequestStop();
    var silentResult = await silentLive.Completion.WaitAsync(TimeSpan.FromSeconds(15));
    if(silentResult.Error is not null || silentResult.Lines!=0 || !silentInput.Disposed) throw new Exception("Silent live lifecycle failed");
    Console.WriteLine("PASS: Silent live input stops, saves an empty log and releases its device");
    var failedInput = new FakeCapture(16000,1,440,failAfterMs:200);
    var failedLive = await Task.Run(() => LiveTranslationSession.StartAsync(() => failedInput,output,null,CancellationToken.None));
    var failedResult = await failedLive.Completion.WaitAsync(TimeSpan.FromSeconds(15));
    if(failedResult.Error is null || !failedInput.Disposed) throw new Exception("Unplugged live input did not stop cleanly");
    Console.WriteLine("PASS: Live device failure stops and releases its device");
    var cancelInput = new FakeCapture(16000,1,440,suppressAllData:true);
    var cancelledLive = await Task.Run(() => LiveTranslationSession.StartAsync(() => cancelInput,output,null,CancellationToken.None));
    await Task.Delay(200);
    cancelledLive.Cancel();
    var cancelledResult = await cancelledLive.Completion.WaitAsync(TimeSpan.FromSeconds(15));
    if(cancelledResult.Error is not null || !cancelInput.Disposed) throw new Exception("Live cancellation did not release the device");
    Console.WriteLine("PASS: Live cancellation releases the capture, recognizer and translator");
    return;
}
if (args.Length >= 2 && args[0] == "--live-file")
{
    var events = new List<LiveLine>();
    var watch = Stopwatch.StartNew();
    var progress = new LiveInlineProgress(p =>
    {
        Console.WriteLine($"[{watch.Elapsed.TotalSeconds:F2}s] {p.Status}");
        if (p.Line is not null)
        {
            Console.WriteLine(p.Line.German + " => " + p.Line.Arabic);
            events.Add(p.Line);
        }
    });
    var replay = new ReplayCapture(Path.GetFullPath(args[1]));
    var live = await Task.Run(() => LiveTranslationSession.StartAsync(() => replay, output, progress, CancellationToken.None));
    await replay.Completed.WaitAsync(TimeSpan.FromSeconds(60));
    await Task.Delay(850);
    live.RequestStop();
    var liveResult = await live.Completion.WaitAsync(TimeSpan.FromSeconds(60));
    if (liveResult.Error is not null) throw liveResult.Error;
    if (liveResult.Lines == 0 || !events.Any(e => !e.TranslationFailed && e.Arabic != "جارٍ الترجمة…" && e.Arabic.Any(c => c is >= '\u0600' and <= '\u06ff')))
        throw new Exception("Live German-to-Arabic replay produced no Arabic translation");
    if (!replay.Disposed) throw new Exception("Replay capture was not released");
    File.WriteAllText(Path.Combine(output, "live-verification.json"), JsonSerializer.Serialize(new { elapsedSeconds=watch.Elapsed.TotalSeconds, result=liveResult, events }, new JsonSerializerOptions { WriteIndented=true }));
    Console.WriteLine("PASS: Real-time audio replay -> German recognition -> Arabic translation -> saved bilingual log");
    return;
}
if (args.Length >= 2 && args[0] == "--test-transcription-safety")
{
    string silentAudio = Path.Combine(output, "transcription-silence-" + Guid.NewGuid().ToString("N") + ".wav");
    using (var wave = new WaveFileWriter(silentAudio, new WaveFormat(48000, 16, 1)))
        wave.Write(new byte[48000 * 2 * 2], 0, 48000 * 2 * 2);
    var silentResult = await Task.Run(() => TranscriptionService.TranscribeAsync(silentAudio, "ar", null, CancellationToken.None));
    if (silentResult.Segments != 0 || File.ReadAllText(silentResult.TextPath).Trim().Length != 0)
        throw new Exception("Silent audio produced a transcript");
    Console.WriteLine("PASS: Silent audio produces an empty UTF-8 transcript and subtitle file");
    using var cancel = new CancellationTokenSource();
    bool reachedInference = false;
    var cancelProgress = new InlineProgress(p =>
    {
        if (!reachedInference && p.Status == "جارٍ تحويل الصوت إلى نص…")
        {
            reachedInference = true;
            cancel.CancelAfter(75);
        }
    });
    string sourceAudio = Path.GetFullPath(args[1]);
    byte[] originalAudio = File.ReadAllBytes(sourceAudio);
    var watch = Stopwatch.StartNew();
    try
    {
        await Task.Run(() => TranscriptionService.TranscribeAsync(sourceAudio, "en", cancelProgress, cancel.Token));
        throw new Exception("Cancellation did not interrupt transcription");
    }
    catch (OperationCanceledException)
    {
        if (!reachedInference || !File.ReadAllBytes(sourceAudio).SequenceEqual(originalAudio))
            throw new Exception("Cancellation changed the source file or never reached inference");
        Console.WriteLine($"PASS: Inference cancellation leaves source audio unchanged ({watch.Elapsed.TotalSeconds:F1}s including model load)");
    }
    return;
}
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAILED: " + name);
    passed.Add(name);
    Console.WriteLine("PASS: " + name);
}

byte[] pcm = new byte[10];
var segmenter = new SpeechSegmenter();
SpeechClip? speech = null;
for (int i=0; i<10; i++) segmenter.Push(new float[320]);
for (int i=0; i<50; i++) segmenter.Push(Enumerable.Repeat(.1f,320).ToArray());
for (int i=0; i<30; i++) speech = segmenter.Push(new float[320]) ?? speech;
Check(speech is not null && speech.Start == TimeSpan.Zero && speech.Samples.Length == 20800, "Live segmentation keeps lead-in and trims trailing silence");
segmenter = new SpeechSegmenter();
for (int i=0; i<500; i++) CheckSilent(segmenter.Push(new float[320]));
Check(segmenter.Flush() is null, "Live digital silence never creates a speech segment");
segmenter = new SpeechSegmenter();
for (int i=0; i<400; i++) speech = segmenter.Push(Enumerable.Repeat(.1f,320).ToArray()) ?? speech;
Check(speech!.Samples.Length <= SpeechSegmenter.MaximumSamples, "Continuous live speech is bounded to eight seconds");
static void CheckSilent(SpeechClip? clip) { if (clip is not null) throw new Exception("Silence created speech"); }
Check(TranscriptionService.SrtTime(TimeSpan.FromMilliseconds(3723004)) == "01:02:03,004", "Subtitle timestamps include hours and milliseconds");
Check(!TranscriptionService.HasAudibleSamples(new float[16000]) && TranscriptionService.HasAudibleSamples(new float[] { .1f, -.1f }), "Silent audio is distinguished from audible samples");
AudioMath.MixToPcm16(new float[] { .2f, 1, -1, float.NaN, 0 }, new float[] { .3f, 1, -1, 0, .5f }, pcm, 1, 1);
Check(Math.Abs(BitConverter.ToInt16(pcm, 0) - 16384) <= 1 && BitConverter.ToInt16(pcm, 2) == 32767
    && BitConverter.ToInt16(pcm, 4) == -32767 && BitConverter.ToInt16(pcm, 6) == 0, "Mix, clamp and reject non-finite samples");

var mono = new MonoSampleProvider(new ConstantChannels());
float[] downmixed = new float[3];
Check(mono.Read(downmixed, 1, 1) == 1 && Math.Abs(downmixed[1] - .35f) < .0001 && downmixed[0] == 0, "Six-channel downmix and destination offset");

string mixedPath = Path.Combine(output, "synthetic-" + Guid.NewGuid().ToString("N") + ".wav");
var mic = new FakeCapture(44100, 1, 440);
var system = new FakeCapture(48000, 2, 880);
var engine = RecordingEngine.Start(mixedPath, mic, system);
await Task.Delay(1600);
engine.RequestStop();
var result = await engine.Completion.WaitAsync(TimeSpan.FromSeconds(8));
Check(result.Error is null, "Concurrent microphone and system recording completes");
using (var reader = new AudioFileReader(mixedPath))
{
    Check(reader.WaveFormat.SampleRate == 48000 && reader.WaveFormat.Channels == 1 && reader.TotalTime.TotalSeconds is > 1.4 and < 2.2, "48 kHz mono WAV with elapsed duration");
    float[] samples = new float[(int)(reader.Length / sizeof(float))];
    int count = reader.Read(samples, 0, samples.Length);
    Check(Power(samples, count, 440) > .005 && Power(samples, count, 880) > .005, "Both voices survive mixing and 44.1-to-48 kHz resampling");
}
Check(mic.Disposed && system.Disposed, "Both capture devices are released after stop");

string silencePath = Path.Combine(output, "silence-" + Guid.NewGuid().ToString("N") + ".wav");
engine = RecordingEngine.Start(silencePath, null, new FakeCapture(48000, 2, 880, suppressAllData: true));
await Task.Delay(550);
engine.RequestStop();
result = await engine.Completion.WaitAsync(TimeSpan.FromSeconds(8));
using (var reader = new WaveFileReader(silencePath))
    Check(result.Error is null && reader.TotalTime.TotalSeconds is > .45 and < 1.2, "Silent loopback keeps its timeline without callbacks");

byte[] before = File.ReadAllBytes(mixedPath);
var collisionSource = new FakeCapture(48000, 1, 440);
try { RecordingEngine.Start(mixedPath, collisionSource, null); throw new Exception("Collision was accepted"); }
catch (IOException) { }
Check(File.ReadAllBytes(mixedPath).SequenceEqual(before) && collisionSource.Disposed, "Existing recordings are never overwritten");

string failurePath = Path.Combine(output, "failure-" + Guid.NewGuid().ToString("N") + ".wav");
var failureSource = new FakeCapture(48000, 1, 440, failAfterMs: 400);
engine = RecordingEngine.Start(failurePath, failureSource, null);
result = await engine.Completion.WaitAsync(TimeSpan.FromSeconds(8));
using (var reader = new WaveFileReader(failurePath))
    Check(result.Error is not null && reader.TotalTime.TotalSeconds > .25 && failureSource.Disposed, "Device failure stops and finalizes the partial WAV");

var microphones = AudioDevices.List(DataFlow.Capture);
var outputs = AudioDevices.List(DataFlow.Render);
Console.WriteLine($"Hardware inventory: {microphones.Count} microphone(s), {outputs.Count} output(s)");

if (args.Contains("--loopback"))
{
    string id = AudioDevices.DefaultId(DataFlow.Render) ?? outputs.First().Id;
    using var enumerator = new MMDeviceEnumerator();
    using var device = enumerator.GetDevice(id);
    string hardwarePath = Path.Combine(output, "loopback-" + Guid.NewGuid().ToString("N") + ".wav");
    engine = RecordingEngine.Start(hardwarePath, null, AudioDevices.Open(id, true));
    using (var player = new WasapiOut(device, AudioClientShareMode.Shared, false, 50))
    {
        player.Init(new SignalGenerator(48000, 2) { Frequency = 660, Gain = .12, Type = SignalGeneratorType.Sin });
        await Task.Delay(350);
        player.Play();
        await Task.Delay(900);
        player.Stop();
        await Task.Delay(450);
    }
    engine.RequestStop();
    result = await engine.Completion.WaitAsync(TimeSpan.FromSeconds(10));
    using var reader = new AudioFileReader(hardwarePath);
    float[] samples = new float[(int)(reader.Length / sizeof(float))];
    int count = reader.Read(samples, 0, samples.Length);
    double power = Power(samples, count, 660);
    Console.WriteLine($"Loopback device: {device.FriendlyName}; duration={reader.TotalTime.TotalSeconds:F3}s; tone amplitude={power:F6}");
    Check(result.Error is null && power > .001 && reader.TotalTime.TotalSeconds > 1.5, "Real WASAPI loopback captures a generated tone");
}

File.WriteAllText(Path.Combine(output, "verification.json"), JsonSerializer.Serialize(new
{
    verifiedAt = DateTimeOffset.Now, checks = passed, microphones = microphones.Count, outputs = outputs.Count,
    microphoneHardwareRecorded = false
}, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"All {passed.Count} checks passed. Report: {Path.Combine(output, "verification.json")}");

static double Power(float[] data, int count, double hz)
{
    double sin = 0, cos = 0;
    for (int i = 0; i < count; i++)
    {
        sin += data[i] * Math.Sin(2 * Math.PI * hz * i / 48000);
        cos += data[i] * Math.Cos(2 * Math.PI * hz * i / 48000);
    }
    return Math.Sqrt(sin * sin + cos * cos) * 2 / count;
}

sealed class ConstantChannels : ISampleProvider
{
    public WaveFormat WaveFormat => WaveFormat.CreateIeeeFloatWaveFormat(48000, 6);
    public int Read(float[] buffer, int offset, int count)
    {
        for (int i = 0; i < count; i++) buffer[offset + i] = (i % 6 + 1) * .1f;
        return count;
    }
}

sealed class InlineProgress(Action<TranscriptionProgress> action) : IProgress<TranscriptionProgress>
{
    public void Report(TranscriptionProgress value) => action(value);
}

sealed class FakeCapture(int rate, int channels, double frequency, bool suppressAllData = false, int failAfterMs = 0) : IWaveIn
{
    private CancellationTokenSource? stop;
    private Task? producer;
    public bool Disposed { get; private set; }
    public WaveFormat WaveFormat { get; set; } = WaveFormat.CreateIeeeFloatWaveFormat(rate, channels);
    public event EventHandler<WaveInEventArgs>? DataAvailable;
    public event EventHandler<StoppedEventArgs>? RecordingStopped;
    public void StartRecording()
    {
        stop = new CancellationTokenSource();
        producer = Task.Run(async () =>
        {
            var clock = Stopwatch.StartNew();
            long written = 0;
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    await Task.Delay(10, stop.Token);
                    if (failAfterMs > 0 && clock.ElapsedMilliseconds >= failAfterMs)
                    {
                        RecordingStopped?.Invoke(this, new StoppedEventArgs(new IOException("Simulated unplug")));
                        return;
                    }
                    long target = (long)(clock.Elapsed.TotalSeconds * rate);
                    int frames = (int)(target - written);
                    float[] values = new float[frames * channels];
                    for (int i = 0; i < frames; i++)
                        for (int c = 0; c < channels; c++)
                            values[i * channels + c] = .2f * (float)Math.Sin(2 * Math.PI * frequency * (written + i) / rate);
                    byte[] bytes = new byte[values.Length * sizeof(float)];
                    Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
                    if (!suppressAllData) DataAvailable?.Invoke(this, new WaveInEventArgs(bytes, bytes.Length));
                    written = target;
                }
            }
            catch (OperationCanceledException) { }
        });
    }
    public void StopRecording()
    {
        stop?.Cancel();
        producer?.GetAwaiter().GetResult();
        RecordingStopped?.Invoke(this, new StoppedEventArgs());
    }
    public void Dispose()
    {
        if (Disposed) return;
        Disposed = true;
        StopRecording();
        stop?.Dispose();
    }
}
