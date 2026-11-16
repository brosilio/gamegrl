using System.Runtime.InteropServices;
using System.Text;

namespace Gamegrl.Rhi.Vulkan;

// The slice of Vulkan 1.3 we use, written against vulkan_core.h. Struct sizes are checked against the C header by
// tests (VkLayoutTests). Dispatchable handles are nint; non-dispatchable handles are ulong.

internal enum VkResult
{
    Success = 0, NotReady = 1, Timeout = 2, Incomplete = 5,
    ErrorOutOfHostMemory = -1, ErrorOutOfDeviceMemory = -2, ErrorInitializationFailed = -3, ErrorDeviceLost = -4,
    ErrorLayerNotPresent = -6, ErrorExtensionNotPresent = -7, ErrorFeatureNotPresent = -8, ErrorIncompatibleDriver = -9,
    SuboptimalKhr = 1000001003, ErrorOutOfDateKhr = -1000001004, ErrorSurfaceLostKhr = -1000000000,
}

internal static class VkStructureType
{
    public const uint ApplicationInfo = 0, InstanceCreateInfo = 1, DeviceQueueCreateInfo = 2, DeviceCreateInfo = 3,
        MemoryAllocateInfo = 5, FenceCreateInfo = 8, SemaphoreCreateInfo = 9, BufferCreateInfo = 12, ImageCreateInfo = 14,
        ImageViewCreateInfo = 15, ShaderModuleCreateInfo = 16, PipelineShaderStageCreateInfo = 18,
        PipelineVertexInputStateCreateInfo = 19, PipelineInputAssemblyStateCreateInfo = 20, PipelineViewportStateCreateInfo = 22,
        PipelineRasterizationStateCreateInfo = 23, PipelineMultisampleStateCreateInfo = 24, PipelineDepthStencilStateCreateInfo = 25,
        PipelineColorBlendStateCreateInfo = 26, PipelineDynamicStateCreateInfo = 27, GraphicsPipelineCreateInfo = 28,
        PipelineLayoutCreateInfo = 30, SamplerCreateInfo = 31, DescriptorSetLayoutCreateInfo = 32, DescriptorPoolCreateInfo = 33,
        DescriptorSetAllocateInfo = 34, WriteDescriptorSet = 35, CommandPoolCreateInfo = 39, CommandBufferAllocateInfo = 40,
        CommandBufferBeginInfo = 42, PhysicalDeviceVulkan13Features = 53, PhysicalDeviceFeatures2 = 1000059000,
        SwapchainCreateInfoKhr = 1000001000, PresentInfoKhr = 1000001001,
        RenderingInfo = 1000044000, RenderingAttachmentInfo = 1000044001, PipelineRenderingCreateInfo = 1000044002,
        ImageMemoryBarrier2 = 1000314002, DependencyInfo = 1000314003, SubmitInfo2 = 1000314004,
        SemaphoreSubmitInfo = 1000314005, CommandBufferSubmitInfo = 1000314006,
        DebugUtilsMessengerCreateInfoExt = 1000128004;
}

internal static class VkConst
{
    public const uint ApiVersion13 = (1u << 22) | (3u << 12);
    public const ulong WholeSize = ulong.MaxValue;
    public const uint QueueFamilyIgnored = uint.MaxValue;
    public const uint QueueGraphics = 1;
    public const uint MemoryDeviceLocal = 1, MemoryHostVisible = 2, MemoryHostCoherent = 4;
    public const uint ImageLayoutUndefined = 0, ImageLayoutColorAttachment = 2, ImageLayoutDepthAttachment = 3,
        ImageLayoutShaderReadOnly = 5, ImageLayoutTransferSrc = 6, ImageLayoutTransferDst = 7, ImageLayoutPresentSrc = 1000001002;
    public const uint AspectColor = 1, AspectDepth = 2;
    public const uint ImageUsageTransferSrc = 1, ImageUsageTransferDst = 2, ImageUsageSampled = 4, ImageUsageColorAttachment = 0x10, ImageUsageDepthAttachment = 0x20;
    public const uint BufferUsageTransferSrc = 1, BufferUsageTransferDst = 2, BufferUsageUniform = 0x10, BufferUsageStorage = 0x20,
        BufferUsageIndex = 0x40, BufferUsageVertex = 0x80;
    public const uint FormatR8G8B8A8Unorm = 37, FormatR8G8B8A8Srgb = 43, FormatB8G8R8A8Unorm = 44, FormatB8G8R8A8Srgb = 50,
        FormatR16G16B16A16Sfloat = 97, FormatD32Sfloat = 126,
        FormatR32Sfloat = 100, FormatR32G32Sfloat = 103, FormatR32G32B32Sfloat = 106, FormatR32G32B32A32Sfloat = 109;
    public const uint ColorSpaceSrgbNonlinear = 0;
    public const uint PresentModeImmediate = 0, PresentModeMailbox = 1, PresentModeFifo = 2;
    public const uint CompositeAlphaOpaque = 1;
    public const uint SurfaceTransformIdentity = 1;
    public const uint FenceSignaled = 1;
    public const uint CommandPoolResetCommandBuffer = 2, CommandPoolTransient = 1;
    public const uint CommandBufferOneTimeSubmit = 1;
    public const uint DescriptorPoolFreeDescriptorSet = 1;
    public const uint PipelineBindPointGraphics = 0;
    public const uint ShaderStageVertex = 1, ShaderStageFragment = 0x10, ShaderStageCompute = 0x20;
    public const uint AttachmentLoadLoad = 0, AttachmentLoadClear = 1, AttachmentLoadDontCare = 2;
    public const uint AttachmentStoreStore = 0, AttachmentStoreDontCare = 1;
    public const uint DynamicStateViewport = 0, DynamicStateScissor = 1;
    public const uint ColorComponentAll = 0xF;
    public const uint SampleCount1 = 1;
    public const uint ImageType2D = 1, ImageViewType2D = 1, ImageTilingOptimal = 0, SharingExclusive = 0;
    public const uint IndexTypeUint16 = 0, IndexTypeUint32 = 1;

    // VkPipelineStageFlagBits2 / VkAccessFlagBits2 (64-bit).
    public const ulong StageNone = 0, StageTopOfPipe = 1, StageColorAttachmentOutput = 0x400, StageEarlyFragmentTests = 0x100,
        StageLateFragmentTests = 0x200, StageFragmentShader = 0x80, StageVertexShader = 0x8, StageTransfer = 0x1000,
        StageAllCommands = 0x10000, StageBottomOfPipe = 0x2000, StageAllTransfer = 0x1000;
    public const ulong AccessNone = 0, AccessShaderRead = 0x20, AccessColorAttachmentWrite = 0x100, AccessColorAttachmentRead = 0x80,
        AccessDepthAttachmentRead = 0x200, AccessDepthAttachmentWrite = 0x400, AccessTransferRead = 0x800, AccessTransferWrite = 0x1000, AccessMemoryRead = 0x8000;

    public const uint DebugSeverityVerbose = 1, DebugSeverityInfo = 0x10, DebugSeverityWarning = 0x100, DebugSeverityError = 0x1000;
    public const uint DebugTypeGeneral = 1, DebugTypeValidation = 2, DebugTypePerformance = 4;
}

// ------------------------------------------------------------------ structs (layouts per vulkan_core.h)

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct VkApplicationInfo
{
    public uint SType; public void* PNext; public byte* ApplicationName; public uint ApplicationVersion;
    public byte* EngineName; public uint EngineVersion; public uint ApiVersion;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct VkInstanceCreateInfo
{
    public uint SType; public void* PNext; public uint Flags; public VkApplicationInfo* ApplicationInfo;
    public uint EnabledLayerCount; public byte** EnabledLayerNames; public uint EnabledExtensionCount; public byte** EnabledExtensionNames;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct VkDeviceQueueCreateInfo
{
    public uint SType; public void* PNext; public uint Flags; public uint QueueFamilyIndex; public uint QueueCount; public float* QueuePriorities;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct VkDeviceCreateInfo
{
    public uint SType; public void* PNext; public uint Flags; public uint QueueCreateInfoCount; public VkDeviceQueueCreateInfo* QueueCreateInfos;
    public uint EnabledLayerCount; public byte** EnabledLayerNames; public uint EnabledExtensionCount; public byte** EnabledExtensionNames;
    public void* EnabledFeatures;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct VkPhysicalDeviceFeatures2
{
    public uint SType; public void* PNext; public fixed uint Features[55];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct VkPhysicalDeviceVulkan13Features
{
    public uint SType; public void* PNext;
    public fixed uint Bits[15]; // robustImageAccess .. maintenance4
    public const int Synchronization2 = 9, DynamicRendering = 12;
}

[StructLayout(LayoutKind.Sequential)]
internal struct VkQueueFamilyProperties
{
    public uint QueueFlags; public uint QueueCount; public uint TimestampValidBits; public uint GranularityW, GranularityH, GranularityD;
}

[StructLayout(LayoutKind.Sequential)]
internal struct VkMemoryType { public uint PropertyFlags; public uint HeapIndex; }

[StructLayout(LayoutKind.Sequential)]
internal struct VkMemoryHeap { public ulong Size; public uint Flags; }

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct VkPhysicalDeviceMemoryProperties
{
    public uint MemoryTypeCount;
    public fixed byte MemoryTypes[32 * 8];
    public uint MemoryHeapCount;
    public fixed byte MemoryHeaps[16 * 16];

    public VkMemoryType Type(int i)
    {
        fixed (byte* p = MemoryTypes) return ((VkMemoryType*)p)[i];
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct VkExtent2D { public uint Width, Height; }

[StructLayout(LayoutKind.Sequential)]
internal struct VkOffset2D { public int X, Y; }

[StructLayout(LayoutKind.Sequential)]
internal struct VkRect2D { public VkOffset2D Offset; public VkExtent2D Extent; }

[StructLayout(LayoutKind.Sequential)]
internal struct VkViewport { public float X, Y, Width, Height, MinDepth, MaxDepth; }

[StructLayout(LayoutKind.Sequential)]
internal struct VkSurfaceCapabilitiesKHR
{
    public uint MinImageCount, MaxImageCount; public VkExtent2D CurrentExtent, MinImageExtent, MaxImageExtent;
    public uint MaxImageArrayLayers, SupportedTransforms, CurrentTransform, SupportedCompositeAlpha, SupportedUsageFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct VkSurfaceFormatKHR { public uint Format, ColorSpace; }

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct VkSwapchainCreateInfoKHR
{
    public uint SType; public void* PNext; public uint Flags; public ulong Surface; public uint MinImageCount; public uint ImageFormat;
    public uint ImageColorSpace; public VkExtent2D ImageExtent; public uint ImageArrayLayers; public uint ImageUsage; public uint ImageSharingMode;
    public uint QueueFamilyIndexCount; public uint* QueueFamilyIndices; public uint PreTransform; public uint CompositeAlpha;
    public uint PresentMode; public uint Clipped; public ulong OldSwapchain;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct VkPresentInfoKHR
{
    public uint SType; public void* PNext; public uint WaitSemaphoreCount; public ulong* WaitSemaphores; public uint SwapchainCount;
    public ulong* Swapchains; public uint* ImageIndices; public VkResult* Results;
}

[StructLayout(LayoutKind.Sequential)]
internal struct VkComponentMapping { public uint R, G, B, A; }

[StructLayout(LayoutKind.Sequential)]
internal struct VkImageSubresourceRange { public uint AspectMask, BaseMipLevel, LevelCount, BaseArrayLayer, LayerCount; }

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct VkImageViewCreateInfo
{
    public uint SType; public void* PNext; public uint Flags; public ulong Image; public uint ViewType; public uint Format;
    public VkComponentMapping Components; public VkImageSubresourceRange SubresourceRange;
}

[StructLayout(LayoutKind.Sequential)]
internal struct VkExtent3D { public uint Width, Height, Depth; }

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct VkImageCreateInfo
{
    public uint SType; public void* PNext; public uint Flags; public uint ImageType; public uint Format; public VkExtent3D Extent;
    public uint MipLevels; public uint ArrayLayers; public uint Samples; public uint Tiling; public uint Usage; public uint SharingMode;
    public uint QueueFamilyIndexCount; public uint* QueueFamilyIndices; public uint InitialLayout;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct VkBufferCreateInfo
{
    public uint SType; public void* PNext; public uint Flags; public ulong Size; public uint Usage; public uint SharingMode;
    public uint QueueFamilyIndexCount; public uint* QueueFamilyIndices;
}

[StructLayout(LayoutKind.Sequential)]
internal struct VkMemoryRequirements { public ulong Size, Alignment; public uint MemoryTypeBits; }

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct VkMemoryAllocateInfo { public uint SType; public void* PNext; public ulong AllocationSize; public uint MemoryTypeIndex; }

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct VkCommandPoolCreateInfo { public uint SType; public void* PNext; public uint Flags; public uint QueueFamilyIndex; }

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct VkCommandBufferAllocateInfo { public uint SType; public void* PNext; public ulong CommandPool; public uint Level; public uint CommandBufferCount; }

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct VkCommandBufferBeginInfo { public uint SType; public void* PNext; public uint Flags; public void* InheritanceInfo; }

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct VkFenceCreateInfo { public uint SType; public void* PNext; public uint Flags; }

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct VkSemaphoreCreateInfo { public uint SType; public void* PNext; public uint Flags; }

[StructLayout(LayoutKind.Explicit, Size = 16)]
internal unsafe struct VkClearValue
{
    [FieldOffset(0)] public fixed float Color[4];
    [FieldOffset(0)] public float Depth;
    [FieldOffset(4)] public uint Stencil;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct VkRenderingAttachmentInfo
{
    public uint SType; public void* PNext; public ulong ImageView; public uint ImageLayout; public uint ResolveMode;
    public ulong ResolveImageView; public uint ResolveImageLayout; public uint LoadOp; public uint StoreOp; public VkClearValue ClearValue;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct VkRenderingInfo
{
    public uint SType; public void* PNext; public uint Flags; public VkRect2D RenderArea; public uint LayerCount; public uint ViewMask;
    public uint ColorAttachmentCount; public VkRenderingAttachmentInfo* ColorAttachments;
    public VkRenderingAttachmentInfo* DepthAttachment; public VkRenderingAttachmentInfo* StencilAttachment;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct VkImageMemoryBarrier2
{
    public uint SType; public void* PNext; public ulong SrcStageMask; public ulong SrcAccessMask; public ulong DstStageMask; public ulong DstAccessMask;
    public uint OldLayout; public uint NewLayout; public uint SrcQueueFamilyIndex; public uint DstQueueFamilyIndex; public ulong Image;
    public VkImageSubresourceRange SubresourceRange;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct VkDependencyInfo
{
    public uint SType; public void* PNext; public uint DependencyFlags; public uint MemoryBarrierCount; public void* MemoryBarriers;
    public uint BufferMemoryBarrierCount; public void* BufferMemoryBarriers; public uint ImageMemoryBarrierCount; public VkImageMemoryBarrier2* ImageMemoryBarriers;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct VkSemaphoreSubmitInfo
{
    public uint SType; public void* PNext; public ulong Semaphore; public ulong Value; public ulong StageMask; public uint DeviceIndex;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct VkCommandBufferSubmitInfo { public uint SType; public void* PNext; public nint CommandBuffer; public uint DeviceMask; }

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct VkSubmitInfo2
{
    public uint SType; public void* PNext; public uint Flags; public uint WaitSemaphoreInfoCount; public VkSemaphoreSubmitInfo* WaitSemaphoreInfos;
    public uint CommandBufferInfoCount; public VkCommandBufferSubmitInfo* CommandBufferInfos;
    public uint SignalSemaphoreInfoCount; public VkSemaphoreSubmitInfo* SignalSemaphoreInfos;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct VkSamplerCreateInfo
{
    public uint SType; public void* PNext; public uint Flags; public uint MagFilter; public uint MinFilter; public uint MipmapMode;
    public uint AddressModeU, AddressModeV, AddressModeW; public float MipLodBias; public uint AnisotropyEnable; public float MaxAnisotropy;
    public uint CompareEnable; public uint CompareOp; public float MinLod, MaxLod; public uint BorderColor; public uint UnnormalizedCoordinates;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct VkShaderModuleCreateInfo { public uint SType; public void* PNext; public uint Flags; public nuint CodeSize; public uint* Code; }

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct VkDescriptorSetLayoutBinding
{
    public uint Binding; public uint DescriptorType; public uint DescriptorCount; public uint StageFlags; public ulong* ImmutableSamplers;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct VkDescriptorSetLayoutCreateInfo { public uint SType; public void* PNext; public uint Flags; public uint BindingCount; public VkDescriptorSetLayoutBinding* Bindings; }

[StructLayout(LayoutKind.Sequential)]
internal struct VkDescriptorPoolSize { public uint Type, DescriptorCount; }

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct VkDescriptorPoolCreateInfo { public uint SType; public void* PNext; public uint Flags; public uint MaxSets; public uint PoolSizeCount; public VkDescriptorPoolSize* PoolSizes; }

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct VkDescriptorSetAllocateInfo { public uint SType; public void* PNext; public ulong DescriptorPool; public uint DescriptorSetCount; public ulong* SetLayouts; }

[StructLayout(LayoutKind.Sequential)]
internal struct VkDescriptorBufferInfo { public ulong Buffer, Offset, Range; }

[StructLayout(LayoutKind.Sequential)]
internal struct VkDescriptorImageInfo { public ulong Sampler, ImageView; public uint ImageLayout; }

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct VkWriteDescriptorSet
{
    public uint SType; public void* PNext; public ulong DstSet; public uint DstBinding; public uint DstArrayElement; public uint DescriptorCount;
    public uint DescriptorType; public VkDescriptorImageInfo* ImageInfo; public VkDescriptorBufferInfo* BufferInfo; public ulong* TexelBufferView;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct VkPipelineLayoutCreateInfo
{
    public uint SType; public void* PNext; public uint Flags; public uint SetLayoutCount; public ulong* SetLayouts;
    public uint PushConstantRangeCount; public void* PushConstantRanges;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct VkPipelineShaderStageCreateInfo
{
    public uint SType; public void* PNext; public uint Flags; public uint Stage; public ulong Module; public byte* Name; public void* SpecializationInfo;
}

[StructLayout(LayoutKind.Sequential)]
internal struct VkVertexInputBindingDescription { public uint Binding, Stride, InputRate; }

[StructLayout(LayoutKind.Sequential)]
internal struct VkVertexInputAttributeDescription { public uint Location, Binding, Format, Offset; }

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct VkPipelineVertexInputStateCreateInfo
{
    public uint SType; public void* PNext; public uint Flags; public uint VertexBindingDescriptionCount; public VkVertexInputBindingDescription* VertexBindingDescriptions;
    public uint VertexAttributeDescriptionCount; public VkVertexInputAttributeDescription* VertexAttributeDescriptions;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct VkPipelineInputAssemblyStateCreateInfo { public uint SType; public void* PNext; public uint Flags; public uint Topology; public uint PrimitiveRestartEnable; }

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct VkPipelineViewportStateCreateInfo
{
    public uint SType; public void* PNext; public uint Flags; public uint ViewportCount; public VkViewport* Viewports; public uint ScissorCount; public VkRect2D* Scissors;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct VkPipelineRasterizationStateCreateInfo
{
    public uint SType; public void* PNext; public uint Flags; public uint DepthClampEnable; public uint RasterizerDiscardEnable; public uint PolygonMode;
    public uint CullMode; public uint FrontFace; public uint DepthBiasEnable; public float DepthBiasConstantFactor; public float DepthBiasClamp;
    public float DepthBiasSlopeFactor; public float LineWidth;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct VkPipelineMultisampleStateCreateInfo
{
    public uint SType; public void* PNext; public uint Flags; public uint RasterizationSamples; public uint SampleShadingEnable; public float MinSampleShading;
    public uint* SampleMask; public uint AlphaToCoverageEnable; public uint AlphaToOneEnable;
}

[StructLayout(LayoutKind.Sequential)]
internal struct VkStencilOpState { public uint FailOp, PassOp, DepthFailOp, CompareOp, CompareMask, WriteMask, Reference; }

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct VkPipelineDepthStencilStateCreateInfo
{
    public uint SType; public void* PNext; public uint Flags; public uint DepthTestEnable; public uint DepthWriteEnable; public uint DepthCompareOp;
    public uint DepthBoundsTestEnable; public uint StencilTestEnable; public VkStencilOpState Front; public VkStencilOpState Back;
    public float MinDepthBounds; public float MaxDepthBounds;
}

[StructLayout(LayoutKind.Sequential)]
internal struct VkPipelineColorBlendAttachmentState
{
    public uint BlendEnable; public uint SrcColorBlendFactor; public uint DstColorBlendFactor; public uint ColorBlendOp;
    public uint SrcAlphaBlendFactor; public uint DstAlphaBlendFactor; public uint AlphaBlendOp; public uint ColorWriteMask;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct VkPipelineColorBlendStateCreateInfo
{
    public uint SType; public void* PNext; public uint Flags; public uint LogicOpEnable; public uint LogicOp; public uint AttachmentCount;
    public VkPipelineColorBlendAttachmentState* Attachments; public fixed float BlendConstants[4];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct VkPipelineDynamicStateCreateInfo { public uint SType; public void* PNext; public uint Flags; public uint DynamicStateCount; public uint* DynamicStates; }

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct VkPipelineRenderingCreateInfo
{
    public uint SType; public void* PNext; public uint ViewMask; public uint ColorAttachmentCount; public uint* ColorAttachmentFormats;
    public uint DepthAttachmentFormat; public uint StencilAttachmentFormat;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct VkGraphicsPipelineCreateInfo
{
    public uint SType; public void* PNext; public uint Flags; public uint StageCount; public VkPipelineShaderStageCreateInfo* Stages;
    public VkPipelineVertexInputStateCreateInfo* VertexInputState; public VkPipelineInputAssemblyStateCreateInfo* InputAssemblyState;
    public void* TessellationState; public VkPipelineViewportStateCreateInfo* ViewportState;
    public VkPipelineRasterizationStateCreateInfo* RasterizationState; public VkPipelineMultisampleStateCreateInfo* MultisampleState;
    public VkPipelineDepthStencilStateCreateInfo* DepthStencilState; public VkPipelineColorBlendStateCreateInfo* ColorBlendState;
    public VkPipelineDynamicStateCreateInfo* DynamicState; public ulong Layout; public ulong RenderPass; public uint Subpass;
    public ulong BasePipelineHandle; public int BasePipelineIndex;
}

[StructLayout(LayoutKind.Sequential)]
internal struct VkBufferCopy { public ulong SrcOffset, DstOffset, Size; }

[StructLayout(LayoutKind.Sequential)]
internal struct VkImageSubresourceLayers { public uint AspectMask, MipLevel, BaseArrayLayer, LayerCount; }

[StructLayout(LayoutKind.Sequential)]
internal struct VkOffset3D { public int X, Y, Z; }

[StructLayout(LayoutKind.Sequential)]
internal struct VkBufferImageCopy
{
    public ulong BufferOffset; public uint BufferRowLength; public uint BufferImageHeight;
    public VkImageSubresourceLayers ImageSubresource; public VkOffset3D ImageOffset; public VkExtent3D ImageExtent;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct VkExtensionProperties { public fixed byte ExtensionName[256]; public uint SpecVersion; }

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct VkLayerProperties { public fixed byte LayerName[256]; public uint SpecVersion; public uint ImplementationVersion; public fixed byte Description[256]; }

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct VkDebugUtilsMessengerCreateInfoEXT
{
    public uint SType; public void* PNext; public uint Flags; public uint MessageSeverity; public uint MessageType;
    public delegate* unmanaged<uint, uint, VkDebugUtilsMessengerCallbackDataEXT*, void*, uint> UserCallback; public void* UserData;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct VkDebugUtilsMessengerCallbackDataEXT
{
    public uint SType; public void* PNext; public uint Flags; public byte* MessageIdName; public int MessageIdNumber; public byte* Message;
    public uint QueueLabelCount; public void* QueueLabels; public uint CmdBufLabelCount; public void* CmdBufLabels; public uint ObjectCount; public void* Objects;
}

/// <summary>Offsets into VkPhysicalDeviceProperties (824 bytes) for the fields we read, per vulkan_core.h.</summary>
internal static class VkPhysicalDevicePropertiesLayout
{
    public const int Size = 824, ApiVersion = 0, DeviceType = 16, DeviceName = 20, Limits = 296;
    public const int MaxUniformBufferRange = Limits + 24, MinUniformBufferOffsetAlignment = Limits + 320, MinStorageBufferOffsetAlignment = Limits + 328;
}

// ------------------------------------------------------------------ functions

/// <summary>Vulkan entry points, loaded at runtime (one instance and one device per process).</summary>
internal static unsafe class Vk
{
    private static nint _library;

    public static delegate* unmanaged<nint, byte*, nint> GetInstanceProcAddr;
    public static delegate* unmanaged<VkInstanceCreateInfo*, void*, nint*, VkResult> CreateInstance;
    public static delegate* unmanaged<byte*, uint*, VkExtensionProperties*, VkResult> EnumerateInstanceExtensionProperties;
    public static delegate* unmanaged<uint*, VkLayerProperties*, VkResult> EnumerateInstanceLayerProperties;

    // Instance level.
    public static delegate* unmanaged<nint, void*, void> DestroyInstance;
    public static delegate* unmanaged<nint, uint*, nint*, VkResult> EnumeratePhysicalDevices;
    public static delegate* unmanaged<nint, byte*, void> GetPhysicalDeviceProperties;
    public static delegate* unmanaged<nint, uint*, VkQueueFamilyProperties*, void> GetPhysicalDeviceQueueFamilyProperties;
    public static delegate* unmanaged<nint, VkPhysicalDeviceMemoryProperties*, void> GetPhysicalDeviceMemoryProperties;
    public static delegate* unmanaged<nint, VkPhysicalDeviceFeatures2*, void> GetPhysicalDeviceFeatures2;
    public static delegate* unmanaged<nint, byte*, uint*, VkExtensionProperties*, VkResult> EnumerateDeviceExtensionProperties;
    public static delegate* unmanaged<nint, VkDeviceCreateInfo*, void*, nint*, VkResult> CreateDevice;
    public static delegate* unmanaged<nint, byte*, nint> GetDeviceProcAddr;
    public static delegate* unmanaged<nint, uint, ulong, uint*, VkResult> GetPhysicalDeviceSurfaceSupportKHR;
    public static delegate* unmanaged<nint, ulong, VkSurfaceCapabilitiesKHR*, VkResult> GetPhysicalDeviceSurfaceCapabilitiesKHR;
    public static delegate* unmanaged<nint, ulong, uint*, VkSurfaceFormatKHR*, VkResult> GetPhysicalDeviceSurfaceFormatsKHR;
    public static delegate* unmanaged<nint, ulong, uint*, uint*, VkResult> GetPhysicalDeviceSurfacePresentModesKHR;
    public static delegate* unmanaged<nint, ulong, void*, void> DestroySurfaceKHR;
    public static delegate* unmanaged<nint, VkDebugUtilsMessengerCreateInfoEXT*, void*, ulong*, VkResult> CreateDebugUtilsMessengerEXT;
    public static delegate* unmanaged<nint, ulong, void*, void> DestroyDebugUtilsMessengerEXT;

    // Device level.
    public static delegate* unmanaged<nint, void*, void> DestroyDevice;
    public static delegate* unmanaged<nint, uint, uint, nint*, void> GetDeviceQueue;
    public static delegate* unmanaged<nint, VkResult> DeviceWaitIdle;
    public static delegate* unmanaged<nint, VkSwapchainCreateInfoKHR*, void*, ulong*, VkResult> CreateSwapchainKHR;
    public static delegate* unmanaged<nint, ulong, void*, void> DestroySwapchainKHR;
    public static delegate* unmanaged<nint, ulong, uint*, ulong*, VkResult> GetSwapchainImagesKHR;
    public static delegate* unmanaged<nint, ulong, ulong, ulong, ulong, uint*, VkResult> AcquireNextImageKHR;
    public static delegate* unmanaged<nint, VkPresentInfoKHR*, VkResult> QueuePresentKHR;
    public static delegate* unmanaged<nint, uint, VkSubmitInfo2*, ulong, VkResult> QueueSubmit2;
    public static delegate* unmanaged<nint, VkResult> QueueWaitIdle;
    public static delegate* unmanaged<nint, VkImageViewCreateInfo*, void*, ulong*, VkResult> CreateImageView;
    public static delegate* unmanaged<nint, ulong, void*, void> DestroyImageView;
    public static delegate* unmanaged<nint, VkImageCreateInfo*, void*, ulong*, VkResult> CreateImage;
    public static delegate* unmanaged<nint, ulong, void*, void> DestroyImage;
    public static delegate* unmanaged<nint, ulong, VkMemoryRequirements*, void> GetImageMemoryRequirements;
    public static delegate* unmanaged<nint, ulong, ulong, ulong, VkResult> BindImageMemory;
    public static delegate* unmanaged<nint, VkBufferCreateInfo*, void*, ulong*, VkResult> CreateBuffer;
    public static delegate* unmanaged<nint, ulong, void*, void> DestroyBuffer;
    public static delegate* unmanaged<nint, ulong, VkMemoryRequirements*, void> GetBufferMemoryRequirements;
    public static delegate* unmanaged<nint, ulong, ulong, ulong, VkResult> BindBufferMemory;
    public static delegate* unmanaged<nint, VkMemoryAllocateInfo*, void*, ulong*, VkResult> AllocateMemory;
    public static delegate* unmanaged<nint, ulong, void*, void> FreeMemory;
    public static delegate* unmanaged<nint, ulong, ulong, ulong, uint, void**, VkResult> MapMemory;
    public static delegate* unmanaged<nint, ulong, void> UnmapMemory;
    public static delegate* unmanaged<nint, VkCommandPoolCreateInfo*, void*, ulong*, VkResult> CreateCommandPool;
    public static delegate* unmanaged<nint, ulong, void*, void> DestroyCommandPool;
    public static delegate* unmanaged<nint, ulong, uint, VkResult> ResetCommandPool;
    public static delegate* unmanaged<nint, VkCommandBufferAllocateInfo*, nint*, VkResult> AllocateCommandBuffers;
    public static delegate* unmanaged<nint, ulong, uint, nint*, void> FreeCommandBuffers;
    public static delegate* unmanaged<nint, VkCommandBufferBeginInfo*, VkResult> BeginCommandBuffer;
    public static delegate* unmanaged<nint, VkResult> EndCommandBuffer;
    public static delegate* unmanaged<nint, VkFenceCreateInfo*, void*, ulong*, VkResult> CreateFence;
    public static delegate* unmanaged<nint, ulong, void*, void> DestroyFence;
    public static delegate* unmanaged<nint, ulong, VkResult> GetFenceStatus;
    public static delegate* unmanaged<nint, uint, ulong*, uint, ulong, VkResult> WaitForFences;
    public static delegate* unmanaged<nint, uint, ulong*, VkResult> ResetFences;
    public static delegate* unmanaged<nint, VkSemaphoreCreateInfo*, void*, ulong*, VkResult> CreateSemaphore;
    public static delegate* unmanaged<nint, ulong, void*, void> DestroySemaphore;
    public static delegate* unmanaged<nint, VkSamplerCreateInfo*, void*, ulong*, VkResult> CreateSampler;
    public static delegate* unmanaged<nint, ulong, void*, void> DestroySampler;
    public static delegate* unmanaged<nint, VkShaderModuleCreateInfo*, void*, ulong*, VkResult> CreateShaderModule;
    public static delegate* unmanaged<nint, ulong, void*, void> DestroyShaderModule;
    public static delegate* unmanaged<nint, VkDescriptorSetLayoutCreateInfo*, void*, ulong*, VkResult> CreateDescriptorSetLayout;
    public static delegate* unmanaged<nint, ulong, void*, void> DestroyDescriptorSetLayout;
    public static delegate* unmanaged<nint, VkDescriptorPoolCreateInfo*, void*, ulong*, VkResult> CreateDescriptorPool;
    public static delegate* unmanaged<nint, ulong, void*, void> DestroyDescriptorPool;
    public static delegate* unmanaged<nint, VkDescriptorSetAllocateInfo*, ulong*, VkResult> AllocateDescriptorSets;
    public static delegate* unmanaged<nint, ulong, uint, ulong*, VkResult> FreeDescriptorSets;
    public static delegate* unmanaged<nint, uint, VkWriteDescriptorSet*, uint, void*, void> UpdateDescriptorSets;
    public static delegate* unmanaged<nint, VkPipelineLayoutCreateInfo*, void*, ulong*, VkResult> CreatePipelineLayout;
    public static delegate* unmanaged<nint, ulong, void*, void> DestroyPipelineLayout;
    public static delegate* unmanaged<nint, ulong, uint, VkGraphicsPipelineCreateInfo*, void*, ulong*, VkResult> CreateGraphicsPipelines;
    public static delegate* unmanaged<nint, ulong, void*, void> DestroyPipeline;

    // Commands.
    public static delegate* unmanaged<nint, VkRenderingInfo*, void> CmdBeginRendering;
    public static delegate* unmanaged<nint, void> CmdEndRendering;
    public static delegate* unmanaged<nint, VkDependencyInfo*, void> CmdPipelineBarrier2;
    public static delegate* unmanaged<nint, uint, ulong, void> CmdBindPipeline;
    public static delegate* unmanaged<nint, uint, ulong, uint, uint, ulong*, uint, uint*, void> CmdBindDescriptorSets;
    public static delegate* unmanaged<nint, uint, uint, ulong*, ulong*, void> CmdBindVertexBuffers;
    public static delegate* unmanaged<nint, ulong, ulong, uint, void> CmdBindIndexBuffer;
    public static delegate* unmanaged<nint, uint, uint, VkViewport*, void> CmdSetViewport;
    public static delegate* unmanaged<nint, uint, uint, VkRect2D*, void> CmdSetScissor;
    public static delegate* unmanaged<nint, uint, uint, uint, uint, void> CmdDraw;
    public static delegate* unmanaged<nint, uint, uint, uint, int, uint, void> CmdDrawIndexed;
    public static delegate* unmanaged<nint, ulong, ulong, uint, VkBufferCopy*, void> CmdCopyBuffer;
    public static delegate* unmanaged<nint, ulong, uint, ulong, uint, VkBufferImageCopy*, void> CmdCopyImageToBuffer;
    public static delegate* unmanaged<nint, ulong, ulong, uint, uint, VkBufferImageCopy*, void> CmdCopyBufferToImage;

    public static void LoadLoader()
    {
        if (_library != 0) return;
        string[] names = OperatingSystem.IsWindows() ? ["vulkan-1.dll"]
            : OperatingSystem.IsMacOS() ? ["libvulkan.1.dylib", "libMoltenVK.dylib"]
            : OperatingSystem.IsAndroid() ? ["libvulkan.so"]
            : ["libvulkan.so.1", "libvulkan.so"];
        foreach (var n in names)
            if (NativeLibrary.TryLoad(n, out _library)) break;
        if (_library == 0) throw new InvalidOperationException("No Vulkan loader found (" + string.Join(", ", names) + ").");
        GetInstanceProcAddr = (delegate* unmanaged<nint, byte*, nint>)NativeLibrary.GetExport(_library, "vkGetInstanceProcAddr");
        CreateInstance = (delegate* unmanaged<VkInstanceCreateInfo*, void*, nint*, VkResult>)Instance(0, "vkCreateInstance");
        EnumerateInstanceExtensionProperties = (delegate* unmanaged<byte*, uint*, VkExtensionProperties*, VkResult>)Instance(0, "vkEnumerateInstanceExtensionProperties");
        EnumerateInstanceLayerProperties = (delegate* unmanaged<uint*, VkLayerProperties*, VkResult>)Instance(0, "vkEnumerateInstanceLayerProperties");
    }

    public static void LoadInstance(nint i)
    {
        DestroyInstance = (delegate* unmanaged<nint, void*, void>)Instance(i, "vkDestroyInstance");
        EnumeratePhysicalDevices = (delegate* unmanaged<nint, uint*, nint*, VkResult>)Instance(i, "vkEnumeratePhysicalDevices");
        GetPhysicalDeviceProperties = (delegate* unmanaged<nint, byte*, void>)Instance(i, "vkGetPhysicalDeviceProperties");
        GetPhysicalDeviceQueueFamilyProperties = (delegate* unmanaged<nint, uint*, VkQueueFamilyProperties*, void>)Instance(i, "vkGetPhysicalDeviceQueueFamilyProperties");
        GetPhysicalDeviceMemoryProperties = (delegate* unmanaged<nint, VkPhysicalDeviceMemoryProperties*, void>)Instance(i, "vkGetPhysicalDeviceMemoryProperties");
        GetPhysicalDeviceFeatures2 = (delegate* unmanaged<nint, VkPhysicalDeviceFeatures2*, void>)Instance(i, "vkGetPhysicalDeviceFeatures2");
        EnumerateDeviceExtensionProperties = (delegate* unmanaged<nint, byte*, uint*, VkExtensionProperties*, VkResult>)Instance(i, "vkEnumerateDeviceExtensionProperties");
        CreateDevice = (delegate* unmanaged<nint, VkDeviceCreateInfo*, void*, nint*, VkResult>)Instance(i, "vkCreateDevice");
        GetDeviceProcAddr = (delegate* unmanaged<nint, byte*, nint>)Instance(i, "vkGetDeviceProcAddr");
        GetPhysicalDeviceSurfaceSupportKHR = (delegate* unmanaged<nint, uint, ulong, uint*, VkResult>)Instance(i, "vkGetPhysicalDeviceSurfaceSupportKHR", optional: true);
        GetPhysicalDeviceSurfaceCapabilitiesKHR = (delegate* unmanaged<nint, ulong, VkSurfaceCapabilitiesKHR*, VkResult>)Instance(i, "vkGetPhysicalDeviceSurfaceCapabilitiesKHR", optional: true);
        GetPhysicalDeviceSurfaceFormatsKHR = (delegate* unmanaged<nint, ulong, uint*, VkSurfaceFormatKHR*, VkResult>)Instance(i, "vkGetPhysicalDeviceSurfaceFormatsKHR", optional: true);
        GetPhysicalDeviceSurfacePresentModesKHR = (delegate* unmanaged<nint, ulong, uint*, uint*, VkResult>)Instance(i, "vkGetPhysicalDeviceSurfacePresentModesKHR", optional: true);
        DestroySurfaceKHR = (delegate* unmanaged<nint, ulong, void*, void>)Instance(i, "vkDestroySurfaceKHR", optional: true);
        CreateDebugUtilsMessengerEXT = (delegate* unmanaged<nint, VkDebugUtilsMessengerCreateInfoEXT*, void*, ulong*, VkResult>)Instance(i, "vkCreateDebugUtilsMessengerEXT", optional: true);
        DestroyDebugUtilsMessengerEXT = (delegate* unmanaged<nint, ulong, void*, void>)Instance(i, "vkDestroyDebugUtilsMessengerEXT", optional: true);
    }

    public static void LoadDevice(nint d)
    {
        DestroyDevice = (delegate* unmanaged<nint, void*, void>)Device(d, "vkDestroyDevice");
        GetDeviceQueue = (delegate* unmanaged<nint, uint, uint, nint*, void>)Device(d, "vkGetDeviceQueue");
        DeviceWaitIdle = (delegate* unmanaged<nint, VkResult>)Device(d, "vkDeviceWaitIdle");
        CreateSwapchainKHR = (delegate* unmanaged<nint, VkSwapchainCreateInfoKHR*, void*, ulong*, VkResult>)Device(d, "vkCreateSwapchainKHR", optional: true);
        DestroySwapchainKHR = (delegate* unmanaged<nint, ulong, void*, void>)Device(d, "vkDestroySwapchainKHR", optional: true);
        GetSwapchainImagesKHR = (delegate* unmanaged<nint, ulong, uint*, ulong*, VkResult>)Device(d, "vkGetSwapchainImagesKHR", optional: true);
        AcquireNextImageKHR = (delegate* unmanaged<nint, ulong, ulong, ulong, ulong, uint*, VkResult>)Device(d, "vkAcquireNextImageKHR", optional: true);
        QueuePresentKHR = (delegate* unmanaged<nint, VkPresentInfoKHR*, VkResult>)Device(d, "vkQueuePresentKHR", optional: true);
        QueueSubmit2 = (delegate* unmanaged<nint, uint, VkSubmitInfo2*, ulong, VkResult>)Device(d, "vkQueueSubmit2");
        QueueWaitIdle = (delegate* unmanaged<nint, VkResult>)Device(d, "vkQueueWaitIdle");
        CreateImageView = (delegate* unmanaged<nint, VkImageViewCreateInfo*, void*, ulong*, VkResult>)Device(d, "vkCreateImageView");
        DestroyImageView = (delegate* unmanaged<nint, ulong, void*, void>)Device(d, "vkDestroyImageView");
        CreateImage = (delegate* unmanaged<nint, VkImageCreateInfo*, void*, ulong*, VkResult>)Device(d, "vkCreateImage");
        DestroyImage = (delegate* unmanaged<nint, ulong, void*, void>)Device(d, "vkDestroyImage");
        GetImageMemoryRequirements = (delegate* unmanaged<nint, ulong, VkMemoryRequirements*, void>)Device(d, "vkGetImageMemoryRequirements");
        BindImageMemory = (delegate* unmanaged<nint, ulong, ulong, ulong, VkResult>)Device(d, "vkBindImageMemory");
        CreateBuffer = (delegate* unmanaged<nint, VkBufferCreateInfo*, void*, ulong*, VkResult>)Device(d, "vkCreateBuffer");
        DestroyBuffer = (delegate* unmanaged<nint, ulong, void*, void>)Device(d, "vkDestroyBuffer");
        GetBufferMemoryRequirements = (delegate* unmanaged<nint, ulong, VkMemoryRequirements*, void>)Device(d, "vkGetBufferMemoryRequirements");
        BindBufferMemory = (delegate* unmanaged<nint, ulong, ulong, ulong, VkResult>)Device(d, "vkBindBufferMemory");
        AllocateMemory = (delegate* unmanaged<nint, VkMemoryAllocateInfo*, void*, ulong*, VkResult>)Device(d, "vkAllocateMemory");
        FreeMemory = (delegate* unmanaged<nint, ulong, void*, void>)Device(d, "vkFreeMemory");
        MapMemory = (delegate* unmanaged<nint, ulong, ulong, ulong, uint, void**, VkResult>)Device(d, "vkMapMemory");
        UnmapMemory = (delegate* unmanaged<nint, ulong, void>)Device(d, "vkUnmapMemory");
        CreateCommandPool = (delegate* unmanaged<nint, VkCommandPoolCreateInfo*, void*, ulong*, VkResult>)Device(d, "vkCreateCommandPool");
        DestroyCommandPool = (delegate* unmanaged<nint, ulong, void*, void>)Device(d, "vkDestroyCommandPool");
        ResetCommandPool = (delegate* unmanaged<nint, ulong, uint, VkResult>)Device(d, "vkResetCommandPool");
        AllocateCommandBuffers = (delegate* unmanaged<nint, VkCommandBufferAllocateInfo*, nint*, VkResult>)Device(d, "vkAllocateCommandBuffers");
        FreeCommandBuffers = (delegate* unmanaged<nint, ulong, uint, nint*, void>)Device(d, "vkFreeCommandBuffers");
        BeginCommandBuffer = (delegate* unmanaged<nint, VkCommandBufferBeginInfo*, VkResult>)Device(d, "vkBeginCommandBuffer");
        EndCommandBuffer = (delegate* unmanaged<nint, VkResult>)Device(d, "vkEndCommandBuffer");
        CreateFence = (delegate* unmanaged<nint, VkFenceCreateInfo*, void*, ulong*, VkResult>)Device(d, "vkCreateFence");
        DestroyFence = (delegate* unmanaged<nint, ulong, void*, void>)Device(d, "vkDestroyFence");
        GetFenceStatus = (delegate* unmanaged<nint, ulong, VkResult>)Device(d, "vkGetFenceStatus");
        WaitForFences = (delegate* unmanaged<nint, uint, ulong*, uint, ulong, VkResult>)Device(d, "vkWaitForFences");
        ResetFences = (delegate* unmanaged<nint, uint, ulong*, VkResult>)Device(d, "vkResetFences");
        CreateSemaphore = (delegate* unmanaged<nint, VkSemaphoreCreateInfo*, void*, ulong*, VkResult>)Device(d, "vkCreateSemaphore");
        DestroySemaphore = (delegate* unmanaged<nint, ulong, void*, void>)Device(d, "vkDestroySemaphore");
        CreateSampler = (delegate* unmanaged<nint, VkSamplerCreateInfo*, void*, ulong*, VkResult>)Device(d, "vkCreateSampler");
        DestroySampler = (delegate* unmanaged<nint, ulong, void*, void>)Device(d, "vkDestroySampler");
        CreateShaderModule = (delegate* unmanaged<nint, VkShaderModuleCreateInfo*, void*, ulong*, VkResult>)Device(d, "vkCreateShaderModule");
        DestroyShaderModule = (delegate* unmanaged<nint, ulong, void*, void>)Device(d, "vkDestroyShaderModule");
        CreateDescriptorSetLayout = (delegate* unmanaged<nint, VkDescriptorSetLayoutCreateInfo*, void*, ulong*, VkResult>)Device(d, "vkCreateDescriptorSetLayout");
        DestroyDescriptorSetLayout = (delegate* unmanaged<nint, ulong, void*, void>)Device(d, "vkDestroyDescriptorSetLayout");
        CreateDescriptorPool = (delegate* unmanaged<nint, VkDescriptorPoolCreateInfo*, void*, ulong*, VkResult>)Device(d, "vkCreateDescriptorPool");
        DestroyDescriptorPool = (delegate* unmanaged<nint, ulong, void*, void>)Device(d, "vkDestroyDescriptorPool");
        AllocateDescriptorSets = (delegate* unmanaged<nint, VkDescriptorSetAllocateInfo*, ulong*, VkResult>)Device(d, "vkAllocateDescriptorSets");
        FreeDescriptorSets = (delegate* unmanaged<nint, ulong, uint, ulong*, VkResult>)Device(d, "vkFreeDescriptorSets");
        UpdateDescriptorSets = (delegate* unmanaged<nint, uint, VkWriteDescriptorSet*, uint, void*, void>)Device(d, "vkUpdateDescriptorSets");
        CreatePipelineLayout = (delegate* unmanaged<nint, VkPipelineLayoutCreateInfo*, void*, ulong*, VkResult>)Device(d, "vkCreatePipelineLayout");
        DestroyPipelineLayout = (delegate* unmanaged<nint, ulong, void*, void>)Device(d, "vkDestroyPipelineLayout");
        CreateGraphicsPipelines = (delegate* unmanaged<nint, ulong, uint, VkGraphicsPipelineCreateInfo*, void*, ulong*, VkResult>)Device(d, "vkCreateGraphicsPipelines");
        DestroyPipeline = (delegate* unmanaged<nint, ulong, void*, void>)Device(d, "vkDestroyPipeline");
        CmdBeginRendering = (delegate* unmanaged<nint, VkRenderingInfo*, void>)Device(d, "vkCmdBeginRendering");
        CmdEndRendering = (delegate* unmanaged<nint, void>)Device(d, "vkCmdEndRendering");
        CmdPipelineBarrier2 = (delegate* unmanaged<nint, VkDependencyInfo*, void>)Device(d, "vkCmdPipelineBarrier2");
        CmdBindPipeline = (delegate* unmanaged<nint, uint, ulong, void>)Device(d, "vkCmdBindPipeline");
        CmdBindDescriptorSets = (delegate* unmanaged<nint, uint, ulong, uint, uint, ulong*, uint, uint*, void>)Device(d, "vkCmdBindDescriptorSets");
        CmdBindVertexBuffers = (delegate* unmanaged<nint, uint, uint, ulong*, ulong*, void>)Device(d, "vkCmdBindVertexBuffers");
        CmdBindIndexBuffer = (delegate* unmanaged<nint, ulong, ulong, uint, void>)Device(d, "vkCmdBindIndexBuffer");
        CmdSetViewport = (delegate* unmanaged<nint, uint, uint, VkViewport*, void>)Device(d, "vkCmdSetViewport");
        CmdSetScissor = (delegate* unmanaged<nint, uint, uint, VkRect2D*, void>)Device(d, "vkCmdSetScissor");
        CmdDraw = (delegate* unmanaged<nint, uint, uint, uint, uint, void>)Device(d, "vkCmdDraw");
        CmdDrawIndexed = (delegate* unmanaged<nint, uint, uint, uint, int, uint, void>)Device(d, "vkCmdDrawIndexed");
        CmdCopyBuffer = (delegate* unmanaged<nint, ulong, ulong, uint, VkBufferCopy*, void>)Device(d, "vkCmdCopyBuffer");
        CmdCopyImageToBuffer = (delegate* unmanaged<nint, ulong, uint, ulong, uint, VkBufferImageCopy*, void>)Device(d, "vkCmdCopyImageToBuffer");
        CmdCopyBufferToImage = (delegate* unmanaged<nint, ulong, ulong, uint, uint, VkBufferImageCopy*, void>)Device(d, "vkCmdCopyBufferToImage");
    }

    private static nint Instance(nint instance, string name, bool optional = false)
    {
        nint p;
        fixed (byte* n = Utf8(name)) p = GetInstanceProcAddr(instance, n);
        if (p == 0 && !optional) throw new InvalidOperationException($"Vulkan function {name} not found.");
        return p;
    }

    private static nint Device(nint device, string name, bool optional = false)
    {
        nint p;
        fixed (byte* n = Utf8(name)) p = GetDeviceProcAddr(device, n);
        if (p == 0 && !optional) throw new InvalidOperationException($"Vulkan function {name} not found.");
        return p;
    }

    public static byte[] Utf8(string s)
    {
        var b = new byte[Encoding.UTF8.GetByteCount(s) + 1];
        Encoding.UTF8.GetBytes(s, b);
        return b;
    }

    public static void Check(VkResult r, string what)
    {
        if (r != VkResult.Success) throw new VulkanException(what, r);
    }
}

public sealed class VulkanException : Exception
{
    internal VulkanException(string what, VkResult result) : base($"{what} failed: {result}") => Result = (int)result;
    public int Result { get; }
}
