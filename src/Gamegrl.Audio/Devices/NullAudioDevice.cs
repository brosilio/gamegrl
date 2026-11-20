using System.Diagnostics;

namespace Gamegrl.Audio.Devices;

/// <summary>
/// A device with no hardware: renders in real time on its own thread and discards the result, so voices start, play and
/// end on schedule on servers, in CI and on machines without sound.
/// </summary>
public sealed class NullAudioDevice(int sampleRate = 48000, ChannelLayout? layout = null, int periodFrames = 256) : IAudioDevice
{
    private Thread? _thread;
    private volatile bool _stopping;

    public string Name => "null";
    public int SampleRate { get; } = sampleRate;
    public ChannelLayout Layout { get; } = layout ?? ChannelLayout.Stereo;
    public int PeriodFrames { get; } = periodFrames;
    public int BufferFrames => PeriodFrames;
    public long LatencyFrames => PeriodFrames;
    public int Underruns => 0;

    public void Start(AudioRenderCallback render)
    {
        if (_thread is not null) throw new InvalidOperationException("Already started");
        _stopping = false;
        _thread = new Thread(() =>
        {
            var buffer = new float[PeriodFrames * Layout.Count];
            long rendered = 0, start = Stopwatch.GetTimestamp();
            while (!_stopping)
            {
                // Keep one period ahead of the clock.
                long due = (long)(Stopwatch.GetElapsedTime(start).TotalSeconds * SampleRate) + PeriodFrames;
                while (rendered < due && !_stopping) { render(buffer, PeriodFrames); rendered += PeriodFrames; }
                Thread.Sleep(Math.Max(1, PeriodFrames * 1000 / SampleRate / 2));
            }
        }) { Name = "gamegrl audio (null)", IsBackground = true };
        _thread.Start();
    }

    public void Stop()
    {
        _stopping = true;
        _thread?.Join();
        _thread = null;
    }

    public void Dispose() => Stop();
}
