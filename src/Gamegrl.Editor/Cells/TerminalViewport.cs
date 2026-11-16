using AnsiDotNet;
using Gamegrl.Rhi;
using Rect = Gamegrl.UI.Rect;
namespace Gamegrl.Editor;

/// <summary>Two bounded GPU downloads; unfinished frames are skipped rather than waited on.</summary>
internal sealed class TerminalViewport : IDisposable
{
    private readonly GpuDevice _device;
    private GpuReadback[] _slots = [];
    private byte[] _pixels = [];
    private int _width, _height;
    private bool _hasImage;
    private int _read, _write;
    public TerminalViewport(GpuDevice device) => _device = device;
    public void Paint(CellDraw draw, Rect area, GpuTexture texture)
    {
        if (texture.Width != _width || texture.Height != _height)
        {
            Dispose(); _width = texture.Width; _height = texture.Height;
            _slots = Enumerable.Range(0, GpuDevice.FramesInFlight).Select(_ => _device.CreateReadback(_width, _height)).ToArray();
            _pixels = new byte[_width * _height * 4]; _hasImage = false; _read = _write = 0;
        }
        for (int count = 0; count < _slots.Length; count++)
        {
            if (!_slots[_read].TryRead(_pixels)) break;
            _hasImage = true; _read = (_read + 1) % _slots.Length;
        }
        if (_hasImage) PaintPixels(draw.Surface, area, _pixels, _width, _height, texture.Format is TextureFormat.Bgra8Srgb or TextureFormat.Bgra8Unorm, draw.Ascii);
    }
    public void Capture(CommandList command, GpuTexture texture)
    {
        if (_slots.Length == 0 || _slots[_write].Pending) return;
        command.CopyToReadback(texture, _slots[_write]); _write = (_write + 1) % _slots.Length;
    }
    internal static void PaintPixels(Surface surface, Rect area, ReadOnlySpan<byte> pixels, int width, int height, bool bgra, bool ascii = false)
    {
        var rect = CellDraw.To(area).Intersect(surface.Clip);
        for (int y = rect.Y; y < rect.Bottom; y++) for (int x = rect.X; x < rect.Right; x++)
        {
            int sx = Math.Clamp((int)((x - area.X) * width / Math.Max(1, area.W)), 0, width - 1);
            int sy = Math.Clamp((int)((y - area.Y) * height / Math.Max(1, area.H)), 0, height - 1);
            int top = (sy * width + sx) * 4, bottom = (Math.Min(height - 1, sy + 1) * width + sx) * 4;
            int r = bgra ? 2 : 0, b = bgra ? 0 : 2;
            if (ascii)
            {
                byte red = (byte)((pixels[top + r] + pixels[bottom + r]) / 2), green = (byte)((pixels[top + 1] + pixels[bottom + 1]) / 2), blue = (byte)((pixels[top + b] + pixels[bottom + b]) / 2);
                int level = (red * 54 + green * 183 + blue * 19) >> 8;
                surface[x, y] = new Cell(" .:-=+*#%@"[level * 9 / 255], new Style(Color.Rgb(red, green, blue), Color.Black));
                continue;
            }
            surface[x, y] = new Cell('▀', new Style(Color.Rgb(pixels[top + r], pixels[top + 1], pixels[top + b]), Color.Rgb(pixels[bottom + r], pixels[bottom + 1], pixels[bottom + b])));
        }
    }
    public void Dispose() { foreach (var slot in _slots) slot.Dispose(); _slots = []; }
}
