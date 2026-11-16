using System.Runtime.CompilerServices;
using Gamegrl.Rhi.Vulkan;
using static Gamegrl.Tests.Harness;

namespace Gamegrl.Tests;

// Expected sizes come from a C program compiled against vulkan_core.h (sizeof). Regenerate if structs are added.
internal static class VkLayoutTests
{
    public static void Register()
    {
        Test("vulkan: struct sizes match vulkan_core.h", () =>
        {
            Check<VkApplicationInfo>(48);
            Check<VkInstanceCreateInfo>(64);
            Check<VkDeviceQueueCreateInfo>(40);
            Check<VkDeviceCreateInfo>(72);
            Check<VkPhysicalDeviceFeatures2>(240);
            Check<VkPhysicalDeviceVulkan13Features>(80);
            Check<VkQueueFamilyProperties>(24);
            Check<VkPhysicalDeviceMemoryProperties>(520);
            Check<VkMemoryType>(8);
            Check<VkMemoryHeap>(16);
            Check<VkSurfaceCapabilitiesKHR>(52);
            Check<VkSurfaceFormatKHR>(8);
            Check<VkSwapchainCreateInfoKHR>(104);
            Check<VkPresentInfoKHR>(64);
            Check<VkImageViewCreateInfo>(80);
            Check<VkImageCreateInfo>(88);
            Check<VkBufferCreateInfo>(56);
            Check<VkMemoryRequirements>(24);
            Check<VkMemoryAllocateInfo>(32);
            Check<VkCommandPoolCreateInfo>(24);
            Check<VkCommandBufferAllocateInfo>(32);
            Check<VkCommandBufferBeginInfo>(32);
            Check<VkFenceCreateInfo>(24);
            Check<VkSemaphoreCreateInfo>(24);
            Check<VkRenderingInfo>(72);
            Check<VkRenderingAttachmentInfo>(72);
            Check<VkClearValue>(16);
            Check<VkImageMemoryBarrier2>(96);
            Check<VkDependencyInfo>(64);
            Check<VkSubmitInfo2>(64);
            Check<VkSemaphoreSubmitInfo>(48);
            Check<VkCommandBufferSubmitInfo>(32);
            Check<VkImageSubresourceRange>(20);
            Check<VkComponentMapping>(16);
            Check<VkSamplerCreateInfo>(80);
            Check<VkShaderModuleCreateInfo>(40);
            Check<VkDescriptorSetLayoutBinding>(24);
            Check<VkDescriptorSetLayoutCreateInfo>(32);
            Check<VkDescriptorPoolSize>(8);
            Check<VkDescriptorPoolCreateInfo>(40);
            Check<VkDescriptorSetAllocateInfo>(40);
            Check<VkWriteDescriptorSet>(64);
            Check<VkDescriptorBufferInfo>(24);
            Check<VkDescriptorImageInfo>(24);
            Check<VkPipelineLayoutCreateInfo>(48);
            Check<VkPipelineShaderStageCreateInfo>(48);
            Check<VkVertexInputBindingDescription>(12);
            Check<VkVertexInputAttributeDescription>(16);
            Check<VkPipelineVertexInputStateCreateInfo>(48);
            Check<VkPipelineInputAssemblyStateCreateInfo>(32);
            Check<VkPipelineViewportStateCreateInfo>(48);
            Check<VkPipelineRasterizationStateCreateInfo>(64);
            Check<VkPipelineMultisampleStateCreateInfo>(48);
            Check<VkStencilOpState>(28);
            Check<VkPipelineDepthStencilStateCreateInfo>(104);
            Check<VkPipelineColorBlendAttachmentState>(32);
            Check<VkPipelineColorBlendStateCreateInfo>(56);
            Check<VkPipelineDynamicStateCreateInfo>(32);
            Check<VkPipelineRenderingCreateInfo>(40);
            Check<VkGraphicsPipelineCreateInfo>(144);
            Check<VkViewport>(24);
            Check<VkRect2D>(16);
            Check<VkBufferCopy>(24);
            Check<VkBufferImageCopy>(56);
            Check<VkExtensionProperties>(260);
            Check<VkLayerProperties>(520);
            Check<VkDebugUtilsMessengerCreateInfoEXT>(48);
            Check<VkDebugUtilsMessengerCallbackDataEXT>(96);
        });
    }

    private static void Check<T>(int expected) where T : unmanaged
    {
        int actual = Unsafe.SizeOf<T>();
        if (actual != expected) throw new Exception($"{typeof(T).Name}: C# size {actual}, C size {expected}");
    }
}
