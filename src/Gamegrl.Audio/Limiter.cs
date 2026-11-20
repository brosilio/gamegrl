namespace Gamegrl.Audio;

/// <summary>
/// A lookahead peak limiter linked across channels: no sample leaves above the ceiling, and gain changes are smooth.
/// Required gain per frame (ceiling ÷ peak) goes through an instant-attack exponential release, a sliding minimum over
/// the lookahead window and then a moving average over the same window; the signal is delayed by the window less one
/// frame, so the averaged gain has fully arrived by the time the peak does (the sliding minimum covers it for the whole
/// average). Allocation-free after construction.
/// </summary>
public sealed class Limiter
{
    private readonly int _channels, _window;
    private readonly float[] _delay;      // interleaved ring of window frames
    private readonly float[] _minValues;  // monotonic deque for the sliding minimum: values and their frame numbers
    private readonly long[] _minFrames;
    private readonly float[] _boxRing;    // the last window minimums, for the moving average
    private int _dqHead, _dqCount, _delayPos, _boxPos;
    private double _boxSum;
    private long _frame;
    private float _release, _released = 1;

    public float Ceiling { get; set; }
    public int LatencyFrames => _window - 1;
    /// <summary>The gain applied to the last frame (1 = no reduction), for meters.</summary>
    public float CurrentGain { get; private set; } = 1;

    public Limiter(int channels, int sampleRate, float ceilingDb = -1, float lookaheadMs = 1.5f, float releaseMs = 80)
    {
        _channels = channels;
        _window = Math.Max(2, (int)(lookaheadMs * 0.001f * sampleRate));
        _delay = new float[_window * channels];
        _minValues = new float[_window];
        _minFrames = new long[_window];
        _boxRing = new float[_window];
        _boxRing.AsSpan().Fill(1);
        _boxSum = _window;
        Ceiling = MathF.Pow(10, ceilingDb / 20);
        _release = 1 - MathF.Exp(-1 / (releaseMs * 0.001f * sampleRate));
    }

    /// <summary>Limits planar channels in place (channel c's frames at <c>planar[c * stride ..]</c>).</summary>
    public void Process(Span<float> planar, int stride, int frames)
    {
        int ch = _channels, window = _window;
        float ceiling = Ceiling;
        for (int f = 0; f < frames; f++)
        {
            float peak = 0;
            for (int c = 0; c < ch; c++) peak = MathF.Max(peak, MathF.Abs(planar[c * stride + f]));
            float required = peak > ceiling ? ceiling / peak : 1;
            // Instant attack, exponential release.
            _released = required < _released ? required : _released + (required - _released) * _release;

            // Sliding minimum over the last `window` frames (monotonic deque, amortised O(1)).
            // Expire the oldest entry first, so the deque never holds more than `window` entries.
            long now = _frame++;
            if (_dqCount > 0 && _minFrames[_dqHead] <= now - window) { _dqHead = (_dqHead + 1) % window; _dqCount--; }
            while (_dqCount > 0 && _minValues[(_dqHead + _dqCount - 1) % window] >= _released) _dqCount--;
            int slot = (_dqHead + _dqCount) % window;
            _minValues[slot] = _released; _minFrames[slot] = now; _dqCount++;
            float held = _minValues[_dqHead];

            // Moving average of the held minimum.
            _boxSum += held - _boxRing[_boxPos];
            _boxRing[_boxPos] = held;
            _boxPos = _boxPos + 1 == window ? 0 : _boxPos + 1;
            float gain = (float)(_boxSum / window);
            if (gain > 1) gain = 1;

            // Delay the signal by window − 1 frames: write the newest frame, read the one written window − 1 frames ago.
            int writeAt = _delayPos * ch;
            int readFrame = _delayPos + 1 == window ? 0 : _delayPos + 1;
            int readAt = readFrame * ch;
            for (int c = 0; c < ch; c++)
            {
                float x = planar[c * stride + f];
                _delay[writeAt + c] = x;
                planar[c * stride + f] = _delay[readAt + c] * gain;
            }
            _delayPos = readFrame;
            CurrentGain = gain;
        }
    }
}
