using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using CallRecorder;

sealed class LiveInlineProgress(Action<LiveUpdate> report) : IProgress<LiveUpdate>
{
    public void Report(LiveUpdate value) => report(value);
}

sealed class ReplayCapture : IWaveIn
{
    private readonly AudioFileReader reader;
    private readonly ISampleProvider samples;
    private readonly CancellationTokenSource stop = new();
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? producer;
    public Task Completed => completion.Task;
    public bool Disposed { get; private set; }
    public WaveFormat WaveFormat { get; set; } = WaveFormat.CreateIeeeFloatWaveFormat(16000, 1);
    public event EventHandler<WaveInEventArgs>? DataAvailable;
    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    public ReplayCapture(string path)
    {
        reader = new AudioFileReader(path);
        ISampleProvider source = reader;
        if (source.WaveFormat.Channels != 1) source = new MonoSampleProvider(source);
        samples = source.WaveFormat.SampleRate == 16000 ? source : new WdlResamplingSampleProvider(source, 16000);
    }

    public void StartRecording()
    {
        producer = Task.Run(async () =>
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
                while (await timer.WaitForNextTickAsync(stop.Token))
                {
                    float[] frame = new float[320];
                    int count = samples.Read(frame, 0, frame.Length);
                    if (count == 0) { completion.TrySetResult(); return; }
                    byte[] bytes = new byte[count * sizeof(float)];
                    Buffer.BlockCopy(frame, 0, bytes, 0, bytes.Length);
                    DataAvailable?.Invoke(this, new WaveInEventArgs(bytes, bytes.Length));
                }
            }
            catch (OperationCanceledException) { completion.TrySetCanceled(); }
            catch (Exception ex) { completion.TrySetException(ex); RecordingStopped?.Invoke(this, new StoppedEventArgs(ex)); }
        });
    }
    public void StopRecording()
    {
        stop.Cancel();
        producer?.GetAwaiter().GetResult();
        RecordingStopped?.Invoke(this, new StoppedEventArgs());
    }
    public void Dispose()
    {
        if (Disposed) return;
        Disposed = true;
        StopRecording();
        reader.Dispose();
        stop.Dispose();
    }
}
