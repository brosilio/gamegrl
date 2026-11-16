using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Gamegrl.Platform;

namespace Gamegrl.Rhi.Vulkan;

public sealed class VulkanOptions
{
    /// <summary>Enable VK_LAYER_KHRONOS_validation when it's installed (default: on in Debug builds).</summary>
    public bool Validation { get; init; }
#if DEBUG
        = true;
#endif
    /// <summary>Prefer a discrete GPU when several are present.</summary>
    public bool PreferDiscrete { get; init; } = true;
}

/// <summary>
/// The Vulkan 1.3 backend: dynamic rendering, synchronization2, one graphics+present queue.
/// TODO(milestone 2): a real memory sub-allocator (today: one VkDeviceMemory per resource).
/// </summary>
public sealed unsafe class VulkanDevice : GpuDevice
{
    private const string ValidationLayer = "VK_LAYER_KHRONOS_validation";

    private readonly nint _instance;
    private readonly ulong _messenger;
    private readonly nint _physical;
    private readonly nint _device;
    private readonly nint _queue;
    private readonly uint _queueFamily;
    private readonly VkPhysicalDeviceMemoryProperties _memory;
    private readonly IWindow? _mainWindow;
    private ulong _mainSurface;
    private readonly ulong _descriptorPool;
    private readonly ulong _uploadPool;
    private readonly ulong _uploadFence;

    private readonly PerFrame[] _frames = new PerFrame[FramesInFlight];
    private ulong _frameNumber;
    private VulkanSwapchain? _currentSwapchain;
    private uint _currentImage;
    private bool _offscreenFrame;
    private bool _disposed;

    private sealed class PerFrame
    {
        public ulong Pool;
        public nint Cmd;
        public ulong Fence;
        public ulong ImageAcquired;
        public ulong? Submitted;
        public readonly List<Deferred> Deletes = new();
        public readonly VulkanCommandList List = new();
    }

    public VulkanDevice(IWindow? mainWindow, VulkanOptions? options = null)
    {
        options ??= new VulkanOptions();
        _mainWindow = mainWindow;
        Vk.LoadLoader();

        // Instance.
        var extensions = new List<string>(mainWindow?.VulkanInstanceExtensions ?? []);
        var layers = new List<string>();
        bool debugUtils = false;
        if (options.Validation && HasInstanceLayer(ValidationLayer))
        {
            layers.Add(ValidationLayer);
            if (HasInstanceExtension("VK_EXT_debug_utils"))
            {
                extensions.Add("VK_EXT_debug_utils");
                debugUtils = true;
            }
        }
        ValidationEnabled = layers.Count > 0;

        using (var appName = new Utf8Strings(["gamegrl"]))
        using (var ext = new Utf8Strings(extensions))
        using (var lay = new Utf8Strings(layers))
        {
            var app = new VkApplicationInfo
            {
                SType = VkStructureType.ApplicationInfo,
                ApplicationName = appName.Pointers[0],
                EngineName = appName.Pointers[0],
                ApiVersion = VkConst.ApiVersion13,
            };
            var ci = new VkInstanceCreateInfo
            {
                SType = VkStructureType.InstanceCreateInfo,
                ApplicationInfo = &app,
                EnabledExtensionCount = (uint)ext.Count,
                EnabledExtensionNames = ext.Pointers,
                EnabledLayerCount = (uint)lay.Count,
                EnabledLayerNames = lay.Pointers,
            };
            nint instance;
            Vk.Check(Vk.CreateInstance(&ci, null, &instance), "vkCreateInstance");
            _instance = instance;
        }
        Vk.LoadInstance(_instance);

        if (debugUtils && Vk.CreateDebugUtilsMessengerEXT != null)
        {
            var mci = new VkDebugUtilsMessengerCreateInfoEXT
            {
                SType = VkStructureType.DebugUtilsMessengerCreateInfoExt,
                MessageSeverity = VkConst.DebugSeverityWarning | VkConst.DebugSeverityError,
                MessageType = VkConst.DebugTypeGeneral | VkConst.DebugTypeValidation | VkConst.DebugTypePerformance,
                UserCallback = &DebugCallback,
            };
            ulong m;
            if (Vk.CreateDebugUtilsMessengerEXT(_instance, &mci, null, &m) == VkResult.Success) _messenger = m;
        }

        _mainSurface = mainWindow?.CreateVulkanSurface(_instance) ?? 0;

        // Physical device: Vulkan 1.3, a graphics queue that can present, VK_KHR_swapchain.
        (_physical, _queueFamily) = PickPhysicalDevice(options.PreferDiscrete);
        var props = new byte[VkPhysicalDevicePropertiesLayout.Size];
        fixed (byte* p = props)
        {
            Vk.GetPhysicalDeviceProperties(_physical, p);
            AdapterName = Marshal.PtrToStringUTF8((nint)(p + VkPhysicalDevicePropertiesLayout.DeviceName)) ?? "unknown";
            Limits = new DeviceLimits(
                (int)*(ulong*)(p + VkPhysicalDevicePropertiesLayout.MinUniformBufferOffsetAlignment),
                (int)*(ulong*)(p + VkPhysicalDevicePropertiesLayout.MinStorageBufferOffsetAlignment),
                *(uint*)(p + VkPhysicalDevicePropertiesLayout.MaxUniformBufferRange));
        }
        VkPhysicalDeviceMemoryProperties mem;
        Vk.GetPhysicalDeviceMemoryProperties(_physical, &mem);
        _memory = mem;

        // Logical device.
        float priority = 1;
        var qci = new VkDeviceQueueCreateInfo
        {
            SType = VkStructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = _queueFamily,
            QueueCount = 1,
            QueuePriorities = &priority,
        };
        var f13 = new VkPhysicalDeviceVulkan13Features { SType = VkStructureType.PhysicalDeviceVulkan13Features };
        f13.Bits[VkPhysicalDeviceVulkan13Features.DynamicRendering] = 1;
        f13.Bits[VkPhysicalDeviceVulkan13Features.Synchronization2] = 1;
        var f2 = new VkPhysicalDeviceFeatures2 { SType = VkStructureType.PhysicalDeviceFeatures2, PNext = &f13 };
        using (var dext = new Utf8Strings(mainWindow is null ? [] : ["VK_KHR_swapchain"]))
        {
            var dci = new VkDeviceCreateInfo
            {
                SType = VkStructureType.DeviceCreateInfo,
                PNext = &f2,
                QueueCreateInfoCount = 1,
                QueueCreateInfos = &qci,
                EnabledExtensionCount = (uint)dext.Count,
                EnabledExtensionNames = dext.Pointers,
            };
            nint device;
            Vk.Check(Vk.CreateDevice(_physical, &dci, null, &device), "vkCreateDevice");
            _device = device;
        }
        Vk.LoadDevice(_device);
        nint queue;
        Vk.GetDeviceQueue(_device, _queueFamily, 0, &queue);
        _queue = queue;

        // Per-frame command buffers and sync.
        for (int i = 0; i < FramesInFlight; i++)
        {
            var f = _frames[i] = new PerFrame();
            f.Pool = CreateCommandPool(VkConst.CommandPoolTransient);
            f.Cmd = AllocateCommandBuffer(f.Pool);
            f.List.Cmd = f.Cmd; f.List.Device = this;
            f.Fence = CreateFence(signaled: true);
            f.ImageAcquired = CreateSemaphore();
        }

        _uploadPool = CreateCommandPool(VkConst.CommandPoolResetCommandBuffer);
        _uploadFence = CreateFence(signaled: false);

        // One descriptor pool for every bind group (sets are freed individually).
        var sizes = stackalloc VkDescriptorPoolSize[]
        {
            new() { Type = 0, DescriptorCount = 1024 },   // sampler
            new() { Type = 2, DescriptorCount = 4096 },   // sampled image
            new() { Type = 6, DescriptorCount = 1024 },   // uniform buffer
            new() { Type = 7, DescriptorCount = 1024 },   // storage buffer
            new() { Type = 8, DescriptorCount = 1024 },   // uniform buffer dynamic
            new() { Type = 9, DescriptorCount = 1024 },   // storage buffer dynamic
        };
        var pci = new VkDescriptorPoolCreateInfo
        {
            SType = VkStructureType.DescriptorPoolCreateInfo,
            Flags = VkConst.DescriptorPoolFreeDescriptorSet,
            MaxSets = 4096,
            PoolSizeCount = 6,
            PoolSizes = sizes,
        };
        ulong pool;
        Vk.Check(Vk.CreateDescriptorPool(_device, &pci, null, &pool), "vkCreateDescriptorPool");
        _descriptorPool = pool;
    }

    public override string BackendName => "Vulkan";
    public override string AdapterName { get; }
    public override DeviceLimits Limits { get; }
    public bool ValidationEnabled { get; }

    internal nint Handle => _device;
    internal nint Physical => _physical;
    internal nint Instance => _instance;
    internal uint QueueFamily => _queueFamily;
    internal nint Queue => _queue;

    // ------------------------------------------------------------------ creation

    public override Swapchain CreateSwapchain(IWindow window, bool vsync = true)
    {
        if (window is IOffscreenWindow)
        {
            var size = window.PixelSize;
            return new VulkanOffscreenSwapchain(this, Math.Max(1, size.Width), Math.Max(1, size.Height));
        }
        ulong surface;
        if (ReferenceEquals(window, _mainWindow) && _mainSurface != 0)
        {
            surface = _mainSurface;
            _mainSurface = 0; // the swapchain owns it now
        }
        else surface = window.CreateVulkanSurface(_instance);
        uint supported;
        Vk.GetPhysicalDeviceSurfaceSupportKHR(_physical, _queueFamily, surface, &supported);
        if (supported == 0) throw new InvalidOperationException("The graphics queue can't present to this window.");
        return new VulkanSwapchain(this, window, surface, vsync);
    }

    public override GpuBuffer CreateBuffer(in BufferDesc desc)
    {
        uint usage = 0;
        if ((desc.Usage & BufferUsage.Vertex) != 0) usage |= VkConst.BufferUsageVertex;
        if ((desc.Usage & BufferUsage.Index) != 0) usage |= VkConst.BufferUsageIndex;
        if ((desc.Usage & BufferUsage.Uniform) != 0) usage |= VkConst.BufferUsageUniform;
        if ((desc.Usage & BufferUsage.Storage) != 0) usage |= VkConst.BufferUsageStorage;
        if ((desc.Usage & BufferUsage.CopySrc) != 0) usage |= VkConst.BufferUsageTransferSrc;
        if (desc.Memory == MemoryKind.GpuOnly || (desc.Usage & BufferUsage.CopyDst) != 0) usage |= VkConst.BufferUsageTransferDst;

        var bci = new VkBufferCreateInfo { SType = VkStructureType.BufferCreateInfo, Size = (ulong)desc.Size, Usage = usage };
        ulong buffer;
        Vk.Check(Vk.CreateBuffer(_device, &bci, null, &buffer), "vkCreateBuffer");
        VkMemoryRequirements req;
        Vk.GetBufferMemoryRequirements(_device, buffer, &req);
        ulong memory = desc.Memory == MemoryKind.Upload
            ? Allocate(req, VkConst.MemoryHostVisible | VkConst.MemoryHostCoherent, VkConst.MemoryDeviceLocal)
            : Allocate(req, VkConst.MemoryDeviceLocal, 0);
        Vk.Check(Vk.BindBufferMemory(_device, buffer, memory, 0), "vkBindBufferMemory");
        void* mapped = null;
        if (desc.Memory == MemoryKind.Upload) Vk.Check(Vk.MapMemory(_device, memory, 0, VkConst.WholeSize, 0, &mapped), "vkMapMemory");
        return new VulkanBuffer(this, desc, buffer, memory, (byte*)mapped);
    }

    public override GpuTexture CreateTexture(in TextureDesc desc)
    {
        uint format = Formats.ToVk(desc.Format);
        uint usage = 0;
        if ((desc.Usage & TextureUsage.Sampled) != 0) usage |= VkConst.ImageUsageSampled | VkConst.ImageUsageTransferDst;
        if ((desc.Usage & TextureUsage.RenderTarget) != 0) usage |= VkConst.ImageUsageColorAttachment;
        if ((desc.Usage & TextureUsage.DepthStencil) != 0) usage |= VkConst.ImageUsageDepthAttachment;
        if ((desc.Usage & TextureUsage.CopySrc) != 0) usage |= VkConst.ImageUsageTransferSrc;
        if ((desc.Usage & TextureUsage.CopyDst) != 0) usage |= VkConst.ImageUsageTransferDst;
        var ici = new VkImageCreateInfo
        {
            SType = VkStructureType.ImageCreateInfo,
            ImageType = VkConst.ImageType2D,
            Format = format,
            Extent = new VkExtent3D { Width = (uint)desc.Width, Height = (uint)desc.Height, Depth = 1 },
            MipLevels = (uint)Math.Max(1, desc.MipLevels),
            ArrayLayers = 1,
            Samples = VkConst.SampleCount1,
            Tiling = VkConst.ImageTilingOptimal,
            Usage = usage,
            InitialLayout = VkConst.ImageLayoutUndefined,
        };
        ulong image;
        Vk.Check(Vk.CreateImage(_device, &ici, null, &image), "vkCreateImage");
        VkMemoryRequirements req;
        Vk.GetImageMemoryRequirements(_device, image, &req);
        ulong memory = Allocate(req, VkConst.MemoryDeviceLocal, 0);
        Vk.Check(Vk.BindImageMemory(_device, image, memory, 0), "vkBindImageMemory");
        uint aspect = desc.Format == TextureFormat.Depth32Float ? VkConst.AspectDepth : VkConst.AspectColor;
        ulong view = CreateView(image, format, aspect, (uint)Math.Max(1, desc.MipLevels));
        return new VulkanTexture(this, image, memory, view, desc.Width, desc.Height, desc.Format, format, desc.Usage, desc.Name);
    }

    public override GpuSampler CreateSampler(in SamplerDesc desc)
    {
        uint filter = desc.Filter == Filter.Linear ? 1u : 0u;
        uint address = desc.Address switch { AddressMode.Repeat => 0u, AddressMode.MirroredRepeat => 1u, _ => 2u };
        var sci = new VkSamplerCreateInfo
        {
            SType = VkStructureType.SamplerCreateInfo,
            MagFilter = filter,
            MinFilter = filter,
            MipmapMode = filter,
            AddressModeU = address,
            AddressModeV = address,
            AddressModeW = address,
            MaxLod = 1000,
        };
        ulong s;
        Vk.Check(Vk.CreateSampler(_device, &sci, null, &s), "vkCreateSampler");
        return new VulkanSampler(this, s);
    }

    public override GpuShader CreateShader(in ShaderDesc desc)
    {
        if (desc.Code.Length % 4 != 0) throw new ArgumentException("SPIR-V must be a multiple of 4 bytes.");
        fixed (byte* code = desc.Code)
        {
            var ci = new VkShaderModuleCreateInfo { SType = VkStructureType.ShaderModuleCreateInfo, CodeSize = (nuint)desc.Code.Length, Code = (uint*)code };
            ulong module;
            Vk.Check(Vk.CreateShaderModule(_device, &ci, null, &module), "vkCreateShaderModule");
            return new VulkanShader(this, module, desc.Stage, desc.EntryPoint);
        }
    }

    public override BindGroupLayout CreateBindGroupLayout(ReadOnlySpan<BindingLayout> entries)
    {
        var bindings = stackalloc VkDescriptorSetLayoutBinding[entries.Length];
        for (int i = 0; i < entries.Length; i++)
        {
            bindings[i] = new VkDescriptorSetLayoutBinding
            {
                Binding = (uint)entries[i].Binding,
                DescriptorType = DescriptorType(entries[i].Type),
                DescriptorCount = 1,
                StageFlags = Stages(entries[i].Visibility),
            };
        }
        var ci = new VkDescriptorSetLayoutCreateInfo { SType = VkStructureType.DescriptorSetLayoutCreateInfo, BindingCount = (uint)entries.Length, Bindings = bindings };
        ulong layout;
        Vk.Check(Vk.CreateDescriptorSetLayout(_device, &ci, null, &layout), "vkCreateDescriptorSetLayout");
        return new VulkanBindGroupLayout(this, layout, entries.ToArray());
    }

    public override BindGroup CreateBindGroup(BindGroupLayout layout, ReadOnlySpan<BindGroupEntry> entries)
    {
        var l = (VulkanBindGroupLayout)layout;
        ulong setLayout = l.Handle;
        var ai = new VkDescriptorSetAllocateInfo
        {
            SType = VkStructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _descriptorPool,
            DescriptorSetCount = 1,
            SetLayouts = &setLayout,
        };
        ulong set;
        Vk.Check(Vk.AllocateDescriptorSets(_device, &ai, &set), "vkAllocateDescriptorSets");

        var writes = stackalloc VkWriteDescriptorSet[entries.Length];
        var buffers = stackalloc VkDescriptorBufferInfo[entries.Length];
        var images = stackalloc VkDescriptorImageInfo[entries.Length];
        for (int i = 0; i < entries.Length; i++)
        {
            var e = entries[i];
            var type = l.TypeOf(e.Binding);
            writes[i] = new VkWriteDescriptorSet
            {
                SType = VkStructureType.WriteDescriptorSet,
                DstSet = set,
                DstBinding = (uint)e.Binding,
                DescriptorCount = 1,
                DescriptorType = DescriptorType(type),
            };
            switch (type)
            {
                case BindingType.UniformBuffer:
                case BindingType.UniformBufferDynamic:
                case BindingType.StorageBufferReadOnly:
                case BindingType.StorageBufferReadOnlyDynamic:
                    var b = (VulkanBuffer)(e.Buffer ?? throw new ArgumentException($"Binding {e.Binding} needs a buffer."));
                    buffers[i] = new VkDescriptorBufferInfo { Buffer = b.Handle, Offset = (ulong)e.Offset, Range = e.Size > 0 ? (ulong)e.Size : VkConst.WholeSize };
                    writes[i].BufferInfo = &buffers[i];
                    break;
                case BindingType.SampledTexture:
                    var t = (VulkanTexture)(e.Texture ?? throw new ArgumentException($"Binding {e.Binding} needs a texture."));
                    images[i] = new VkDescriptorImageInfo { ImageView = t.View, ImageLayout = VkConst.ImageLayoutShaderReadOnly };
                    writes[i].ImageInfo = &images[i];
                    break;
                case BindingType.Sampler:
                    var s = (VulkanSampler)(e.Sampler ?? throw new ArgumentException($"Binding {e.Binding} needs a sampler."));
                    images[i] = new VkDescriptorImageInfo { Sampler = s.Handle };
                    writes[i].ImageInfo = &images[i];
                    break;
            }
        }
        Vk.UpdateDescriptorSets(_device, (uint)entries.Length, writes, 0, null);
        return new VulkanBindGroup(this, set);
    }

    public override GpuReadback CreateReadback(int width, int height) => new VulkanReadback(this, width, height);

    public override GraphicsPipeline CreateGraphicsPipeline(GraphicsPipelineDesc desc) => Pipelines.Create(this, desc);

    // ------------------------------------------------------------------ uploads

    public override void WriteBuffer<T>(GpuBuffer buffer, ReadOnlySpan<T> data, long offset = 0)
    {
        var dst = (VulkanBuffer)buffer;
        long bytes = (long)data.Length * Unsafe.SizeOf<T>();
        if (bytes == 0) return;
        if (dst.Mapped != null)
        {
            dst.Write(data, offset);
            return;
        }
        using var staging = (VulkanBuffer)CreateBuffer(new BufferDesc(bytes, BufferUsage.CopySrc, MemoryKind.Upload));
        staging.Write(data);
        ImmediateSubmit(cmd =>
        {
            var region = new VkBufferCopy { SrcOffset = 0, DstOffset = (ulong)offset, Size = (ulong)bytes };
            Vk.CmdCopyBuffer(cmd, staging.Handle, dst.Handle, 1, &region);
        });
        DestroyNow(staging);
    }

    public override void WriteTexture(GpuTexture texture, ReadOnlySpan<byte> pixels)
    {
        var tex = (VulkanTexture)texture;
        using var staging = (VulkanBuffer)CreateBuffer(new BufferDesc(pixels.Length, BufferUsage.CopySrc, MemoryKind.Upload));
        staging.Write(pixels);
        ImmediateSubmit(cmd =>
        {
            Barriers.Transition(cmd, tex, VkConst.ImageLayoutTransferDst);
            var region = new VkBufferImageCopy
            {
                ImageSubresource = new VkImageSubresourceLayers { AspectMask = tex.Aspect, LayerCount = 1 },
                ImageExtent = new VkExtent3D { Width = (uint)tex.Width, Height = (uint)tex.Height, Depth = 1 },
            };
            Vk.CmdCopyBufferToImage(cmd, staging.Handle, tex.Image, VkConst.ImageLayoutTransferDst, 1, &region);
            Barriers.Transition(cmd, tex, VkConst.ImageLayoutShaderReadOnly);
        });
        DestroyNow(staging);
    }

    private void ImmediateSubmit(Action<nint> record)
    {
        nint cmd = AllocateCommandBuffer(_uploadPool);
        var begin = new VkCommandBufferBeginInfo { SType = VkStructureType.CommandBufferBeginInfo, Flags = VkConst.CommandBufferOneTimeSubmit };
        Vk.Check(Vk.BeginCommandBuffer(cmd, &begin), "vkBeginCommandBuffer");
        record(cmd);
        Vk.Check(Vk.EndCommandBuffer(cmd), "vkEndCommandBuffer");
        var cbi = new VkCommandBufferSubmitInfo { SType = VkStructureType.CommandBufferSubmitInfo, CommandBuffer = cmd };
        var submit = new VkSubmitInfo2 { SType = VkStructureType.SubmitInfo2, CommandBufferInfoCount = 1, CommandBufferInfos = &cbi };
        ulong fence = _uploadFence;
        Vk.Check(Vk.QueueSubmit2(_queue, 1, &submit, fence), "vkQueueSubmit2");
        Vk.Check(Vk.WaitForFences(_device, 1, &fence, 1, ulong.MaxValue), "vkWaitForFences");
        Vk.Check(Vk.ResetFences(_device, 1, &fence), "vkResetFences");
        Vk.FreeCommandBuffers(_device, _uploadPool, 1, &cmd);
    }

    private void DestroyNow(VulkanBuffer b)
    {
        Vk.DestroyBuffer(_device, b.Handle, null);
        Vk.FreeMemory(_device, b.MemoryHandle, null);
        b.Handle = b.MemoryHandle = 0;
    }

    // ------------------------------------------------------------------ frames

    public override bool BeginFrame(Swapchain swapchain, out Frame frame)
    {
        frame = default;
        if (swapchain is VulkanOffscreenSwapchain offscreen)
        {
            var slot = _frames[_frameNumber % FramesInFlight];
            ulong ready = slot.Fence;
            var status = Vk.WaitForFences(_device, 1, &ready, 1, 0);
            if (status == VkResult.Timeout) return false;
            Vk.Check(status, "vkWaitForFences");
            FlushDeletes(slot);
            Vk.Check(Vk.ResetFences(_device, 1, &ready), "vkResetFences");
            Vk.Check(Vk.ResetCommandPool(_device, slot.Pool, 0), "vkResetCommandPool");
            var start = new VkCommandBufferBeginInfo { SType = VkStructureType.CommandBufferBeginInfo, Flags = VkConst.CommandBufferOneTimeSubmit };
            Vk.Check(Vk.BeginCommandBuffer(slot.Cmd, &start), "vkBeginCommandBuffer");
            _offscreenFrame = true;
            frame = new Frame(slot.List, offscreen.Texture, (int)(_frameNumber % FramesInFlight), _frameNumber);
            return true;
        }
        var sc = (VulkanSwapchain)swapchain;
        var f = _frames[_frameNumber % FramesInFlight];

        ulong fence = f.Fence;
        Vk.Check(Vk.WaitForFences(_device, 1, &fence, 1, ulong.MaxValue), "vkWaitForFences");
        FlushDeletes(f);

        if (!sc.EnsureCurrent()) return false;

        uint image;
        var r = Vk.AcquireNextImageKHR(_device, sc.Handle, ulong.MaxValue, f.ImageAcquired, 0, &image);
        if (r == VkResult.ErrorOutOfDateKhr)
        {
            sc.Invalidate();
            return false;
        }
        if (r != VkResult.Success && r != VkResult.SuboptimalKhr) Vk.Check(r, "vkAcquireNextImageKHR");
        if (r == VkResult.SuboptimalKhr) sc.Invalidate(); // recreate after this frame

        Vk.Check(Vk.ResetFences(_device, 1, &fence), "vkResetFences");
        Vk.Check(Vk.ResetCommandPool(_device, f.Pool, 0), "vkResetCommandPool");
        var begin = new VkCommandBufferBeginInfo { SType = VkStructureType.CommandBufferBeginInfo, Flags = VkConst.CommandBufferOneTimeSubmit };
        Vk.Check(Vk.BeginCommandBuffer(f.Cmd, &begin), "vkBeginCommandBuffer");

        var backbuffer = sc.Images[image];
        backbuffer.Layout = VkConst.ImageLayoutUndefined; // contents from the last present are discarded
        _currentSwapchain = sc;
        _currentImage = image;
        frame = new Frame(f.List, backbuffer, (int)(_frameNumber % FramesInFlight), _frameNumber);
        return true;
    }

    public override void EndFrame(in Frame frame)
    {
        if (_offscreenFrame)
        {
            var slot = _frames[_frameNumber % FramesInFlight];
            Vk.Check(Vk.EndCommandBuffer(slot.Cmd), "vkEndCommandBuffer");
            var buffer = new VkCommandBufferSubmitInfo { SType = VkStructureType.CommandBufferSubmitInfo, CommandBuffer = slot.Cmd };
            var submission = new VkSubmitInfo2 { SType = VkStructureType.SubmitInfo2, CommandBufferInfoCount = 1, CommandBufferInfos = &buffer };
            Vk.Check(Vk.QueueSubmit2(_queue, 1, &submission, slot.Fence), "vkQueueSubmit2");
            slot.Submitted = _frameNumber; _offscreenFrame = false; _frameNumber++; return;
        }
        var sc = _currentSwapchain ?? throw new InvalidOperationException("EndFrame without BeginFrame.");
        var f = _frames[_frameNumber % FramesInFlight];
        var backbuffer = sc.Images[_currentImage];
        Barriers.Transition(f.Cmd, backbuffer, VkConst.ImageLayoutPresentSrc);
        Vk.Check(Vk.EndCommandBuffer(f.Cmd), "vkEndCommandBuffer");

        var wait = new VkSemaphoreSubmitInfo { SType = VkStructureType.SemaphoreSubmitInfo, Semaphore = f.ImageAcquired, StageMask = VkConst.StageColorAttachmentOutput };
        var signal = new VkSemaphoreSubmitInfo { SType = VkStructureType.SemaphoreSubmitInfo, Semaphore = sc.RenderFinished[_currentImage], StageMask = VkConst.StageAllCommands };
        var cbi = new VkCommandBufferSubmitInfo { SType = VkStructureType.CommandBufferSubmitInfo, CommandBuffer = f.Cmd };
        var submit = new VkSubmitInfo2
        {
            SType = VkStructureType.SubmitInfo2,
            WaitSemaphoreInfoCount = 1,
            WaitSemaphoreInfos = &wait,
            CommandBufferInfoCount = 1,
            CommandBufferInfos = &cbi,
            SignalSemaphoreInfoCount = 1,
            SignalSemaphoreInfos = &signal,
        };
        Vk.Check(Vk.QueueSubmit2(_queue, 1, &submit, f.Fence), "vkQueueSubmit2");

        ulong swapchain = sc.Handle;
        ulong renderFinished = sc.RenderFinished[_currentImage];
        uint image = _currentImage;
        var present = new VkPresentInfoKHR
        {
            SType = VkStructureType.PresentInfoKhr,
            WaitSemaphoreCount = 1,
            WaitSemaphores = &renderFinished,
            SwapchainCount = 1,
            Swapchains = &swapchain,
            ImageIndices = &image,
        };
        var r = Vk.QueuePresentKHR(_queue, &present);
        if (r is VkResult.ErrorOutOfDateKhr or VkResult.SuboptimalKhr) sc.Invalidate();
        else Vk.Check(r, "vkQueuePresentKHR");

        f.Submitted = _frameNumber;
        _currentSwapchain = null;
        _frameNumber++;
    }

    internal bool IsComplete(ulong number)
    {
        var slot = _frames[number % FramesInFlight];
        if (slot.Submitted is not { } submitted) return false;
        if (submitted > number) return true;
        if (submitted != number) return false;
        return Vk.GetFenceStatus(_device, slot.Fence) == VkResult.Success;
    }
    internal ulong RecordingFrame => _frameNumber;

    public override void WaitIdle() => Vk.DeviceWaitIdle(_device);

    // ------------------------------------------------------------------ helpers

    internal void DeferDestroy(DeferredKind kind, ulong handle, ulong memory)
    {
        if (_disposed) return;
        _frames[_frameNumber % FramesInFlight].Deletes.Add(new Deferred(kind, handle, memory));
    }

    private void FlushDeletes(PerFrame f)
    {
        foreach (var d in f.Deletes) Destroy(d);
        f.Deletes.Clear();
    }

    private void Destroy(Deferred d)
    {
        switch (d.Kind)
        {
            case DeferredKind.Buffer: Vk.DestroyBuffer(_device, d.Handle, null); break;
            case DeferredKind.Image: Vk.DestroyImage(_device, d.Handle, null); break;
            case DeferredKind.ImageView: Vk.DestroyImageView(_device, d.Handle, null); break;
            case DeferredKind.Sampler: Vk.DestroySampler(_device, d.Handle, null); break;
            case DeferredKind.ShaderModule: Vk.DestroyShaderModule(_device, d.Handle, null); break;
            case DeferredKind.DescriptorSetLayout: Vk.DestroyDescriptorSetLayout(_device, d.Handle, null); break;
            case DeferredKind.DescriptorSet:
                ulong set = d.Handle;
                Vk.FreeDescriptorSets(_device, _descriptorPool, 1, &set);
                break;
            case DeferredKind.Pipeline: Vk.DestroyPipeline(_device, d.Handle, null); break;
            case DeferredKind.PipelineLayout: Vk.DestroyPipelineLayout(_device, d.Handle, null); break;
        }
        if (d.Memory != 0) Vk.FreeMemory(_device, d.Memory, null);
    }

    internal ulong CreateView(ulong image, uint format, uint aspect, uint mips)
    {
        var vci = new VkImageViewCreateInfo
        {
            SType = VkStructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = VkConst.ImageViewType2D,
            Format = format,
            SubresourceRange = new VkImageSubresourceRange { AspectMask = aspect, LevelCount = mips, LayerCount = 1 },
        };
        ulong view;
        Vk.Check(Vk.CreateImageView(_device, &vci, null, &view), "vkCreateImageView");
        return view;
    }

    internal ulong CreateSemaphore()
    {
        var sci = new VkSemaphoreCreateInfo { SType = VkStructureType.SemaphoreCreateInfo };
        ulong s;
        Vk.Check(Vk.CreateSemaphore(_device, &sci, null, &s), "vkCreateSemaphore");
        return s;
    }

    private ulong CreateFence(bool signaled)
    {
        var fci = new VkFenceCreateInfo { SType = VkStructureType.FenceCreateInfo, Flags = signaled ? VkConst.FenceSignaled : 0 };
        ulong f;
        Vk.Check(Vk.CreateFence(_device, &fci, null, &f), "vkCreateFence");
        return f;
    }

    private ulong CreateCommandPool(uint flags)
    {
        var ci = new VkCommandPoolCreateInfo { SType = VkStructureType.CommandPoolCreateInfo, Flags = flags, QueueFamilyIndex = _queueFamily };
        ulong pool;
        Vk.Check(Vk.CreateCommandPool(_device, &ci, null, &pool), "vkCreateCommandPool");
        return pool;
    }

    private nint AllocateCommandBuffer(ulong pool)
    {
        var ai = new VkCommandBufferAllocateInfo { SType = VkStructureType.CommandBufferAllocateInfo, CommandPool = pool, Level = 0, CommandBufferCount = 1 };
        nint cmd;
        Vk.Check(Vk.AllocateCommandBuffers(_device, &ai, &cmd), "vkAllocateCommandBuffers");
        return cmd;
    }

    private ulong Allocate(in VkMemoryRequirements req, uint required, uint preferred)
    {
        int type = FindMemoryType(req.MemoryTypeBits, required | preferred);
        if (type < 0) type = FindMemoryType(req.MemoryTypeBits, required);
        if (type < 0) throw new InvalidOperationException("No suitable Vulkan memory type.");
        var ai = new VkMemoryAllocateInfo { SType = VkStructureType.MemoryAllocateInfo, AllocationSize = req.Size, MemoryTypeIndex = (uint)type };
        ulong memory;
        Vk.Check(Vk.AllocateMemory(_device, &ai, null, &memory), "vkAllocateMemory");
        return memory;
    }

    private int FindMemoryType(uint bits, uint flags)
    {
        var mem = _memory;
        for (int i = 0; i < mem.MemoryTypeCount; i++)
            if ((bits & (1u << i)) != 0 && (mem.Type(i).PropertyFlags & flags) == flags) return i;
        return -1;
    }

    private (nint Device, uint Family) PickPhysicalDevice(bool preferDiscrete)
    {
        uint count;
        Vk.Check(Vk.EnumeratePhysicalDevices(_instance, &count, null), "vkEnumeratePhysicalDevices");
        var devices = new nint[count];
        fixed (nint* d = devices) Vk.Check(Vk.EnumeratePhysicalDevices(_instance, &count, d), "vkEnumeratePhysicalDevices");

        (nint, uint) best = default;
        int bestScore = -1;
        var props = new byte[VkPhysicalDevicePropertiesLayout.Size];
        foreach (var pd in devices)
        {
            uint api, type;
            fixed (byte* p = props)
            {
                Vk.GetPhysicalDeviceProperties(pd, p);
                api = *(uint*)(p + VkPhysicalDevicePropertiesLayout.ApiVersion);
                type = *(uint*)(p + VkPhysicalDevicePropertiesLayout.DeviceType);
            }
            if (api < VkConst.ApiVersion13 || _mainWindow is not null && !HasDeviceExtension(pd, "VK_KHR_swapchain")) continue;
            int family = FindQueueFamily(pd);
            if (family < 0) continue;
            int score = type switch { 2 => preferDiscrete ? 3 : 2, 1 => preferDiscrete ? 2 : 3, 4 => 0, _ => 1 }; // 4 = CPU
            if (score > bestScore)
            {
                bestScore = score;
                best = (pd, (uint)family);
            }
        }
        if (bestScore < 0) throw new InvalidOperationException("No Vulkan 1.3 GPU with a graphics queue that can present.");
        return best;
    }

    private int FindQueueFamily(nint pd)
    {
        uint count;
        Vk.GetPhysicalDeviceQueueFamilyProperties(pd, &count, null);
        var families = stackalloc VkQueueFamilyProperties[(int)count];
        Vk.GetPhysicalDeviceQueueFamilyProperties(pd, &count, families);
        for (uint i = 0; i < count; i++)
        {
            if ((families[i].QueueFlags & VkConst.QueueGraphics) == 0) continue;
            if (_mainWindow is null) return (int)i;
            uint present;
            Vk.GetPhysicalDeviceSurfaceSupportKHR(pd, i, _mainSurface, &present);
            if (present != 0) return (int)i;
        }
        return -1;
    }

    private static bool HasDeviceExtension(nint pd, string name)
    {
        uint count;
        Vk.EnumerateDeviceExtensionProperties(pd, null, &count, null);
        var props = new VkExtensionProperties[count];
        fixed (VkExtensionProperties* p = props) Vk.EnumerateDeviceExtensionProperties(pd, null, &count, p);
        foreach (var e in props)
            if (Marshal.PtrToStringUTF8((nint)e.ExtensionName) == name) return true;
        return false;
    }

    private static bool HasInstanceExtension(string name)
    {
        uint count;
        Vk.EnumerateInstanceExtensionProperties(null, &count, null);
        var props = new VkExtensionProperties[count];
        fixed (VkExtensionProperties* p = props) Vk.EnumerateInstanceExtensionProperties(null, &count, p);
        foreach (var e in props)
            if (Marshal.PtrToStringUTF8((nint)e.ExtensionName) == name) return true;
        return false;
    }

    private static bool HasInstanceLayer(string name)
    {
        uint count;
        Vk.EnumerateInstanceLayerProperties(&count, null);
        var props = new VkLayerProperties[count];
        fixed (VkLayerProperties* p = props) Vk.EnumerateInstanceLayerProperties(&count, p);
        foreach (var l in props)
            if (Marshal.PtrToStringUTF8((nint)l.LayerName) == name) return true;
        return false;
    }

    internal static uint DescriptorType(BindingType t) => t switch
    {
        BindingType.Sampler => 0,
        BindingType.SampledTexture => 2,
        BindingType.UniformBuffer => 6,
        BindingType.StorageBufferReadOnly => 7,
        BindingType.UniformBufferDynamic => 8,
        BindingType.StorageBufferReadOnlyDynamic => 9,
        _ => throw new ArgumentOutOfRangeException(nameof(t)),
    };

    internal static uint Stages(ShaderStages s)
    {
        uint r = 0;
        if ((s & ShaderStages.Vertex) != 0) r |= VkConst.ShaderStageVertex;
        if ((s & ShaderStages.Fragment) != 0) r |= VkConst.ShaderStageFragment;
        if ((s & ShaderStages.Compute) != 0) r |= VkConst.ShaderStageCompute;
        return r;
    }

    [UnmanagedCallersOnly]
    private static uint DebugCallback(uint severity, uint types, VkDebugUtilsMessengerCallbackDataEXT* data, void* user)
    {
        string level = severity >= VkConst.DebugSeverityError ? "error" : "warning";
        Log.Warn($"[vulkan {level}] {Marshal.PtrToStringUTF8((nint)data->Message)}");
        return 0;
    }

    public override void Dispose()
    {
        if (_disposed) return;
        Vk.DeviceWaitIdle(_device);
        foreach (var f in _frames) FlushDeletes(f);
        _disposed = true;
        foreach (var f in _frames)
        {
            Vk.DestroyFence(_device, f.Fence, null);
            Vk.DestroySemaphore(_device, f.ImageAcquired, null);
            Vk.DestroyCommandPool(_device, f.Pool, null);
        }
        Vk.DestroyFence(_device, _uploadFence, null);
        Vk.DestroyCommandPool(_device, _uploadPool, null);
        Vk.DestroyDescriptorPool(_device, _descriptorPool, null);
        Vk.DestroyDevice(_device, null);
        if (_mainSurface != 0) Vk.DestroySurfaceKHR(_instance, _mainSurface, null);
        if (_messenger != 0) Vk.DestroyDebugUtilsMessengerEXT(_instance, _messenger, null);
        Vk.DestroyInstance(_instance, null);
    }

    internal void DestroySurface(ulong surface) => Vk.DestroySurfaceKHR(_instance, surface, null);
}

/// <summary>Pins a list of strings as NUL-terminated UTF-8 for a Vulkan create-info.</summary>
internal sealed unsafe class Utf8Strings : IDisposable
{
    private readonly nint[] _allocations;
    public readonly byte** Pointers;
    public readonly int Count;

    public Utf8Strings(IReadOnlyList<string> strings)
    {
        Count = strings.Count;
        _allocations = new nint[Count];
        Pointers = (byte**)NativeMemory.Alloc((nuint)Math.Max(1, Count), (nuint)sizeof(nint));
        for (int i = 0; i < Count; i++)
        {
            _allocations[i] = Marshal.StringToCoTaskMemUTF8(strings[i]);
            Pointers[i] = (byte*)_allocations[i];
        }
    }

    public void Dispose()
    {
        foreach (var a in _allocations) Marshal.FreeCoTaskMem(a);
        NativeMemory.Free(Pointers);
    }
}
