using System.Runtime.InteropServices;

namespace Gamegrl.Audio.Devices;

/// <summary>The slice of ALSA's PCM API we use (alsa/pcm.h), loaded from libasound.so.2 as function pointers.</summary>
internal static unsafe class Alsa
{
    public const int StreamPlayback = 0;
    public const int AccessRwInterleaved = 3;
    public const int FormatS16Le = 2, FormatS32Le = 10, FormatFloatLe = 14;
    public const int EPIPE = 32, ESTRPIPE = 86, EINTR = 4, EAGAIN = 11;

    // snd_pcm_chmap_position
    public const int ChmapMono = 2, ChmapFL = 3, ChmapFR = 4, ChmapRL = 5, ChmapRR = 6, ChmapFC = 7, ChmapLFE = 8, ChmapSL = 9, ChmapSR = 10,
        ChmapRC = 11, ChmapFLC = 12, ChmapFRC = 13, ChmapTC = 21, ChmapTFL = 22, ChmapTFR = 23, ChmapTFC = 24, ChmapTRL = 25, ChmapTRR = 26, ChmapTRC = 27;

    private static nint _lib, _libc;
    private static bool _tried;

    public static delegate* unmanaged<nint*, byte*, int, int, int> Open;
    public static delegate* unmanaged<nint, int> Close;
    public static delegate* unmanaged<nint*, int> HwParamsMalloc;
    public static delegate* unmanaged<nint, void> HwParamsFree;
    public static delegate* unmanaged<nint, nint, int> HwParamsAny;
    public static delegate* unmanaged<nint, nint, int, int> HwParamsSetAccess;
    public static delegate* unmanaged<nint, nint, int, int> HwParamsTestFormat;
    public static delegate* unmanaged<nint, nint, int, int> HwParamsSetFormat;
    public static delegate* unmanaged<nint, nint, uint*, int> HwParamsSetChannelsNear;
    public static delegate* unmanaged<nint, nint, uint*, int*, int> HwParamsSetRateNear;
    public static delegate* unmanaged<nint, nint, nuint*, int*, int> HwParamsSetPeriodSizeNear;
    public static delegate* unmanaged<nint, nint, nuint*, int> HwParamsSetBufferSizeNear;
    public static delegate* unmanaged<nint, nuint*, int*, int> HwParamsGetPeriodSize;
    public static delegate* unmanaged<nint, nuint*, int> HwParamsGetBufferSize;
    public static delegate* unmanaged<nint, nint, int> HwParams;
    public static delegate* unmanaged<nint*, int> SwParamsMalloc;
    public static delegate* unmanaged<nint, void> SwParamsFree;
    public static delegate* unmanaged<nint, nint, int> SwParamsCurrent;
    public static delegate* unmanaged<nint, nint, nuint, int> SwParamsSetStartThreshold;
    public static delegate* unmanaged<nint, nint, nuint, int> SwParamsSetAvailMin;
    public static delegate* unmanaged<nint, nint, int> SwParams;
    public static delegate* unmanaged<nint, int> Prepare;
    public static delegate* unmanaged<nint, void*, nuint, nint> WriteI;
    public static delegate* unmanaged<nint, int, int, int> Recover;
    public static delegate* unmanaged<nint, nint*, int> Delay;
    public static delegate* unmanaged<nint, int> Drop;
    public static delegate* unmanaged<nint, uint*> GetChmap;
    public static delegate* unmanaged<int, byte*> StrError;
    public static delegate* unmanaged<void*, void> Free;
    public static delegate* unmanaged<int, int, int*, int> SchedSetScheduler;

    /// <summary>Loads libasound once; false when it isn't installed.</summary>
    public static bool Load()
    {
        if (_tried) return _lib != 0;
        _tried = true;
        if (!OperatingSystem.IsLinux() || !NativeLibrary.TryLoad("libasound.so.2", out _lib)) return false;
        nint F(string name) => NativeLibrary.GetExport(_lib, name);
        Open = (delegate* unmanaged<nint*, byte*, int, int, int>)F("snd_pcm_open");
        Close = (delegate* unmanaged<nint, int>)F("snd_pcm_close");
        HwParamsMalloc = (delegate* unmanaged<nint*, int>)F("snd_pcm_hw_params_malloc");
        HwParamsFree = (delegate* unmanaged<nint, void>)F("snd_pcm_hw_params_free");
        HwParamsAny = (delegate* unmanaged<nint, nint, int>)F("snd_pcm_hw_params_any");
        HwParamsSetAccess = (delegate* unmanaged<nint, nint, int, int>)F("snd_pcm_hw_params_set_access");
        HwParamsTestFormat = (delegate* unmanaged<nint, nint, int, int>)F("snd_pcm_hw_params_test_format");
        HwParamsSetFormat = (delegate* unmanaged<nint, nint, int, int>)F("snd_pcm_hw_params_set_format");
        HwParamsSetChannelsNear = (delegate* unmanaged<nint, nint, uint*, int>)F("snd_pcm_hw_params_set_channels_near");
        HwParamsSetRateNear = (delegate* unmanaged<nint, nint, uint*, int*, int>)F("snd_pcm_hw_params_set_rate_near");
        HwParamsSetPeriodSizeNear = (delegate* unmanaged<nint, nint, nuint*, int*, int>)F("snd_pcm_hw_params_set_period_size_near");
        HwParamsSetBufferSizeNear = (delegate* unmanaged<nint, nint, nuint*, int>)F("snd_pcm_hw_params_set_buffer_size_near");
        HwParamsGetPeriodSize = (delegate* unmanaged<nint, nuint*, int*, int>)F("snd_pcm_hw_params_get_period_size");
        HwParamsGetBufferSize = (delegate* unmanaged<nint, nuint*, int>)F("snd_pcm_hw_params_get_buffer_size");
        HwParams = (delegate* unmanaged<nint, nint, int>)F("snd_pcm_hw_params");
        SwParamsMalloc = (delegate* unmanaged<nint*, int>)F("snd_pcm_sw_params_malloc");
        SwParamsFree = (delegate* unmanaged<nint, void>)F("snd_pcm_sw_params_free");
        SwParamsCurrent = (delegate* unmanaged<nint, nint, int>)F("snd_pcm_sw_params_current");
        SwParamsSetStartThreshold = (delegate* unmanaged<nint, nint, nuint, int>)F("snd_pcm_sw_params_set_start_threshold");
        SwParamsSetAvailMin = (delegate* unmanaged<nint, nint, nuint, int>)F("snd_pcm_sw_params_set_avail_min");
        SwParams = (delegate* unmanaged<nint, nint, int>)F("snd_pcm_sw_params");
        Prepare = (delegate* unmanaged<nint, int>)F("snd_pcm_prepare");
        WriteI = (delegate* unmanaged<nint, void*, nuint, nint>)F("snd_pcm_writei");
        Recover = (delegate* unmanaged<nint, int, int, int>)F("snd_pcm_recover");
        Delay = (delegate* unmanaged<nint, nint*, int>)F("snd_pcm_delay");
        Drop = (delegate* unmanaged<nint, int>)F("snd_pcm_drop");
        GetChmap = (delegate* unmanaged<nint, uint*>)F("snd_pcm_get_chmap");
        StrError = (delegate* unmanaged<int, byte*>)F("snd_strerror");
        if (NativeLibrary.TryLoad("libc.so.6", out _libc))
        {
            Free = (delegate* unmanaged<void*, void>)NativeLibrary.GetExport(_libc, "free");
            if (NativeLibrary.TryGetExport(_libc, "sched_setscheduler", out var sched)) SchedSetScheduler = (delegate* unmanaged<int, int, int*, int>)sched;
        }
        return true;
    }

    public static string Error(int code) => Marshal.PtrToStringUTF8((nint)StrError(code)) ?? $"error {code}";

    /// <summary>Our speaker for an ALSA channel position, or null for positions we don't carry.</summary>
    public static Speaker? SpeakerOf(uint position) => (int)position switch
    {
        ChmapMono or ChmapFC => Speaker.FrontCenter,
        ChmapFL => Speaker.FrontLeft, ChmapFR => Speaker.FrontRight,
        ChmapRL => Speaker.BackLeft, ChmapRR => Speaker.BackRight, ChmapRC => Speaker.BackCenter,
        ChmapLFE => Speaker.Lfe, ChmapSL => Speaker.SideLeft, ChmapSR => Speaker.SideRight,
        ChmapFLC => Speaker.FrontLeftCenter, ChmapFRC => Speaker.FrontRightCenter,
        ChmapTC => Speaker.TopCenter, ChmapTFL => Speaker.TopFrontLeft, ChmapTFR => Speaker.TopFrontRight, ChmapTFC => Speaker.TopFrontCenter,
        ChmapTRL => Speaker.TopBackLeft, ChmapTRR => Speaker.TopBackRight, ChmapTRC => Speaker.TopBackCenter,
        _ => null,
    };

    /// <summary>ALSA's standard channel order for a count, used when the device reports no channel map.</summary>
    public static uint[] DefaultChmap(int channels) => channels switch
    {
        1 => [ChmapMono],
        2 => [ChmapFL, ChmapFR],
        3 => [ChmapFL, ChmapFR, ChmapFC],
        4 => [ChmapFL, ChmapFR, ChmapRL, ChmapRR],
        5 => [ChmapFL, ChmapFR, ChmapRL, ChmapRR, ChmapFC],
        6 => [ChmapFL, ChmapFR, ChmapRL, ChmapRR, ChmapFC, ChmapLFE],
        8 => [ChmapFL, ChmapFR, ChmapRL, ChmapRR, ChmapFC, ChmapLFE, ChmapSL, ChmapSR],
        _ => [],
    };

    /// <summary>
    /// For each device channel, the layout channel feeding it (−1: silent). A layout speaker the device lacks is dropped;
    /// the engine renders in the layout built from the device's map, so that only happens for odd maps.
    /// </summary>
    public static int[] Permutation(ReadOnlySpan<uint> deviceMap, ChannelLayout layout)
    {
        var perm = new int[deviceMap.Length];
        for (int d = 0; d < deviceMap.Length; d++)
            perm[d] = SpeakerOf(deviceMap[d]) is { } s ? layout.IndexOf(s) : -1;
        return perm;
    }
}
