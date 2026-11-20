using System.Text;
using Gamegrl;

namespace Gamegrl.Audio.Devices;

public struct AlsaDeviceOptions
{
    /// <summary>An ALSA PCM name: "default" (through the desktop's sound server), "hw:1,0" or "plughw:1,0" (straight to a card).</summary>
    public string Device;
    public int SampleRate;
    public ChannelLayout Layout;
    public int PeriodFrames;
    public int Periods;
    /// <summary>Ask for SCHED_FIFO on the audio thread (needs rtprio permission; otherwise the thread runs at normal priority).</summary>
    public bool RealTime;

    public static AlsaDeviceOptions Default => new() { Device = "default", SampleRate = 48000, Layout = ChannelLayout.Stereo, PeriodFrames = 256, Periods = 3, RealTime = true };
}

/// <summary>
/// ALSA playback on its own thread: renders a period, converts it to the device's format and channel order, and writes
/// it (blocking until the device has room, which paces the thread). Underruns are recovered and counted.
/// </summary>
public sealed unsafe class AlsaDevice : IAudioDevice
{
    private nint _pcm;
    private readonly int _format, _deviceChannels;
    private readonly int[] _permutation;
    private Thread? _thread;
    private volatile bool _stopping;
    private AudioRenderCallback? _render;
    private long _latency;
    private int _underruns;

    public string Name { get; }
    public int SampleRate { get; }
    public ChannelLayout Layout { get; }
    public int PeriodFrames { get; }
    public int BufferFrames { get; }
    public long LatencyFrames => Volatile.Read(ref _latency);
    public int Underruns => Volatile.Read(ref _underruns);
    public bool RealTimeThread { get; private set; }
    private readonly bool _wantRealTime;

    public static bool IsAvailable => Alsa.Load();

    private AlsaDevice(string name, nint pcm, int rate, ChannelLayout layout, int period, int buffer, int format, int deviceChannels, int[] permutation, bool realTime)
    {
        Name = name; _pcm = pcm; SampleRate = rate; Layout = layout; PeriodFrames = period; BufferFrames = buffer;
        _format = format; _deviceChannels = deviceChannels; _permutation = permutation; _wantRealTime = realTime;
        _latency = buffer;
    }

    public static AlsaDevice Open(AlsaDeviceOptions options)
    {
        if (!Alsa.Load()) throw new AudioDeviceException("libasound.so.2 isn't available");
        options.Device ??= "default";
        options.Layout ??= ChannelLayout.Stereo;
        nint pcm;
        int err;
        fixed (byte* name = Encoding.UTF8.GetBytes(options.Device + "\0"))
            err = Alsa.Open(&pcm, name, Alsa.StreamPlayback, 0);
        if (err < 0) throw new AudioDeviceException($"ALSA can't open '{options.Device}': {Alsa.Error(err)}");

        nint hw = 0, sw = 0;
        try
        {
            Check(Alsa.HwParamsMalloc(&hw), "hw params");
            Check(Alsa.HwParamsAny(pcm, hw), "hw params");
            Check(Alsa.HwParamsSetAccess(pcm, hw, Alsa.AccessRwInterleaved), "interleaved access");
            int format = Alsa.HwParamsTestFormat(pcm, hw, Alsa.FormatFloatLe) == 0 ? Alsa.FormatFloatLe
                : Alsa.HwParamsTestFormat(pcm, hw, Alsa.FormatS32Le) == 0 ? Alsa.FormatS32Le : Alsa.FormatS16Le;
            Check(Alsa.HwParamsSetFormat(pcm, hw, format), "sample format");
            uint channels = (uint)options.Layout.Count;
            Check(Alsa.HwParamsSetChannelsNear(pcm, hw, &channels), "channel count");
            uint rate = (uint)options.SampleRate;
            int dir = 0;
            Check(Alsa.HwParamsSetRateNear(pcm, hw, &rate, &dir), "sample rate");
            nuint period = (nuint)options.PeriodFrames;
            Check(Alsa.HwParamsSetPeriodSizeNear(pcm, hw, &period, &dir), "period size");
            nuint buffer = period * (nuint)Math.Max(2, options.Periods);
            Check(Alsa.HwParamsSetBufferSizeNear(pcm, hw, &buffer), "buffer size");
            Check(Alsa.HwParams(pcm, hw), "applying hw params");
            Check(Alsa.HwParamsGetPeriodSize(hw, &period, &dir), "period size");
            Check(Alsa.HwParamsGetBufferSize(hw, &buffer), "buffer size");

            Check(Alsa.SwParamsMalloc(&sw), "sw params");
            Check(Alsa.SwParamsCurrent(pcm, sw), "sw params");
            Check(Alsa.SwParamsSetStartThreshold(pcm, sw, buffer - period), "start threshold");
            Check(Alsa.SwParamsSetAvailMin(pcm, sw, period), "avail min");
            Check(Alsa.SwParams(pcm, sw), "applying sw params");

            // The device's channel order; the engine renders in the layout of the speakers the device has.
            uint[] map = [];
            uint* chmap = Alsa.GetChmap(pcm);
            if (chmap != null)
            {
                if (chmap[0] == channels) { map = new uint[channels]; for (int i = 0; i < channels; i++) map[i] = chmap[1 + i] & 0xFFFF; }
                if (Alsa.Free != null) Alsa.Free(chmap);
            }
            if (map.Length != channels) map = Alsa.DefaultChmap((int)channels);
            ChannelLayout? layout = null;
            int[]? perm = null;
            if (map.Length == channels)
            {
                uint mask = 0;
                foreach (var p in map) if (Alsa.SpeakerOf(p) is { } s) mask |= 1u << (int)s;
                if (System.Numerics.BitOperations.PopCount(mask) == channels) { layout = ChannelLayout.FromMask(mask); perm = Alsa.Permutation(map, layout); }
            }
            if (layout is null || perm is null)
            {
                // No usable map: assume the channels come in our order.
                layout = channels == options.Layout.Count ? options.Layout : ChannelLayout.ForChannels((int)channels);
                perm = new int[channels];
                for (int i = 0; i < perm.Length; i++) perm[i] = i;
            }
            return new AlsaDevice(options.Device, pcm, (int)rate, layout, (int)period, (int)buffer, format, (int)channels, perm, options.RealTime);
        }
        catch
        {
            Alsa.Close(pcm);
            throw;
        }
        finally
        {
            if (hw != 0) Alsa.HwParamsFree(hw);
            if (sw != 0) Alsa.SwParamsFree(sw);
        }
    }

    private static void Check(int err, string what)
    {
        if (err < 0) throw new AudioDeviceException($"ALSA {what}: {Alsa.Error(err)}");
    }

    public void Start(AudioRenderCallback render)
    {
        if (_thread is not null) throw new InvalidOperationException("Already started");
        _render = render;
        _stopping = false;
        _thread = new Thread(Run) { Name = "gamegrl audio", IsBackground = true, Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    private void Run()
    {
        if (_wantRealTime && Alsa.SchedSetScheduler != null)
        {
            int priority = 70;
            RealTimeThread = Alsa.SchedSetScheduler(0, 1 /* SCHED_FIFO */, &priority) == 0;
        }
        int period = PeriodFrames, channels = Layout.Count, devChannels = _deviceChannels;
        var mix = new float[period * channels];
        var device = new byte[period * devChannels * (_format == Alsa.FormatS16Le ? 2 : 4)];
        var perm = _permutation;
        bool identity = _format == Alsa.FormatFloatLe && devChannels == channels;
        for (int i = 0; i < perm.Length && identity; i++) identity = perm[i] == i;

        Alsa.Prepare(_pcm);
        while (!_stopping)
        {
            _render!(mix, period);
            fixed (float* src = mix)
            fixed (byte* dst = device)
            {
                void* data = identity ? src : dst;
                if (!identity) Convert(src, dst, period, channels, devChannels, perm, _format);
                int written = 0;
                int frameBytes = devChannels * (_format == Alsa.FormatS16Le ? 2 : 4);
                while (written < period && !_stopping)
                {
                    nint r = Alsa.WriteI(_pcm, (byte*)data + written * frameBytes, (nuint)(period - written));
                    if (r >= 0) { written += (int)r; continue; }
                    int e = (int)r;
                    if (e == -Alsa.EPIPE) Interlocked.Increment(ref _underruns);
                    if (Alsa.Recover(_pcm, e, 1) < 0)
                    {
                        Log.Error($"Audio device '{Name}' failed: {Alsa.Error(e)}");
                        _stopping = true;
                    }
                }
            }
            nint delay;
            if (Alsa.Delay(_pcm, &delay) == 0) Volatile.Write(ref _latency, delay);
        }
    }

    /// <summary>Float layout order → the device's channel order and sample format.</summary>
    private static void Convert(float* src, byte* dst, int frames, int channels, int devChannels, int[] perm, int format)
    {
        for (int f = 0; f < frames; f++)
        {
            float* inFrame = src + f * channels;
            for (int d = 0; d < devChannels; d++)
            {
                int c = perm[d];
                float x = c >= 0 ? inFrame[c] : 0;
                int i = f * devChannels + d;
                switch (format)
                {
                    case Alsa.FormatFloatLe: ((float*)dst)[i] = x; break;
                    case Alsa.FormatS32Le: ((int*)dst)[i] = (int)Math.Clamp(x * 2147483648.0, int.MinValue, int.MaxValue); break;
                    default: ((short*)dst)[i] = (short)Math.Clamp(MathF.Round(x * 32767f), -32768, 32767); break;
                }
            }
        }
    }

    public void Stop()
    {
        if (_thread is null) return;
        _stopping = true;
        _thread.Join();
        _thread = null;
        Alsa.Drop(_pcm);
    }

    public void Dispose()
    {
        Stop();
        if (_pcm != 0) { Alsa.Close(_pcm); _pcm = 0; }
    }
}
