using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace CallRecorder;

public sealed class GermanArabicTranslator : IDisposable
{
    private static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoutCallRecorder", "models", "translation-de-ar-v1.2");
    private static readonly SemaphoreSlim DownloadGate = new(1, 1);
    private static readonly TranslationManifest Manifest = LoadManifest();
    private readonly InferenceSession encoder;
    private readonly InferenceSession decoder;
    private readonly SentencePieceTokenizer tokenizer;
    private readonly Dictionary<string, int> vocabulary;
    private readonly Dictionary<int, string> pieces;
    private readonly long startId, eosId, unknownId;
    public static bool HasModel => Manifest.Assets.All(a => File.Exists(Path.Combine(Root, a.Name)) && new FileInfo(Path.Combine(Root, a.Name)).Length == a.Size);

    private static TranslationManifest LoadManifest()
    {
        using var stream = typeof(GermanArabicTranslator).Assembly.GetManifestResourceStream("TranslationAssets.json")!;
        return JsonSerializer.Deserialize<TranslationManifest>(stream)!;
    }

    public static async Task EnsureModelAsync(IProgress<TranscriptionProgress>? progress, CancellationToken token)
    {
        await DownloadGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Root);
            long total = Manifest.Assets.Sum(a => a.Size), done = 0;
            foreach (var asset in Manifest.Assets)
            {
                string path = Path.Combine(Root, asset.Name);
                progress?.Report(new("جارٍ التحقق من نموذج الترجمة الألمانية…", done * 100d / total));
                if (File.Exists(path) && new FileInfo(path).Length == asset.Size && await ValidHashAsync(path, asset.Sha256, token).ConfigureAwait(false))
                {
                    done += asset.Size;
                    continue;
                }
                if (new DriveInfo(Path.GetPathRoot(Root)!).AvailableFreeSpace < total + 100L * 1024 * 1024)
                    throw new IOException("يلزم نحو 250 ميغابايت متاحة لتجهيز نموذج الترجمة المحلي.");
                string temp = path + "." + Guid.NewGuid().ToString("N") + ".download";
                try
                {
                    await using var input = typeof(GermanArabicTranslator).Assembly.GetManifestResourceStream("TranslationModel/" + asset.Name)
                        ?? throw new InvalidDataException("نموذج الترجمة المضمن غير موجود. استخدم النسخة الكاملة من البرنامج.");
                    progress?.Report(new("جارٍ تجهيز نموذج الترجمة المحلي…", done * 100d / total));
                    await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
                        await input.CopyToAsync(output, token).ConfigureAwait(false);
                    if (!await ValidHashAsync(temp, asset.Sha256, token).ConfigureAwait(false)) throw new InvalidDataException("فشل التحقق من سلامة نموذج الترجمة.");
                    File.Move(temp, path, true);
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
                done += asset.Size;
            }
            progress?.Report(new("نموذج الترجمة الألمانية إلى العربية جاهز.", 100));
        }
        finally { DownloadGate.Release(); }
    }

    private static async Task<bool> ValidHashAsync(string path, string expected, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)).Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<GermanArabicTranslator> CreateAsync(IProgress<TranscriptionProgress>? progress, CancellationToken token)
    {
        await EnsureModelAsync(progress, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return new GermanArabicTranslator();
    }

    private GermanArabicTranslator()
    {
        vocabulary = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(Path.Combine(Root, "vocab.json")))!;
        pieces = vocabulary.ToDictionary(kv => kv.Value, kv => kv.Key);
        using var spm = File.OpenRead(Path.Combine(Root, "source.spm"));
        tokenizer = SentencePieceTokenizer.Create(spm, false, false);
        using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "config.json")));
        startId = config.RootElement.GetProperty("decoder_start_token_id").GetInt64();
        eosId = config.RootElement.GetProperty("eos_token_id").GetInt64();
        unknownId = vocabulary["<unk>"];
        using var options = new SessionOptions
        {
            IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
            InterOpNumThreads = 1, GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
        };
        encoder = new InferenceSession(Path.Combine(Root, "encoder_model_quantized.onnx"), options);
        try { decoder = new InferenceSession(Path.Combine(Root, "decoder_model_quantized.onnx"), options); }
        catch { encoder.Dispose(); throw; }
    }

    public string Translate(string german, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(german)) return "";
        var sourceIds = EncodeGerman(german);
        if (sourceIds.Count > 500) throw new InvalidOperationException("المقطع أطول من الحد الذي يدعمه نموذج الترجمة.");
        var mask = new DenseTensor<long>(Enumerable.Repeat(1L, sourceIds.Count).ToArray(), new[] { 1, sourceIds.Count });
        using var runOptions = new RunOptions();
        using var registration = token.Register(() => runOptions.Terminate = true);
        try
        {
            using var encoded = encoder.Run(new[]
            {
                NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(sourceIds.ToArray(), new[] {1, sourceIds.Count})),
                NamedOnnxValue.CreateFromTensor("attention_mask", mask)
            }, encoder.OutputMetadata.Keys.ToArray(), runOptions);
            var hidden = encoded.First().AsTensor<float>();
            var generated = new List<long> { startId };
            var text = new StringBuilder();
            for (int step = 0; step < 192; step++)
            {
                token.ThrowIfCancellationRequested();
                using var output = decoder.Run(new[]
                {
                    NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(generated.ToArray(), new[] {1, generated.Count})),
                    NamedOnnxValue.CreateFromTensor("encoder_attention_mask", mask),
                    NamedOnnxValue.CreateFromTensor("encoder_hidden_states", hidden)
                }, new[] { "logits" }, runOptions);
                var logits = output.First().AsTensor<float>();
                int vocabularySize = logits.Dimensions[2], position = logits.Dimensions[1] - 1;
                int next = 0;
                float best = float.NegativeInfinity;
                for (int id = 0; id < vocabularySize; id++)
                {
                    if (id == startId) continue;
                    float score = logits[0, position, id];
                    if (score > best) { best = score; next = id; }
                }
                if (next == eosId) return text.ToString().Replace('▁', ' ').Trim();
                generated.Add(next);
                text.Append(pieces.TryGetValue(next, out string? piece) ? piece : "�");
            }
            throw new InvalidOperationException("لم تكتمل ترجمة هذا المقطع ضمن الحد المسموح. النص الألماني محفوظ.");
        }
        catch (OnnxRuntimeException) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
    }

    public List<long> EncodeGerman(string german)
    {
        var sourceIds = new List<long>();
        if (vocabulary.TryGetValue(">>ara<<", out int arabicTag)) sourceIds.Add(arabicTag);
        foreach (var piece in tokenizer.EncodeToTokens(german, out _))
            sourceIds.Add(vocabulary.TryGetValue(piece.Value, out int id) ? id : unknownId);
        sourceIds.Add(eosId);
        return sourceIds;
    }

    public void Dispose() { decoder.Dispose(); encoder.Dispose(); }
    private sealed record TranslationManifest(string Repository, string Revision, TranslationAsset[] Assets);
    private sealed record TranslationAsset(string Name, long Size, string Sha256);
}
