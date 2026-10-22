using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Channels;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Whisper.net;

namespace CallRecorder;

public sealed record SpeechClip(float[] Samples, TimeSpan Start);
public sealed record LiveLine(string Time, string German, string Arabic, bool TranslationFailed = false);
public sealed record LiveUpdate(string Status, LiveLine? Line = null, double? Percent = null);
public sealed record LiveResult(string LogPath, int Lines, Exception? Error);

// Segment 16 kHz mono audio at short pauses, keeping a small lead-in for initial consonants.
public sealed class SpeechSegmenter
{
    private readonly Queue<float[]> leadIn = new();
    private readonly List<float> utterance = new(16000 * 8);
    private long seen, start;
    private int voicedSamples, silentSamples;
    public const int FrameSamples = 320;
    public const int MaximumSamples = 16000 * 8;

    public SpeechClip? Push(float[] frame)
    {
        if (frame.Length != FrameSamples) throw new ArgumentException("Expected a 20 ms frame.");
        long frameStart = seen;
        seen += frame.Length;
        double energy = 0;
        foreach (float value in frame) if (float.IsFinite(value)) energy += value * value;
        bool voice = Math.Sqrt(energy / frame.Length) >= .003;
        if (utterance.Count == 0)
        {
            if (!voice)
            {
                leadIn.Enqueue((float[])frame.Clone());
                while (leadIn.Count > 10) leadIn.Dequeue();
                return null;
            }
            start = Math.Max(0, frameStart - leadIn.Count * FrameSamples);
            while (leadIn.TryDequeue(out var previous)) utterance.AddRange(previous);
        }
        utterance.AddRange(frame);
        if (voice) { voicedSamples += frame.Length; silentSamples = 0; }
        else silentSamples += frame.Length;
        return silentSamples >= 9600 || utterance.Count >= MaximumSamples ? Flush() : null;
    }

    public SpeechClip? Flush()
    {
        SpeechClip? clip = null;
        if (voicedSamples >= 3200)
        {
            int length = utterance.Count - Math.Max(0, silentSamples - 1600);
            clip = new(utterance.Take(length).ToArray(), TimeSpan.FromSeconds(start / 16000d));
        }
        utterance.Clear();
        voicedSamples = silentSamples = 0;
        leadIn.Clear();
        return clip;
    }
}

public sealed class LiveTranslationSession
{
    private readonly IWaveIn capture;
    private readonly BufferedWaveProvider buffer;
    private readonly ISampleProvider samples;
    private readonly WhisperFactory factory;
    private readonly WhisperProcessor processor;
    private readonly GermanArabicTranslator translator;
    private readonly IProgress<LiveUpdate>? progress;
    private readonly CancellationTokenSource abort;
    private readonly Channel<SpeechClip> clips = Channel.CreateBounded<SpeechClip>(new BoundedChannelOptions(6)
    { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly string logPath;
    private int stopRequested;
    private float peak;
    private Exception? failure;
    public Task<LiveResult> Completion { get; private set; } = null!;
    public float Peak => Volatile.Read(ref peak);
    public string LogPath => logPath;

    private LiveTranslationSession(IWaveIn capture, WhisperFactory factory, WhisperProcessor processor,
        GermanArabicTranslator translator, string logPath, IProgress<LiveUpdate>? progress, CancellationToken token)
    {
        this.capture = capture;
        this.factory = factory;
        this.processor = processor;
        this.translator = translator;
        this.logPath = logPath;
        this.progress = progress;
        abort = CancellationTokenSource.CreateLinkedTokenSource(token);
        buffer = new BufferedWaveProvider(capture.WaveFormat)
        { ReadFully = true, BufferDuration = TimeSpan.FromSeconds(10), DiscardOnBufferOverflow = false };
        ISampleProvider provider = buffer.ToSampleProvider();
        if (provider.WaveFormat.Channels != 1) provider = new MonoSampleProvider(provider);
        samples = provider.WaveFormat.SampleRate == 16000 ? provider : new WdlResamplingSampleProvider(provider, 16000);
        capture.DataAvailable += (_, args) =>
        {
            try { buffer.AddSamples(args.Buffer, 0, args.BytesRecorded); }
            catch (Exception ex) { Fail(ex); }
        };
        capture.RecordingStopped += (_, args) =>
        {
            if (args.Exception is not null) Fail(args.Exception);
            else if (Volatile.Read(ref stopRequested) == 0 && !abort.IsCancellationRequested)
                Fail(new IOException("توقف جهاز الصوت. تحقق من توصيله ثم أعد تشغيل الترجمة المباشرة."));
        };
    }

    public static async Task<LiveTranslationSession> StartAsync(Func<IWaveIn> captureFactory, string outputDirectory,
        IProgress<LiveUpdate>? progress, CancellationToken token)
    {
        var setup = new BridgeProgress(progress);
        await TranscriptionService.EnsureModelAsync(setup, token).ConfigureAwait(false);
        await TranscriptionService.PrepareRuntimeAsync(token).ConfigureAwait(false);
        await GermanArabicTranslator.EnsureModelAsync(setup, token).ConfigureAwait(false);
        progress?.Report(new("جارٍ تحميل محرك الاستماع والترجمة…"));
        WhisperFactory? factory = null;
        WhisperProcessor? processor = null;
        GermanArabicTranslator? translator = null;
        IWaveIn? capture = null;
        try
        {
            factory = WhisperFactory.FromPath(TranscriptionService.ModelPath);
            processor = factory.CreateBuilder().WithLanguage("de").WithNoContext().WithTemperature(0)
                .WithNoSpeechThreshold(.6f).WithThreads(Math.Clamp(Environment.ProcessorCount / 2, 1, 6)).Build();
            translator = await GermanArabicTranslator.CreateAsync(null, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(outputDirectory);
            string log = Path.Combine(outputDirectory, $"Live_DE_AR_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_{Guid.NewGuid().ToString("N")[..6]}.txt");
            capture = captureFactory();
            var session = new LiveTranslationSession(capture, factory, processor, translator, log, progress, token);
            session.Completion = Task.Run(session.RunAsync);
            return session;
        }
        catch
        {
            capture?.Dispose();
            if (processor is not null) await processor.DisposeAsync().ConfigureAwait(false);
            factory?.Dispose();
            translator?.Dispose();
            throw;
        }
    }

    public void RequestStop() => Interlocked.Exchange(ref stopRequested, 1);
    public void Cancel() { try { abort.Cancel(); } catch (ObjectDisposedException) { } }
    private void Fail(Exception ex)
    {
        Interlocked.CompareExchange(ref failure, ex, null);
        abort.Cancel();
    }

    private async Task<LiveResult> RunAsync()
    {
        int lines = 0;
        Task? producer = null;
        try
        {
            await using var log = new StreamWriter(new FileStream(logPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(true));
            await log.WriteLineAsync("الترجمة المباشرة من الألمانية إلى العربية — " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            await log.FlushAsync();
            capture.StartRecording();
            progress?.Report(new("الاستماع يعمل — تكلّم بالألمانية أو شغّل صوت المكالمة."));
            producer = ProduceAsync();
            await foreach (var clip in clips.Reader.ReadAllAsync(abort.Token).ConfigureAwait(false))
            {
                progress?.Report(new("جارٍ التعرف على الكلام الألماني…"));
                var german = new StringBuilder();
                await foreach (var segment in processor.ProcessAsync(clip.Samples, abort.Token).ConfigureAwait(false))
                    if (!string.IsNullOrWhiteSpace(segment.Text)) german.Append(segment.Text.Trim()).Append(' ');
                string original = german.ToString().Trim();
                if (original.Length == 0) continue;
                string time = clip.Start.ToString(@"hh\:mm\:ss");
                // Persist the original before attempting translation, including during cancellation.
                await log.WriteLineAsync($"[{time}] DE: {original}");
                await log.FlushAsync();
                progress?.Report(new("جارٍ ترجمة الكلام إلى العربية…", new LiveLine(time, original, "جارٍ الترجمة…")));
                string arabic;
                bool failed = false;
                try { arabic = translator.Translate(original, abort.Token); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { arabic = "تعذّرت ترجمة هذا المقطع: " + ex.Message; failed = true; }
                await log.WriteLineAsync($"[{time}] AR: {arabic}\n");
                await log.FlushAsync();
                lines++;
                progress?.Report(new(Volatile.Read(ref stopRequested) == 0 ? "الاستماع مستمر — الترجمة تظهر بعد كل مقطع كلام." : "جارٍ إكمال المقاطع المتبقية…", new LiveLine(time, original, arabic, failed)));
            }
            if (producer is not null) await producer.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (abort.IsCancellationRequested) { }
        catch (Exception ex) { Interlocked.CompareExchange(ref failure, ex, null); }
        finally
        {
            Interlocked.Exchange(ref stopRequested, 1);
            abort.Cancel();
            if (producer is not null) try { await producer.ConfigureAwait(false); } catch { }
            try { capture.Dispose(); } catch (Exception ex) { Interlocked.CompareExchange(ref failure, ex, null); }
            try { await processor.DisposeAsync().ConfigureAwait(false); } catch (Exception ex) { Interlocked.CompareExchange(ref failure, ex, null); }
            try { factory.Dispose(); } catch (Exception ex) { Interlocked.CompareExchange(ref failure, ex, null); }
            try { translator.Dispose(); } catch (Exception ex) { Interlocked.CompareExchange(ref failure, ex, null); }
            abort.Dispose();
            Volatile.Write(ref peak, 0);
        }
        return new(logPath, lines, failure);
    }

    private async Task ProduceAsync()
    {
        var segmenter = new SpeechSegmenter();
        var clock = Stopwatch.StartNew();
        long readSamples = 0;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
            while (Volatile.Read(ref stopRequested) == 0)
            {
                await timer.WaitForNextTickAsync(abort.Token).ConfigureAwait(false);
                long target = (long)(Math.Max(0, clock.Elapsed.TotalSeconds - .2) * 16000);
                ReadUntil(target);
            }
            clock.Stop();
            capture.StopRecording();
            ReadUntil((long)(clock.Elapsed.TotalSeconds * 16000));
            Queue(segmenter.Flush());
        }
        catch (OperationCanceledException) when (abort.IsCancellationRequested) { }
        catch (Exception ex) { Fail(ex); }
        finally { clips.Writer.TryComplete(); }

        void ReadUntil(long target)
        {
            while (readSamples + SpeechSegmenter.FrameSamples <= target)
            {
                abort.Token.ThrowIfCancellationRequested();
                var frame = new float[SpeechSegmenter.FrameSamples];
                samples.Read(frame, 0, frame.Length);
                float max = 0;
                foreach (float value in frame) max = Math.Max(max, Math.Abs(value));
                Volatile.Write(ref peak, Math.Clamp(max, 0, 1));
                Queue(segmenter.Push(frame));
                readSamples += frame.Length;
            }
        }
        void Queue(SpeechClip? clip)
        {
            if (clip is not null && !clips.Writer.TryWrite(clip))
                throw new IOException("تعذّر مواكبة سرعة الكلام على هذا الجهاز. أُوقفت الترجمة؛ يمكنك تحويل التسجيل كاملًا بعد المكالمة.");
        }
    }

    private sealed class BridgeProgress(IProgress<LiveUpdate>? progress) : IProgress<TranscriptionProgress>
    {
        public void Report(TranscriptionProgress value) => progress?.Report(new(value.Status, Percent: value.Percent));
    }
}
