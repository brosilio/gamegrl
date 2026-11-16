using Gamegrl.Platform;

namespace Gamegrl.Rhi.Vulkan;

/// <summary>Creates the Vulkan <see cref="GpuDevice"/> for the main window. Add after a platform module.</summary>
public sealed class VulkanModule(VulkanOptions? options = null) : IModule
{
    public VulkanModule() : this(null) { }

    public void Build(EngineBuilder builder)
    {
        var window = builder.World.Resource<PlatformContext>().MainWindow;
        var device = new VulkanDevice(window, options);
        builder.SetResource<GpuDevice>(device);
        builder.OnStop(_ => device.Dispose());
    }
}
