namespace Gamegrl.Rhi.Vulkan;

internal static unsafe class Pipelines
{
    public static GraphicsPipeline Create(VulkanDevice device, GraphicsPipelineDesc desc)
    {
        // Layout.
        var setLayouts = stackalloc ulong[desc.BindGroupLayouts.Length];
        for (int i = 0; i < desc.BindGroupLayouts.Length; i++) setLayouts[i] = ((VulkanBindGroupLayout)desc.BindGroupLayouts[i]).Handle;
        var lci = new VkPipelineLayoutCreateInfo
        {
            SType = VkStructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = (uint)desc.BindGroupLayouts.Length,
            SetLayouts = setLayouts,
        };
        ulong layout;
        Vk.Check(Vk.CreatePipelineLayout(device.Handle, &lci, null, &layout), "vkCreatePipelineLayout");

        // Shaders.
        var vs = (VulkanShader)desc.VertexShader;
        var fs = (VulkanShader)desc.FragmentShader;
        fixed (byte* vsEntry = vs.EntryPoint)
        fixed (byte* fsEntry = fs.EntryPoint)
        {
            var stages = stackalloc VkPipelineShaderStageCreateInfo[2];
            stages[0] = new VkPipelineShaderStageCreateInfo { SType = VkStructureType.PipelineShaderStageCreateInfo, Stage = VkConst.ShaderStageVertex, Module = vs.Module, Name = vsEntry };
            stages[1] = new VkPipelineShaderStageCreateInfo { SType = VkStructureType.PipelineShaderStageCreateInfo, Stage = VkConst.ShaderStageFragment, Module = fs.Module, Name = fsEntry };

            // Vertex input.
            int attributeCount = 0;
            foreach (var vb in desc.VertexBuffers) attributeCount += vb.Attributes.Length;
            var bindings = stackalloc VkVertexInputBindingDescription[Math.Max(1, desc.VertexBuffers.Length)];
            var attributes = stackalloc VkVertexInputAttributeDescription[Math.Max(1, attributeCount)];
            int a = 0;
            for (int b = 0; b < desc.VertexBuffers.Length; b++)
            {
                var vb = desc.VertexBuffers[b];
                bindings[b] = new VkVertexInputBindingDescription { Binding = (uint)b, Stride = (uint)vb.Stride, InputRate = vb.Step == VertexStep.Instance ? 1u : 0u };
                foreach (var attr in vb.Attributes)
                    attributes[a++] = new VkVertexInputAttributeDescription { Location = (uint)attr.Location, Binding = (uint)b, Format = Formats.Vertex(attr.Format), Offset = (uint)attr.Offset };
            }
            var vertexInput = new VkPipelineVertexInputStateCreateInfo
            {
                SType = VkStructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount = (uint)desc.VertexBuffers.Length,
                VertexBindingDescriptions = bindings,
                VertexAttributeDescriptionCount = (uint)attributeCount,
                VertexAttributeDescriptions = attributes,
            };

            var inputAssembly = new VkPipelineInputAssemblyStateCreateInfo
            {
                SType = VkStructureType.PipelineInputAssemblyStateCreateInfo,
                Topology = desc.Topology switch { PrimitiveTopology.TriangleStrip => 4u, PrimitiveTopology.LineList => 1u, _ => 3u },
            };
            var viewport = new VkPipelineViewportStateCreateInfo { SType = VkStructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1 };
            var raster = new VkPipelineRasterizationStateCreateInfo
            {
                SType = VkStructureType.PipelineRasterizationStateCreateInfo,
                CullMode = desc.CullMode switch { CullMode.Front => 1u, CullMode.Back => 2u, _ => 0u },
                FrontFace = desc.FrontFace == FrontFace.Clockwise ? 1u : 0u,
                LineWidth = 1,
            };
            var multisample = new VkPipelineMultisampleStateCreateInfo { SType = VkStructureType.PipelineMultisampleStateCreateInfo, RasterizationSamples = VkConst.SampleCount1 };
            var depth = new VkPipelineDepthStencilStateCreateInfo
            {
                SType = VkStructureType.PipelineDepthStencilStateCreateInfo,
                DepthTestEnable = desc.DepthTest ? 1u : 0u,
                DepthWriteEnable = desc.DepthWrite ? 1u : 0u,
                DepthCompareOp = (uint)desc.DepthCompare,
                MaxDepthBounds = 1,
            };

            var blends = stackalloc VkPipelineColorBlendAttachmentState[Math.Max(1, desc.ColorFormats.Length)];
            for (int i = 0; i < desc.ColorFormats.Length; i++) blends[i] = Blend(desc.Blend);
            var colorBlend = new VkPipelineColorBlendStateCreateInfo
            {
                SType = VkStructureType.PipelineColorBlendStateCreateInfo,
                AttachmentCount = (uint)desc.ColorFormats.Length,
                Attachments = blends,
            };

            var dynamicStates = stackalloc uint[] { VkConst.DynamicStateViewport, VkConst.DynamicStateScissor };
            var dynamic = new VkPipelineDynamicStateCreateInfo { SType = VkStructureType.PipelineDynamicStateCreateInfo, DynamicStateCount = 2, DynamicStates = dynamicStates };

            var colorFormats = stackalloc uint[Math.Max(1, desc.ColorFormats.Length)];
            for (int i = 0; i < desc.ColorFormats.Length; i++) colorFormats[i] = Formats.ToVk(desc.ColorFormats[i]);
            var rendering = new VkPipelineRenderingCreateInfo
            {
                SType = VkStructureType.PipelineRenderingCreateInfo,
                ColorAttachmentCount = (uint)desc.ColorFormats.Length,
                ColorAttachmentFormats = colorFormats,
                DepthAttachmentFormat = desc.DepthFormat == TextureFormat.Unknown ? 0 : Formats.ToVk(desc.DepthFormat),
            };

            var ci = new VkGraphicsPipelineCreateInfo
            {
                SType = VkStructureType.GraphicsPipelineCreateInfo,
                PNext = &rendering,
                StageCount = 2,
                Stages = stages,
                VertexInputState = &vertexInput,
                InputAssemblyState = &inputAssembly,
                ViewportState = &viewport,
                RasterizationState = &raster,
                MultisampleState = &multisample,
                DepthStencilState = &depth,
                ColorBlendState = &colorBlend,
                DynamicState = &dynamic,
                Layout = layout,
                BasePipelineIndex = -1,
            };
            ulong pipeline;
            Vk.Check(Vk.CreateGraphicsPipelines(device.Handle, 0, 1, &ci, null, &pipeline), "vkCreateGraphicsPipelines");
            return new VulkanPipeline(device, pipeline, layout);
        }
    }

    // VkBlendFactor: 0 zero, 1 one, 6 src alpha, 7 one minus src alpha. VkBlendOp: 0 add.
    private static VkPipelineColorBlendAttachmentState Blend(BlendMode mode) => mode switch
    {
        BlendMode.Alpha => new() { BlendEnable = 1, SrcColorBlendFactor = 6, DstColorBlendFactor = 7, SrcAlphaBlendFactor = 1, DstAlphaBlendFactor = 7, ColorWriteMask = VkConst.ColorComponentAll },
        BlendMode.Premultiplied => new() { BlendEnable = 1, SrcColorBlendFactor = 1, DstColorBlendFactor = 7, SrcAlphaBlendFactor = 1, DstAlphaBlendFactor = 7, ColorWriteMask = VkConst.ColorComponentAll },
        BlendMode.Additive => new() { BlendEnable = 1, SrcColorBlendFactor = 6, DstColorBlendFactor = 1, SrcAlphaBlendFactor = 1, DstAlphaBlendFactor = 1, ColorWriteMask = VkConst.ColorComponentAll },
        _ => new() { ColorWriteMask = VkConst.ColorComponentAll },
    };
}
