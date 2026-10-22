using System.Diagnostics;
using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace CallRecorder;

public sealed record AudioDevice(string Id, string Name);
public sealed record RecordingResult(string FilePath, TimeSpan Duration, Exception? Error);

public static class AudioDevices
{
    public static List<AudioDevice> List(DataFlow flow)
    {
        using var enumerator = new MMDeviceEnumerator();
        var result = new List<AudioDevice>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            using (device) result.Add(new AudioDevice(device.ID, device.FriendlyName));
        }
        return result;
    }

    public static string? DefaultId(DataFlow flow)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(flow, Role.Communications);
            return device.ID;
        }
        catch { return null; }
    }

    public static IWaveIn Open(string id, bool loopback)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDevice(id);
        return loopback ? new WasapiLoopbackCapture(device) : new WasapiCapture(device, false, 40);
    }
}

// Average every channel, including devices with more than two channels.
public sealed class MonoSampleProvider(ISampleProvider source) : ISampleProvider
{
    private float[] input = [];
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);

    public int Read(float[] buffer, int offset, int count)
    {
        int channels = source.WaveFormat.Channels;
        int needed = checked(count * channels);
        if (input.Length < needed) input = new float[needed];
        int frames = source.Read(input, 0, needed) / channels;
        for (int frame = 0; frame < frames; frame++)
        {
            float sum = 0;
            for (int channel = 0; channel < channels; channel++) sum += input[frame * channels + channel];
            buffer[offset + frame] = sum / channels;
        }
        return frames;
    }
}

public static class AudioMath
{
    public static void MixToPcm16(ReadOnlySpan<float> mic, ReadOnlySpan<float> system,
        Span<byte> destination, float micGain, float systemGain)
    {
        for (int i = 0; i < mic.Length; i++)
        {
            float value = mic[i] * micGain + system[i] * systemGain;
            if (!float.IsFinite(value)) value = 0;
            short pcm = (short)Math.Round(Math.Clamp(value, -1f, 1f) * short.MaxValue);
            destination[i * 2] = (byte)(pcm & 0xff);
            destination[i * 2 + 1] = (byte)((pcm >> 8) & 0xff);
        }
    }
}

public sealed class RecordingEngine
{
    public const int SampleRate = 48000;
    private const int BlockFrames = 960;
    private const double BufferDelaySeconds = 0.25;
    private readonly Stopwatch clock = new();
    private readonly List<CaptureInput> inputs = [];
    private readonly string path;
    private readonly WaveFileWriter writer;
    private int stopRequested;
    private Exception? failure;
    private float micGain = 1, systemGain = 1, micPeak, systemPeak;

    public Task<RecordingResult> Completion { get; private set; } = null!;
    public TimeSpan Elapsed => clock.Elapsed;
    public float MicPeak => Volatile.Read(ref micPeak);
    public float SystemPeak => Volatile.Read(ref systemPeak);
    public float MicGain { get => Volatile.Read(ref micGain); set => Volatile.Write(ref micGain, value); }
    public float SystemGain { get => Volatile.Read(ref systemGain); set => Volatile.Write(ref systemGain, value); }

    private RecordingEngine(string path)
    {
        this.path = path;
        // Never overwrite an existing recording, even on a filename collision.
        writer = new WaveFileWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read),
            new WaveFormat(SampleRate, 16, 1));
    }

    // Ownership of both sources transfers to the engine, including on startup failure.
    public static RecordingEngine Start(string path, IWaveIn? microphone, IWaveIn? system)
    {
        RecordingEngine? engine = null;
        try
        {
            if (microphone is null && system is null) throw new InvalidOperationException("اختر مصدر صوت واحدًا على الأقل.");
            engine = new RecordingEngine(path);
            if (microphone is not null) engine.Add(microphone, true);
            if (system is not null) engine.Add(system, false);
            engine.clock.Start();
            foreach (var input in engine.inputs) input.Capture.StartRecording();
            engine.Completion = Task.Run(engine.PumpAsync);
            return engine;
        }
        catch
        {
            // Dispose all supplied sources, including a source whose format was rejected.
            foreach (var source in new[] { microphone, system }.Where(s => s is not null).Distinct())
                try { source!.Dispose(); } catch { }
            if (engine is not null)
            {
                engine.clock.Stop();
                try { engine.writer.Dispose(); } catch { }
                try { File.Delete(path); } catch { }
            }
            throw;
        }
    }

    private void Add(IWaveIn capture, bool isMic)
    {
        var buffer = new BufferedWaveProvider(capture.WaveFormat)
        {
            BufferDuration = TimeSpan.FromSeconds(10), ReadFully = true, DiscardOnBufferOverflow = false
        };
        ISampleProvider samples = buffer.ToSampleProvider();
        if (samples.WaveFormat.Channels != 1) samples = new MonoSampleProvider(samples);
        if (samples.WaveFormat.SampleRate != SampleRate) samples = new WdlResamplingSampleProvider(samples, SampleRate);
        var input = new CaptureInput(capture, samples, isMic);
        inputs.Add(input);
        capture.DataAvailable += (_, e) =>
        {
            try { buffer.AddSamples(e.Buffer, 0, e.BytesRecorded); }
            catch (Exception ex) { Fail(ex); }
        };
        capture.RecordingStopped += (_, e) =>
        {
            if (e.Exception is not null) Fail(e.Exception);
            else if (Volatile.Read(ref stopRequested) == 0)
                Fail(new IOException("توقف جهاز الصوت أثناء التسجيل. تحقق من توصيله ثم ابدأ تسجيلًا جديدًا."));
        };
    }

    private void Fail(Exception ex)
    {
        Interlocked.CompareExchange(ref failure, ex, null);
        RequestStop();
    }

    public void RequestStop() => Interlocked.Exchange(ref stopRequested, 1);

    private async Task<RecordingResult> PumpAsync()
    {
        var mic = new float[BlockFrames];
        var system = new float[BlockFrames];
        var pcm = new byte[BlockFrames * 2];
        long writtenFrames = 0;
        long lastFlush = 0;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
            while (Volatile.Read(ref stopRequested) == 0)
            {
                await timer.WaitForNextTickAsync().ConfigureAwait(false);
                WriteUntil((long)(Math.Max(0, clock.Elapsed.TotalSeconds - BufferDelaySeconds) * SampleRate));
                if (writtenFrames - lastFlush >= SampleRate)
                {
                    // Update the WAV header regularly so a partial recording remains recoverable.
                    writer.Flush();
                    lastFlush = writtenFrames;
                    var root = Path.GetPathRoot(Path.GetFullPath(path));
                    if (root is not null && new DriveInfo(root).AvailableFreeSpace < 20L * 1024 * 1024)
                        Fail(new IOException("أُوقف التسجيل لأن مساحة القرص أوشكت على النفاد."));
                    // Stay below the classic WAV 4 GiB limit.
                    if (clock.Elapsed >= TimeSpan.FromHours(10))
                        Fail(new IOException("تم حفظ التسجيل بعد بلوغ الحد الأقصى: عشر ساعات. يمكنك بدء تسجيل جديد."));
                }
            }
            clock.Stop();
            long finalFrame = (long)(clock.Elapsed.TotalSeconds * SampleRate);
            foreach (var input in inputs) input.Capture.StopRecording();
            WriteUntil(finalFrame);
        }
        catch (Exception ex) { Interlocked.CompareExchange(ref failure, ex, null); }
        finally
        {
            Interlocked.Exchange(ref stopRequested, 1);
            clock.Stop();
            foreach (var input in inputs)
            {
                try { input.Capture.Dispose(); }
                catch (Exception ex) { Interlocked.CompareExchange(ref failure, ex, null); }
            }
            try { writer.Dispose(); }
            catch (Exception ex) { Interlocked.CompareExchange(ref failure, ex, null); }
            Volatile.Write(ref micPeak, 0);
            Volatile.Write(ref systemPeak, 0);
        }
        return new RecordingResult(path, TimeSpan.FromSeconds((double)writtenFrames / SampleRate), failure);

        void WriteUntil(long targetFrames)
        {
            while (writtenFrames < targetFrames)
            {
                int count = (int)Math.Min(BlockFrames, targetFrames - writtenFrames);
                Array.Clear(mic);
                Array.Clear(system);
                foreach (var input in inputs)
                    input.Samples.Read(input.IsMic ? mic : system, 0, count);
                float m = MicGain, s = SystemGain;
                Volatile.Write(ref micPeak, Peak(mic, count, m));
                Volatile.Write(ref systemPeak, Peak(system, count, s));
                AudioMath.MixToPcm16(mic.AsSpan(0, count), system.AsSpan(0, count), pcm, m, s);
                writer.Write(pcm, 0, count * 2);
                writtenFrames += count;
            }
        }
    }

    private static float Peak(float[] samples, int count, float gain)
    {
        float peak = 0;
        for (int i = 0; i < count; i++) peak = Math.Max(peak, Math.Abs(samples[i] * gain));
        return Math.Clamp(peak, 0, 1);
    }

    private sealed record CaptureInput(IWaveIn Capture, ISampleProvider Samples, bool IsMic);
}
