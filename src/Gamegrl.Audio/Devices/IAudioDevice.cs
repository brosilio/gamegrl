namespace Gamegrl.Audio.Devices;

/// <summary>Called on the device's audio thread for each period: fill <c>frames</c> interleaved frames in the device's layout.</summary>
public delegate void AudioRenderCallback(Span<float> interleaved, int frames);

/// <summary>
/// An output device with its own real-time thread. The engine renders in the device's rate and <see cref="Layout"/>
/// (channels in <see cref="Speaker"/> order); the backend converts the sample format and reorders channels for the
/// hardware.
/// </summary>
public interface IAudioDevice : IDisposable
{
    string Name { get; }
    int SampleRate { get; }
    ChannelLayout Layout { get; }
    /// <summary>Frames rendered per callback.</summary>
    int PeriodFrames { get; }
    /// <summary>The device's whole buffer, in frames.</summary>
    int BufferFrames { get; }
    /// <summary>The last measured delay between rendering a frame and hearing it.</summary>
    long LatencyFrames { get; }
    /// <summary>Times the device ran dry (the render callback was late).</summary>
    int Underruns { get; }
    void Start(AudioRenderCallback render);
    void Stop();
}

public sealed class AudioDeviceException(string message) : Exception(message);
