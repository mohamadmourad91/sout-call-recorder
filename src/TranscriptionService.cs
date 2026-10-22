using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace CallRecorder;

public sealed record TranscriptionProgress(string Status, double Percent, string? Text = null);
public sealed record TranscriptResult(string TextPath, string SubtitlePath, int Segments);

public static class TranscriptionService
{
    public const long ModelSize = 574041195;
    private const string ModelHash = "394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2";
    private const string ModelUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo-q5_0.bin";
    private static readonly SemaphoreSlim ModelGate = new(1, 1);
    private static readonly SemaphoreSlim RuntimeGate = new(1, 1);
    private static readonly HttpClient Client = new() { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly string DataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoutCallRecorder");
    public static string ModelPath => Path.Combine(DataRoot, "models", "ggml-large-v3-turbo-q5_0.bin");
    public static bool HasModel => File.Exists(ModelPath) && new FileInfo(ModelPath).Length == ModelSize;

    public static async Task EnsureModelAsync(IProgress<TranscriptionProgress>? progress, CancellationToken token)
    {
        await ModelGate.WaitAsync(token).ConfigureAwait(false);
        string? temporary = null;
        try
        {
            if (HasModel)
            {
                progress?.Report(new("جارٍ التحقق من نموذج اللغة…", 0));
                if (await HasValidHashAsync(ModelPath, token).ConfigureAwait(false)) return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(ModelPath)!);
            if (new DriveInfo(Path.GetPathRoot(ModelPath)!).AvailableFreeSpace < ModelSize + 200L * 1024 * 1024)
                throw new IOException("لا توجد مساحة كافية لتنزيل نموذج اللغة. يلزم نحو 800 ميغابايت متاحة.");
            temporary = ModelPath + "." + Guid.NewGuid().ToString("N") + ".download";
            progress?.Report(new("جارٍ تنزيل نموذج اللغة لأول مرة (574 MB)…", 0));
            using var response = await Client.GetAsync(ModelUrl, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using (var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
            {
                byte[] buffer = new byte[131072];
                long received = 0;
                long reported = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    received += read;
                    if (received > ModelSize) throw new InvalidDataException("حجم نموذج اللغة غير متوقع.");
                    if (received - reported >= 1024 * 1024)
                    {
                        reported = received;
                        progress?.Report(new($"تنزيل نموذج اللغة: {received / 1000000} / 574 MB", received * 100d / ModelSize));
                    }
                }
                if (received != ModelSize) throw new InvalidDataException("لم يكتمل تنزيل نموذج اللغة. أعد المحاولة.");
            }
            progress?.Report(new("جارٍ التحقق من سلامة التنزيل…", 100));
            if (!await HasValidHashAsync(temporary, token).ConfigureAwait(false))
                throw new InvalidDataException("فشل التحقق من سلامة نموذج اللغة. أعد تنزيله.");
            File.Move(temporary, ModelPath, true);
            temporary = null;
        }
        finally
        {
            if (temporary is not null) try { File.Delete(temporary); } catch { }
            ModelGate.Release();
        }
    }

    private static async Task<bool> HasValidHashAsync(string file, CancellationToken token)
    {
        await using var stream = File.OpenRead(file);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false))
            .Equals(ModelHash, StringComparison.OrdinalIgnoreCase);
    }

    internal static async Task PrepareRuntimeAsync(CancellationToken token)
    {
        await RuntimeGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            // Embed native libraries so the EXE can be copied without a separate runtimes folder.
            string root = Path.Combine(DataRoot, "runtime", "1.9.1");
            var assembly = typeof(TranscriptionService).Assembly;
            foreach (string resource in assembly.GetManifestResourceNames())
            {
                bool cpu = resource.StartsWith("NativeCpu/", StringComparison.Ordinal);
                bool vulkan = resource.StartsWith("NativeVulkan/", StringComparison.Ordinal);
                if (!cpu && !vulkan) continue;
                token.ThrowIfCancellationRequested();
                string directory = cpu ? Path.Combine(root, "runtimes", "win-x64") : Path.Combine(root, "runtimes", "vulkan", "win-x64");
                Directory.CreateDirectory(directory);
                string target = Path.Combine(directory, resource[(resource.IndexOf('/') + 1)..]);
                await using var content = assembly.GetManifestResourceStream(resource)!;
                if (File.Exists(target) && new FileInfo(target).Length == content.Length) continue;
                string temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
                        await content.CopyToAsync(file, token).ConfigureAwait(false);
                    File.Move(temp, target, true);
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
            RuntimeOptions.LibraryPath = Path.Combine(root, "whisper.dll");
            RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu];
        }
        finally { RuntimeGate.Release(); }
    }

    public static async Task<TranscriptResult> TranscribeAsync(string audioPath, string language,
        IProgress<TranscriptionProgress>? progress, CancellationToken token)
    {
        if (!File.Exists(audioPath)) throw new FileNotFoundException("الملف الصوتي غير موجود.", audioPath);
        if (language is not ("ar" or "en" or "de" or "auto")) throw new ArgumentException("لغة غير معروفة.", nameof(language));
        await EnsureModelAsync(progress, token).ConfigureAwait(false);
        await PrepareRuntimeAsync(token).ConfigureAwait(false);
        progress?.Report(new("جارٍ تحميل محرك تحويل الصوت…", 0));
        // Decode and resample locally. The audio never enters the HTTP client.
        using var reader = new AudioFileReader(audioPath);
        if (reader.TotalTime <= TimeSpan.Zero) throw new InvalidDataException("الملف الصوتي فارغ.");
        using var factory = WhisperFactory.FromPath(ModelPath);
        await using var processor = factory.CreateBuilder().WithLanguage(language)
            .WithThreads(Math.Clamp(Environment.ProcessorCount / 2, 1, 8))
            .WithNoContext().WithTemperature(0).WithNoSpeechThreshold(.6f).Build();
        ISampleProvider samples = reader;
        if (samples.WaveFormat.Channels != 1) samples = new MonoSampleProvider(samples);
        if (samples.WaveFormat.SampleRate != 16000) samples = new WdlResamplingSampleProvider(samples, 16000);

        string prefix = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(audioPath))!,
            Path.GetFileNameWithoutExtension(audioPath) + "_text_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N")[..6]);
        string partialText = prefix + ".partial.txt", partialSrt = prefix + ".partial.srt";
        int segments = 0;
        bool complete = false;
        try
        {
            await using (var text = new StreamWriter(new FileStream(partialText, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(true)))
            await using (var subtitles = new StreamWriter(new FileStream(partialSrt, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(true)))
            {
                // Bound memory for long calls: process at most five minutes at a time.
                float[] chunk = new float[16000 * 60 * 5];
                long completedSamples = 0;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    int length = 0;
                    while (length < chunk.Length)
                    {
                        token.ThrowIfCancellationRequested();
                        int read = samples.Read(chunk, length, Math.Min(16000, chunk.Length - length));
                        if (read == 0) break;
                        length += read;
                    }
                    if (length == 0) break;
                    var offset = TimeSpan.FromSeconds(completedSamples / 16000d);
                    progress?.Report(new("جارٍ تحويل الصوت إلى نص…", Math.Min(99, offset.TotalSeconds / reader.TotalTime.TotalSeconds * 100)));
                    // Skip near-digital silence to avoid generating text from silent recordings.
                    if (HasAudibleSamples(chunk.AsSpan(0, length)))
                    {
                        await foreach (var segment in processor.ProcessAsync(chunk.AsMemory(0, length), token).ConfigureAwait(false))
                        {
                            string line = segment.Text.Trim();
                            if (line.Length == 0) continue;
                            segments++;
                            await text.WriteLineAsync(line.AsMemory(), token).ConfigureAwait(false);
                            await text.FlushAsync(token).ConfigureAwait(false);
                            await subtitles.WriteLineAsync($"{segments}\n{SrtTime(offset + segment.Start)} --> {SrtTime(offset + segment.End)}\n{line}\n".AsMemory(), token).ConfigureAwait(false);
                            await subtitles.FlushAsync(token).ConfigureAwait(false);
                            progress?.Report(new("جارٍ تحويل الصوت إلى نص…", Math.Min(99, (offset + segment.End).TotalSeconds / reader.TotalTime.TotalSeconds * 100), line));
                        }
                    }
                    completedSamples += length;
                }
                token.ThrowIfCancellationRequested();
            }
            File.Move(partialText, prefix + ".txt");
            File.Move(partialSrt, prefix + ".srt");
            complete = true;
            progress?.Report(new(segments == 0 ? "اكتمل التحويل؛ لم يُكتشف كلام واضح." : "اكتمل التحويل وحُفظ النص.", 100));
            return new(prefix + ".txt", prefix + ".srt", segments);
        }
        catch (OperationCanceledException)
        {
            progress?.Report(new("أُلغي التحويل. إذا استُخرج نص، حُفظ في ملف partial.txt بجانب التسجيل.", 0));
            throw;
        }
        finally
        {
            if (!complete && segments == 0)
            {
                try { File.Delete(partialText); } catch { }
                try { File.Delete(partialSrt); } catch { }
            }
        }
    }

    public static bool HasAudibleSamples(ReadOnlySpan<float> samples)
    {
        double energy = 0;
        foreach (float value in samples) if (float.IsFinite(value)) energy += value * value;
        return samples.Length > 0 && Math.Sqrt(energy / samples.Length) > .0001;
    }

    public static string SrtTime(TimeSpan time) => string.Format(CultureInfo.InvariantCulture,
        "{0:00}:{1:00}:{2:00},{3:000}", (long)time.TotalHours, time.Minutes, time.Seconds, time.Milliseconds);
}
