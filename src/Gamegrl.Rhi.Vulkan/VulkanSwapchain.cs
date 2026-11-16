using Gamegrl.Platform;

namespace Gamegrl.Rhi.Vulkan;

internal sealed unsafe class VulkanSwapchain : Swapchain
{
    private readonly VulkanDevice _device;
    private readonly IWindow _window;
    private readonly ulong _surface;
    private readonly bool _vsync;
    private bool _stale = true;

    public ulong Handle;
    public VulkanTexture[] Images = [];
    public ulong[] RenderFinished = [];

    public VulkanSwapchain(VulkanDevice device, IWindow window, ulong surface, bool vsync)
    {
        _device = device;
        _window = window;
        _surface = surface;
        _vsync = vsync;
        EnsureCurrent();
    }

    public void Invalidate() => _stale = true;

    /// <summary>Recreate if the window size changed or presentation reported out-of-date. False: nothing to draw into.</summary>
    public bool EnsureCurrent()
    {
        var (w, h) = _window.PixelSize;
        if (w <= 0 || h <= 0) return false;
        if (!_stale && w == Width && h == Height) return true;
        Recreate();
        return Width > 0 && Height > 0;
    }

    private void Recreate()
    {
        _device.WaitIdle();
        nint pd = _device.Physical;
        VkSurfaceCapabilitiesKHR caps;
        Vk.Check(Vk.GetPhysicalDeviceSurfaceCapabilitiesKHR(pd, _surface, &caps), "vkGetPhysicalDeviceSurfaceCapabilitiesKHR");

        uint count;
        Vk.GetPhysicalDeviceSurfaceFormatsKHR(pd, _surface, &count, null);
        var formats = stackalloc VkSurfaceFormatKHR[(int)count];
        Vk.GetPhysicalDeviceSurfaceFormatsKHR(pd, _surface, &count, formats);
        var chosen = formats[0];
        for (int i = 0; i < count; i++)
        {
            if ((formats[i].Format == VkConst.FormatB8G8R8A8Srgb || formats[i].Format == VkConst.FormatR8G8B8A8Srgb)
                && formats[i].ColorSpace == VkConst.ColorSpaceSrgbNonlinear)
            {
                chosen = formats[i];
                break;
            }
        }

        uint presentMode = VkConst.PresentModeFifo; // always available; vsync
        if (!_vsync)
        {
            Vk.GetPhysicalDeviceSurfacePresentModesKHR(pd, _surface, &count, null);
            var modes = stackalloc uint[(int)count];
            Vk.GetPhysicalDeviceSurfacePresentModesKHR(pd, _surface, &count, modes);
            for (int i = 0; i < count; i++)
                if (modes[i] == VkConst.PresentModeMailbox) presentMode = VkConst.PresentModeMailbox;
            if (presentMode == VkConst.PresentModeFifo)
                for (int i = 0; i < count; i++)
                    if (modes[i] == VkConst.PresentModeImmediate) presentMode = VkConst.PresentModeImmediate;
        }

        var (ww, wh) = _window.PixelSize;
        var extent = caps.CurrentExtent.Width != uint.MaxValue
            ? caps.CurrentExtent
            : new VkExtent2D
            {
                Width = Math.Clamp((uint)ww, caps.MinImageExtent.Width, caps.MaxImageExtent.Width),
                Height = Math.Clamp((uint)wh, caps.MinImageExtent.Height, caps.MaxImageExtent.Height),
            };
        if (extent.Width == 0 || extent.Height == 0)
        {
            Width = Height = 0;
            return;
        }

        uint imageCount = caps.MinImageCount + 1;
        if (caps.MaxImageCount > 0 && imageCount > caps.MaxImageCount) imageCount = caps.MaxImageCount;

        ulong old = Handle;
        var ci = new VkSwapchainCreateInfoKHR
        {
            SType = VkStructureType.SwapchainCreateInfoKhr,
            Surface = _surface,
            MinImageCount = imageCount,
            ImageFormat = chosen.Format,
            ImageColorSpace = chosen.ColorSpace,
            ImageExtent = extent,
            ImageArrayLayers = 1,
            ImageUsage = VkConst.ImageUsageColorAttachment | VkConst.ImageUsageTransferDst,
            ImageSharingMode = VkConst.SharingExclusive,
            PreTransform = caps.CurrentTransform,
            CompositeAlpha = VkConst.CompositeAlphaOpaque,
            PresentMode = presentMode,
            Clipped = 1,
            OldSwapchain = old,
        };
        ulong handle;
        Vk.Check(Vk.CreateSwapchainKHR(_device.Handle, &ci, null, &handle), "vkCreateSwapchainKHR");
        DestroyImages();
        if (old != 0) Vk.DestroySwapchainKHR(_device.Handle, old, null);
        Handle = handle;

        Vk.GetSwapchainImagesKHR(_device.Handle, handle, &count, null);
        var images = stackalloc ulong[(int)count];
        Vk.GetSwapchainImagesKHR(_device.Handle, handle, &count, images);
        var format = Formats.FromVk(chosen.Format);
        Images = new VulkanTexture[count];
        RenderFinished = new ulong[count];
        for (int i = 0; i < count; i++)
        {
            ulong view = _device.CreateView(images[i], chosen.Format, VkConst.AspectColor, 1);
            Images[i] = new VulkanTexture(null, images[i], 0, view, (int)extent.Width, (int)extent.Height, format, chosen.Format,
                TextureUsage.RenderTarget, "swapchain");
            RenderFinished[i] = _device.CreateSemaphore();
        }
        Format = format;
        Width = (int)extent.Width;
        Height = (int)extent.Height;
        _stale = false;
    }

    private void DestroyImages()
    {
        foreach (var img in Images) Vk.DestroyImageView(_device.Handle, img.View, null);
        foreach (var s in RenderFinished) Vk.DestroySemaphore(_device.Handle, s, null);
        Images = [];
        RenderFinished = [];
    }

    public override void Dispose()
    {
        if (Handle == 0) return;
        _device.WaitIdle();
        DestroyImages();
        Vk.DestroySwapchainKHR(_device.Handle, Handle, null);
        _device.DestroySurface(_surface);
        Handle = 0;
    }
}

internal static class Formats
{
    public static uint ToVk(TextureFormat f) => f switch
    {
        TextureFormat.Rgba8Unorm => VkConst.FormatR8G8B8A8Unorm,
        TextureFormat.Rgba8Srgb => VkConst.FormatR8G8B8A8Srgb,
        TextureFormat.Bgra8Unorm => VkConst.FormatB8G8R8A8Unorm,
        TextureFormat.Bgra8Srgb => VkConst.FormatB8G8R8A8Srgb,
        TextureFormat.Rgba16Float => VkConst.FormatR16G16B16A16Sfloat,
        TextureFormat.Depth32Float => VkConst.FormatD32Sfloat,
        _ => throw new ArgumentOutOfRangeException(nameof(f), f, null),
    };

    public static TextureFormat FromVk(uint f) => f switch
    {
        VkConst.FormatR8G8B8A8Unorm => TextureFormat.Rgba8Unorm,
        VkConst.FormatR8G8B8A8Srgb => TextureFormat.Rgba8Srgb,
        VkConst.FormatB8G8R8A8Unorm => TextureFormat.Bgra8Unorm,
        VkConst.FormatB8G8R8A8Srgb => TextureFormat.Bgra8Srgb,
        VkConst.FormatR16G16B16A16Sfloat => TextureFormat.Rgba16Float,
        VkConst.FormatD32Sfloat => TextureFormat.Depth32Float,
        _ => TextureFormat.Unknown,
    };

    public static uint Vertex(VertexFormat f) => f switch
    {
        VertexFormat.Float => VkConst.FormatR32Sfloat,
        VertexFormat.Float2 => VkConst.FormatR32G32Sfloat,
        VertexFormat.Float3 => VkConst.FormatR32G32B32Sfloat,
        VertexFormat.Float4 => VkConst.FormatR32G32B32A32Sfloat,
        VertexFormat.UByte4Norm => VkConst.FormatR8G8B8A8Unorm,
        _ => throw new ArgumentOutOfRangeException(nameof(f)),
    };
}
