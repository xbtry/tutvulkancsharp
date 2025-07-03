using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Maths;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;
using Silk.NET.Windowing;
using Semaphore = Silk.NET.Vulkan.Semaphore;


var app = new VulkanTutorialApp();
app.Run();

struct QueueFamilyIndices
{
    public uint? GraphicsFamily { get; set; }
    public uint? PresentFamily { get; set; }
    public bool IsComplete()
    {
        return GraphicsFamily.HasValue && PresentFamily.HasValue;
    }
}

struct SwapChainSupportDetails
{
    public SurfaceCapabilitiesKHR Capabilities;
    public SurfaceFormatKHR[] Formats;
    public PresentModeKHR[] PresentModes;
}

struct Vertex
{
    public Vector2D<float> pos;
    public Vector3D<float> color;

    public static VertexInputBindingDescription GetBindingDescription()
    {
        VertexInputBindingDescription bindingDescription = new VertexInputBindingDescription()
        {
            Binding = 0,
            Stride = (uint)Unsafe.SizeOf<Vertex>(),
            InputRate = VertexInputRate.Vertex
        };
        return bindingDescription;
    }

    public static VertexInputAttributeDescription[] GetVertexInputAttributeDescription()
    {
        var attributeDescriptions = new[]
        {
            new VertexInputAttributeDescription()
            {
                Binding = 0,
                Location = 0,
                Format = Format.R32G32Sfloat,
                Offset = (uint)Marshal.OffsetOf<Vertex>(nameof(pos)),
            },
            new VertexInputAttributeDescription()
            {
                Binding = 0,
                Location = 1,
                Format = Format.R32G32Sfloat,
                Offset = (uint)Marshal.OffsetOf<Vertex>(nameof(color)),
            }
        };
        
        return attributeDescriptions;
    }
    
}
unsafe class VulkanTutorialApp
{
    private IWindow? _window;
    private IMonitor? _monitor;
    
    private Vk? _vk;
    private Instance _instance;
    private PhysicalDevice _physicalDevice;
    private Device _device;
    private KhrSurface? _khrSurface;
    private SurfaceKHR _surface;
    
    private Queue _graphicsQueue;
    private Queue _presentQueue;
    
    private KhrSwapchain? _khrSwapchain;
    private SwapchainKHR _swapchain;
    private Image[]? _swapChainImages;
    private Format _swapChainImageFormat;
    private Extent2D _swapChainExtent;
    private ImageView[]? _swapChainImageViews;
    private Framebuffer[]? _swapChainFramebuffers;
    
    private RenderPass _renderPass;
    private PipelineLayout _pipelineLayout;
    private Pipeline _graphicsPipeline;

    private CommandPool _graphicsCommandPool;
    private CommandBuffer[]? _graphicsCommandBuffers;

    const int MAX_FRAMES_IN_FLIGHT = 2;
    
    private Semaphore[]? _imageAvailableSemaphores;
    private Semaphore[]? _renderFinishedSemaphores;
    private Fence[]? _inFlightFences;
    private Fence[]? _imagesInFlight;
    private int _currentFrame = 0;

    private bool _frameBufferResized = false;
    
    private Vertex[] vertices = new Vertex[]
    {
        new Vertex { pos = new Vector2D<float>(0.0f,-0.5f), color = new Vector3D<float>(1.0f, 0.0f, 0.0f) },
        new Vertex { pos = new Vector2D<float>(0.5f,0.5f), color = new Vector3D<float>(0.0f, 1.0f, 0.0f) },
        new Vertex { pos = new Vector2D<float>(-0.5f,0.5f), color = new Vector3D<float>(0.0f, 0.0f, 1.0f) },
    };
    
    private ExtDebugUtils? _debugUtils;
    private DebugUtilsMessengerEXT _debugMessenger;
    
    private bool enableValidationLayers = true;

    private readonly string[] validationLayers = new[]
    {
        "VK_LAYER_KHRONOS_validation"
    };
    
    private readonly string[] deviceExtensions = new[]
    {
        KhrSwapchain.ExtensionName
    };
    
    public void Run()
    {
        InitializeWindow();
        InitializeVulkan();
        MainLoop();
        CleanUp();
    }
    private void GetMonitorBounds(out int width, out int height)
    {
        _monitor = Silk.NET.Windowing.Monitor.GetMainMonitor(_window);
        width = _monitor.Bounds.Size.X;
        height = _monitor.Bounds.Size.Y;
    }
    private void InitializeWindow()
    {
        GetMonitorBounds(out var width, out var height);
        var options = WindowOptions.DefaultVulkan with
        {
            Size = new Vector2D<int>(width, height),
            Title = "Vulkan Tutorial"
        };
        _window = Window.Create(options);
        _window.Initialize();
        if (_window.VkSurface is null)
        {
            throw new Exception("Windowing platform doesn't support Vulkan");
        }
    }

    private void InitializeVulkan()
    {
        CreateInstance();
        SetupDebugMessenger();
        CreateSurface();
        PickPhysicalDevice();
        CreateLogicalDevice();
        CreateSwapChain();
        CreateImageViews();
        CreateRenderPass();
        CreateGraphicsPipeline();
        CreateFramebuffers();
        CreateCommandPool();
        CreateCommandBuffers();
        CreateSyncObjects();
    }

    private void MainLoop()
    {
        _window!.Render += DrawFrame;
        _window!.Run();
        _vk!.DeviceWaitIdle(_device);
    }

    private void CleanUpSwapChain()
    {
        foreach (var framebuffer in _swapChainFramebuffers)
        {
            _vk!.DestroyFramebuffer(_device, framebuffer, null);
        }
        
        fixed (CommandBuffer* commandBuffersPtr = _graphicsCommandBuffers)
        {
            _vk!.FreeCommandBuffers(_device, _graphicsCommandPool, (uint)_graphicsCommandBuffers!.Length, commandBuffersPtr);
        }
        
        _vk!.DestroyPipeline(_device, _graphicsPipeline, null);
        _vk!.DestroyPipelineLayout(_device, _pipelineLayout, null);
        _vk!.DestroyRenderPass(_device, _renderPass, null);
        
        foreach (var imageView in _swapChainImageViews)
        {
            _vk!.DestroyImageView(_device, imageView, null);
        }
        
        _khrSwapchain!.DestroySwapchain(_device, _swapchain, null);
    }
    private void CleanUp()
    {
        CleanUpSwapChain();
        
        for (int i = 0; i < MAX_FRAMES_IN_FLIGHT; i++)
        {
            _vk!.DestroySemaphore(_device, _renderFinishedSemaphores[i], null);
            _vk!.DestroySemaphore(_device, _imageAvailableSemaphores[i], null);
            _vk!.DestroyFence(_device, _inFlightFences[i], null);
        }
        
        _vk!.DestroyCommandPool(_device, _graphicsCommandPool, null);
        
        _vk!.DestroyDevice(_device, null);
        
        if (enableValidationLayers)
        {
            _debugUtils!.DestroyDebugUtilsMessenger(_instance, _debugMessenger, null);
        }
        
        _khrSurface!.DestroySurface(_instance, _surface, null);
        _vk!.DestroyInstance(_instance,null);
        _vk!.Dispose();
        
        _window?.Dispose();
    }

    private void CreateInstance()
    {
        _vk = Vk.GetApi();

        if (enableValidationLayers && !CheckValidationLayerSupport())
        {
            throw new Exception("Vulkan validation layers are not supported");
        }
        
        ApplicationInfo appInfo = new ApplicationInfo()
        {
            SType = StructureType.ApplicationInfo, 
            PApplicationName = (byte*)Marshal.StringToHGlobalAnsi("Vulkan Tutorial"), 
            ApplicationVersion = new Version32(1,0,0), 
            PEngineName = (byte*)Marshal.StringToHGlobalAnsi("No Engine"),
            EngineVersion = new Version32(1,0,0),
            ApiVersion = Vk.Version13
        };

        InstanceCreateInfo createInfo = new InstanceCreateInfo()
        {
            SType = StructureType.InstanceCreateInfo,
            PApplicationInfo = &appInfo,
        };

        var extensions = GetRequiredExtensions();
        createInfo.EnabledExtensionCount = (uint)extensions.Length;
        createInfo.PpEnabledExtensionNames = (byte**)SilkMarshal.StringArrayToPtr(extensions);

        if (enableValidationLayers)
        {
            createInfo.EnabledLayerCount = (uint)validationLayers.Length;
            createInfo.PpEnabledLayerNames = (byte**)SilkMarshal.StringArrayToPtr(validationLayers);

            DebugUtilsMessengerCreateInfoEXT debugCreateInfo = new DebugUtilsMessengerCreateInfoEXT();
            PopulateDebugMessengerCreateInfo(ref debugCreateInfo);
            createInfo.PNext = &debugCreateInfo;
        }
        else
        {
            createInfo.EnabledLayerCount = 0;
            createInfo.PNext = null;
        }

        if (_vk.CreateInstance(in createInfo, null, out _instance) != Result.Success)
        {
            throw new Exception("Failed to create instance");
        }
        
        Marshal.FreeHGlobal((IntPtr)appInfo.PApplicationName);
        Marshal.FreeHGlobal((IntPtr)appInfo.PEngineName);
        SilkMarshal.Free((nint)createInfo.PpEnabledExtensionNames);

        if (enableValidationLayers)
        {
            SilkMarshal.Free((nint)createInfo.PpEnabledLayerNames);
        }
    }

    private void CreateSurface()
    {
        if (!_vk!.TryGetInstanceExtension<KhrSurface>(_instance, out _khrSurface))
        {
            throw new NotSupportedException("KhrSurface is not supported");
        }

        _surface = _window!.VkSurface!.Create<AllocationCallbacks>(_instance.ToHandle(), null).ToSurface();
    }
    private void PickPhysicalDevice()
    {
        var devices = _vk!.GetPhysicalDevices(_instance);
        foreach (var device in devices)
        {
            _vk!.GetPhysicalDeviceProperties(device, out var properties);
            Console.WriteLine($"GPU Name: {Marshal.PtrToStringAnsi((IntPtr)properties.DeviceName)}");
            if (IsDeviceSuitable(device))
            {
                Console.WriteLine($"Selected GPU: {Marshal.PtrToStringAnsi((IntPtr)properties.DeviceName)}");
                _physicalDevice = device;
                break;
            }
        }

        if (_physicalDevice.Handle == 0)
        {
            throw new Exception("Failed to find Vulkan supported GPU.");
        }
    }

    private void CreateLogicalDevice()
    {
        var indices = FindQueueFamilies(_physicalDevice);

        var uniqueQueueFamilies = new[] { indices.GraphicsFamily!.Value, indices.PresentFamily!.Value };
        uniqueQueueFamilies = uniqueQueueFamilies.Distinct().ToArray();
        
        using var mem = GlobalMemory.Allocate(uniqueQueueFamilies.Length * sizeof(DeviceQueueCreateInfo));
        var queueCreateInfos = (DeviceQueueCreateInfo*)Unsafe.AsPointer(ref mem.GetPinnableReference());


        float queuePriority = 1.0f;
        for (int i = 0; i < uniqueQueueFamilies.Length; i++)
        {
            queueCreateInfos[i] = new DeviceQueueCreateInfo()
            {
                SType = StructureType.DeviceQueueCreateInfo,
                QueueFamilyIndex = uniqueQueueFamilies[i],
                QueueCount = 1,
                PQueuePriorities = &queuePriority
            };
        }

        PhysicalDeviceFeatures deviceFeatures = new PhysicalDeviceFeatures();

        DeviceCreateInfo createInfo = new DeviceCreateInfo()
        {
            SType = StructureType.DeviceCreateInfo,
            QueueCreateInfoCount = (uint)uniqueQueueFamilies.Length,
            PQueueCreateInfos = queueCreateInfos,
            PEnabledFeatures = &deviceFeatures,
            EnabledExtensionCount = (uint)deviceExtensions.Length,
            PpEnabledExtensionNames = (byte**)SilkMarshal.StringArrayToPtr(deviceExtensions)
        };
        
        if (enableValidationLayers)
        {
            createInfo.EnabledLayerCount = (uint)validationLayers.Length;
            createInfo.PpEnabledLayerNames = (byte**)SilkMarshal.StringArrayToPtr(validationLayers);
        }
        else
        {
            createInfo.EnabledLayerCount = 0;
        }

        if (_vk!.CreateDevice(_physicalDevice, in createInfo, null, out _device) != Result.Success)
        {
            throw new Exception("Failed to create logical device");
        }

        _vk!.GetDeviceQueue(_device, indices.GraphicsFamily!.Value, 0, out _graphicsQueue);
        _vk!.GetDeviceQueue(_device, indices.PresentFamily!.Value, 0, out _presentQueue);
        if (enableValidationLayers)
        {
            SilkMarshal.Free((nint)createInfo.PpEnabledLayerNames);
        }
    }
    
    private bool IsDeviceSuitable(PhysicalDevice device)
    {
        var indices = FindQueueFamilies(device);
        bool extensionsSupported = CheckDeviceExtensionsSupport(device);

        bool swapChainAdequate = false;
        if (extensionsSupported)
        {
            var swapChainSupport = QuerySwapChainSupport(device);
            swapChainAdequate = swapChainSupport.Formats.Any() && swapChainSupport.PresentModes.Any();
        }

        return indices.IsComplete() && extensionsSupported && swapChainAdequate;
    }

    private bool CheckDeviceExtensionsSupport(PhysicalDevice device)
    {
        uint extensionsCount = 0;
        _vk!.EnumerateDeviceExtensionProperties(device, (byte*)null, ref extensionsCount, null);

        var availableExtensions = new ExtensionProperties[extensionsCount];
        fixed (ExtensionProperties* pAvailableExtensions = availableExtensions)
        {
            _vk!.EnumerateDeviceExtensionProperties(device, (byte*)null, ref extensionsCount, pAvailableExtensions);
        }

        var availableExtensionNames =
            availableExtensions.Select(extension => Marshal.PtrToStringAnsi((IntPtr)extension.ExtensionName));
        return deviceExtensions.All(availableExtensionNames.Contains);
    }
    
    private QueueFamilyIndices FindQueueFamilies(PhysicalDevice device)
    {
        var indices = new QueueFamilyIndices();
        uint queueFamilyCount = 0;
        _vk!.GetPhysicalDeviceQueueFamilyProperties(device, ref queueFamilyCount, null);
        var queueFamilies = new QueueFamilyProperties[queueFamilyCount];
        fixed (QueueFamilyProperties* pQueueFamilies = queueFamilies)
        {
            _vk!.GetPhysicalDeviceQueueFamilyProperties(device, ref queueFamilyCount, pQueueFamilies);
        }

        uint i = 0;
        foreach (var queueFamily in queueFamilies)
        {
            if (queueFamily.QueueFlags.HasFlag(QueueFlags.GraphicsBit))
            {
                indices.GraphicsFamily = i;
            }

            _khrSurface!.GetPhysicalDeviceSurfaceSupport(device, i, _surface, out var presentSupport);

            if (presentSupport)
            {
                indices.PresentFamily = i;
            }
            
            if (indices.IsComplete())
            {
                break;
            }

            i++;
        }

        return indices;
    }

    private void RecreateSwapChain()
    {
        Vector2D<int> frameBufferSize = _window!.FramebufferSize;

        while (frameBufferSize.X == 0 || frameBufferSize.Y == 0)
        {
            frameBufferSize = _window!.FramebufferSize;
            _window.DoEvents();
        }

        _vk!.DeviceWaitIdle(_device);
        
        CleanUpSwapChain();
        CreateSwapChain();
        CreateImageViews();
        CreateRenderPass();
        CreateGraphicsPipeline();
        CreateFramebuffers();
        CreateCommandBuffers();
        
        _imagesInFlight = new Fence[_swapChainImages!.Length];
    }
    private void CreateSwapChain()
    {
        var swapChainSupport = QuerySwapChainSupport(_physicalDevice);
        var surfaceFormat = ChooseSwapSurfaceFormat(swapChainSupport.Formats);
        var presentMode = ChoosePresentMode(swapChainSupport.PresentModes);
        var extent = ChooseSwapExtent(swapChainSupport.Capabilities);

        var imageCount = swapChainSupport.Capabilities.MinImageCount + 1;
        if (swapChainSupport.Capabilities.MaxImageCount > 0 && imageCount > swapChainSupport.Capabilities.MaxImageCount)
        {
            imageCount = swapChainSupport.Capabilities.MaxImageCount;
        }

        SwapchainCreateInfoKHR createInfo = new SwapchainCreateInfoKHR()
        {
            SType = StructureType.SwapchainCreateInfoKhr,
            Surface = _surface,
            MinImageCount = imageCount,
            ImageFormat = surfaceFormat.Format,
            ImageColorSpace = surfaceFormat.ColorSpace,
            ImageExtent = extent,
            ImageArrayLayers = 1,
            ImageUsage = ImageUsageFlags.ColorAttachmentBit
        };

        var indices = FindQueueFamilies(_physicalDevice);
        var queueFamilyIndices = stackalloc[] { indices.GraphicsFamily!.Value, indices.PresentFamily!.Value };

        if (indices.GraphicsFamily != indices.PresentFamily)
        {
            createInfo = createInfo with
            {
                ImageSharingMode = SharingMode.Concurrent,
                QueueFamilyIndexCount = 2,
                PQueueFamilyIndices = queueFamilyIndices
            };
        }
        else
        {
            createInfo.ImageSharingMode = SharingMode.Exclusive;
        }

        createInfo = createInfo with
        {
            PreTransform = swapChainSupport.Capabilities.CurrentTransform,
            CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr,
            PresentMode = presentMode,
            Clipped = true,

            OldSwapchain = default
        };

        if (!_vk!.TryGetDeviceExtension(_instance, _device, out _khrSwapchain))
        {
            throw new NotSupportedException("Failed to get Vulkan Swapchain Extension.");
        }

        if (_khrSwapchain!.CreateSwapchain(_device, in createInfo, null, out _swapchain) != Result.Success)
        {
            throw new Exception("Failed to create swapchain");
        }

        _khrSwapchain.GetSwapchainImages(_device, _swapchain, ref imageCount, null);
        _swapChainImages = new Image[imageCount];
        fixed (Image* swapChainImagesPtr = _swapChainImages)
        {
            _khrSwapchain.GetSwapchainImages(_device, _swapchain, ref imageCount, swapChainImagesPtr);
        }

        _swapChainImageFormat = surfaceFormat.Format;
        _swapChainExtent = extent;
    }

    private SurfaceFormatKHR ChooseSwapSurfaceFormat(IReadOnlyList<SurfaceFormatKHR> availableFormats)
    {
        foreach (var availableFormat in availableFormats)
        {
            if (availableFormat.Format == Format.B8G8R8A8Srgb &&
                availableFormat.ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr)
            {
                return availableFormat;
            }
        }

        return availableFormats[0];
    }

    private PresentModeKHR ChoosePresentMode(IReadOnlyList<PresentModeKHR> availablePresentModes)
    {
        foreach (var availablePresentMode in availablePresentModes)
        {
            if (availablePresentMode == PresentModeKHR.MailboxKhr)
            {
                return availablePresentMode;
            }
        }

        return PresentModeKHR.FifoKhr;
    }

    private Extent2D ChooseSwapExtent(SurfaceCapabilitiesKHR capabilities)
    {
        if (capabilities.CurrentExtent.Width != uint.MaxValue)
        {
            return capabilities.CurrentExtent;
        }
        else
        {
            var frameBufferSize = _window!.FramebufferSize;

            Extent2D actualExtent = new Extent2D()
            {
                Width = (uint)frameBufferSize.X,
                Height = (uint)frameBufferSize.Y
            };
            
            actualExtent.Width = Math.Clamp(actualExtent.Width, capabilities.MinImageExtent.Width, capabilities.MaxImageExtent.Width);
            actualExtent.Height = Math.Clamp(actualExtent.Height, capabilities.MinImageExtent.Height, capabilities.MaxImageExtent.Height);
            
            return actualExtent;
        }
    }

    private SwapChainSupportDetails QuerySwapChainSupport(PhysicalDevice device)
    {
        var details = new SwapChainSupportDetails();

        _khrSurface!.GetPhysicalDeviceSurfaceCapabilities(device, _surface, out details.Capabilities);

        uint formatCount = 0;
        _khrSurface.GetPhysicalDeviceSurfaceFormats(device, _surface, ref formatCount, null);

        if (formatCount != 0)
        {
            details.Formats = new SurfaceFormatKHR[formatCount];
            fixed (SurfaceFormatKHR* formatsPtr = details.Formats)
            {
                _khrSurface.GetPhysicalDeviceSurfaceFormats(device, _surface, ref formatCount, formatsPtr);
            }
        }
        else
        {
            details.Formats = Array.Empty<SurfaceFormatKHR>();
        }

        uint presentModeCount = 0;
        _khrSurface.GetPhysicalDeviceSurfacePresentModes(device, _surface, ref presentModeCount, null);

        if (presentModeCount != 0)
        {
            details.PresentModes = new PresentModeKHR[presentModeCount];
            fixed (PresentModeKHR* presentModesPtr = details.PresentModes)
            {
                _khrSurface.GetPhysicalDeviceSurfacePresentModes(device,  _surface, ref presentModeCount, presentModesPtr);
            }
        }
        else
        {
            details.PresentModes = Array.Empty<PresentModeKHR>();
        }
        
        return details;
    }

    private void CreateImageViews()
    {
        _swapChainImageViews = new ImageView[_swapChainImages!.Length];

        for (int i = 0; i < _swapChainImages!.Length; i++)
        {
            ImageViewCreateInfo createInfo = new ImageViewCreateInfo()
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = _swapChainImages[i],
                ViewType = ImageViewType.Type2D,
                Format = _swapChainImageFormat,
                Components =
                {
                    R = ComponentSwizzle.R,
                    G = ComponentSwizzle.G,
                    B = ComponentSwizzle.B,
                    A = ComponentSwizzle.A,
                },
                SubresourceRange =
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1,
                }
            };

            if (_vk!.CreateImageView(_device, in createInfo, null, out _swapChainImageViews[i]) != Result.Success)
            {
                throw new Exception($"Failed to create image view {_swapChainImageViews[i]}");
            }
        }
    }

    private void CreateRenderPass()
    {
        AttachmentDescription colorAttachment = new AttachmentDescription()
        {
            Format = _swapChainImageFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.PresentSrcKhr
        };

        AttachmentReference colorAttachmentReference = new AttachmentReference()
        {
            Attachment = 0,
            Layout = ImageLayout.ColorAttachmentOptimal
        };

        SubpassDescription subpassDescription = new SubpassDescription()
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorAttachmentReference
        };

        RenderPassCreateInfo renderPassInfo = new RenderPassCreateInfo()
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 1,
            PAttachments = &colorAttachment,
            SubpassCount = 1,
            PSubpasses = &subpassDescription,
        };

        if (_vk!.CreateRenderPass(_device, in renderPassInfo, null, out _renderPass) != Result.Success)
        {
            throw new Exception($"Failed to create render pass");
        }
    }
    private void CreateGraphicsPipeline()
    {
        string projectDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));;
        if (!File.Exists(Path.Combine(projectDir, "vshader.spv")) &&
            !File.Exists(Path.Combine(projectDir, "fshader.spv")))
        {
            ShaderCompiler.CompileShaders(Path.Combine(projectDir,"vshader.vert"),Path.Combine(projectDir,"fshader.frag"));
        }
        var vertShaderCode = File.ReadAllBytes(Path.Combine(projectDir,"vshader.spv"));
        var fragShaderCode = File.ReadAllBytes(Path.Combine(projectDir,"fshader.spv"));
        
        var vertShaderModule = CreateShaderModule(vertShaderCode);
        var fragShaderModule = CreateShaderModule(fragShaderCode);

        PipelineShaderStageCreateInfo vertShaderStageInfo = new PipelineShaderStageCreateInfo()
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.VertexBit,
            Module = vertShaderModule,
            PName = (byte*)SilkMarshal.StringToPtr("main")
        };
        
        PipelineShaderStageCreateInfo fragShaderStageInfo = new PipelineShaderStageCreateInfo()
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.FragmentBit,
            Module = fragShaderModule,
            PName = (byte*)SilkMarshal.StringToPtr("main")
        };

        var shaderStages = stackalloc[]
        {
            vertShaderStageInfo,
            fragShaderStageInfo
        };

        var bindingDescription = Vertex.GetBindingDescription();
        var attributeDescriptions = Vertex.GetVertexInputAttributeDescription();

        fixed (VertexInputAttributeDescription* attributeDescriptionPtr = attributeDescriptions)
        {
            PipelineVertexInputStateCreateInfo vertexInputStateInfo = new PipelineVertexInputStateCreateInfo()
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount = 1,
                VertexAttributeDescriptionCount = (uint)attributeDescriptions.Length,
                PVertexBindingDescriptions = &bindingDescription,
                PVertexAttributeDescriptions = attributeDescriptionPtr,
            };
            
            PipelineInputAssemblyStateCreateInfo inputAssemblyInfo = new()
            {
                SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                Topology = PrimitiveTopology.TriangleList,
                PrimitiveRestartEnable = false,
            };
            
            Viewport viewport = new Viewport()
            {
                X = 0,
                Y = 0,
                Width = _swapChainExtent.Width,
                Height = _swapChainExtent.Height,
                MinDepth = 0,
                MaxDepth = 1
            };

            Rect2D scissor = new Rect2D()
            {
                Offset = { X = 0, Y = 0 },
                Extent = _swapChainExtent
            };

            PipelineViewportStateCreateInfo viewportStateInfo = new PipelineViewportStateCreateInfo()
            {
                SType = StructureType.PipelineViewportStateCreateInfo,
                ViewportCount = 1,
                PViewports = &viewport,
                ScissorCount = 1,
                PScissors = &scissor,
            };

            PipelineRasterizationStateCreateInfo rasterizationStateInfo = new PipelineRasterizationStateCreateInfo()
            {
                SType = StructureType.PipelineRasterizationStateCreateInfo,
                DepthClampEnable = false,
                RasterizerDiscardEnable = false,
                PolygonMode = PolygonMode.Fill,
                LineWidth = 1,
                CullMode = CullModeFlags.BackBit,
                FrontFace = FrontFace.Clockwise,
                DepthBiasEnable = false
            };

            PipelineMultisampleStateCreateInfo multisampleStateInfo = new PipelineMultisampleStateCreateInfo()
            {
                SType = StructureType.PipelineMultisampleStateCreateInfo,
                SampleShadingEnable = false,
                RasterizationSamples = SampleCountFlags.Count1Bit
            };

            PipelineColorBlendAttachmentState colorBlendAttachmentState = new PipelineColorBlendAttachmentState()
            {
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit |
                                 ColorComponentFlags.ABit,
                BlendEnable = false
            };

            PipelineColorBlendStateCreateInfo colorBlendStateInfo = new PipelineColorBlendStateCreateInfo()
            {
                SType = StructureType.PipelineColorBlendStateCreateInfo,
                LogicOp = LogicOp.Copy,
                LogicOpEnable = false,
                AttachmentCount = 1,
                PAttachments = &colorBlendAttachmentState,
            };

            colorBlendStateInfo.BlendConstants[0] = 0;
            colorBlendStateInfo.BlendConstants[1] = 0;
            colorBlendStateInfo.BlendConstants[2] = 0;
            colorBlendStateInfo.BlendConstants[3] = 0;

            PipelineLayoutCreateInfo pipelineLayoutCreateInfo = new PipelineLayoutCreateInfo()
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 0,
                PushConstantRangeCount = 0
            };

            if (_vk!.CreatePipelineLayout(_device, in pipelineLayoutCreateInfo, null, out _pipelineLayout) !=
                Result.Success)
            {
                throw new Exception($"Failed to create pipeline layout");
            }

            GraphicsPipelineCreateInfo graphicsPipelineCreateInfo = new GraphicsPipelineCreateInfo()
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                StageCount = 2,
                PStages = shaderStages,
                PVertexInputState = &vertexInputStateInfo,
                PInputAssemblyState = &inputAssemblyInfo,
                PViewportState = &viewportStateInfo,
                PRasterizationState = &rasterizationStateInfo,
                PMultisampleState = &multisampleStateInfo,
                PColorBlendState = &colorBlendStateInfo,
                Layout = _pipelineLayout,
                RenderPass = _renderPass,
                Subpass = 0,
                BasePipelineHandle = default
            };

            if (_vk!.CreateGraphicsPipelines(_device, default, 1, in graphicsPipelineCreateInfo, null,
                    out _graphicsPipeline) != Result.Success)
            {
                throw new Exception($"Failed to create graphics pipelines");
            }
        
        }
        _vk!.DestroyShaderModule(_device, fragShaderModule, null);
        _vk!.DestroyShaderModule(_device, vertShaderModule, null);

        SilkMarshal.Free((nint)vertShaderStageInfo.PName);
        SilkMarshal.Free((nint)fragShaderStageInfo.PName); 
    }

    private void CreateFramebuffers()
    {
        _swapChainFramebuffers = new Framebuffer[_swapChainImageViews!.Length];

        for (int i = 0; i < _swapChainImageViews!.Length; i++)
        {
            var attachment =  _swapChainImageViews[i];
            FramebufferCreateInfo frameBufferCreateInfo = new FramebufferCreateInfo()
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = _renderPass,
                AttachmentCount = 1,
                PAttachments = &attachment,
                Width = _swapChainExtent.Width,
                Height = _swapChainExtent.Height,
                Layers = 1
            };

            if (_vk!.CreateFramebuffer(_device, in frameBufferCreateInfo, null, out _swapChainFramebuffers[i]) !=
                Result.Success)
            {
                throw new Exception($"Failed to create framebuffer");
            }
        }
    }

    private void CreateCommandPool()
    {
        var queueFamilyIndices = FindQueueFamilies(_physicalDevice);
        CommandPoolCreateInfo poolCreateInfo = new CommandPoolCreateInfo()
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = queueFamilyIndices.GraphicsFamily!.Value
        };

        if (_vk!.CreateCommandPool(_device, in poolCreateInfo, null, out _graphicsCommandPool) != Result.Success)
        {
            throw new Exception($"Failed to create command pool");
        }
    }
    private void CreateCommandBuffers()
    {
        _graphicsCommandBuffers = new CommandBuffer[_swapChainFramebuffers!.Length];

        CommandBufferAllocateInfo commandBufferAllocateInfo = new CommandBufferAllocateInfo()
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _graphicsCommandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = (uint)_graphicsCommandBuffers.Length
        };

        fixed (CommandBuffer* commandBuffersPtr = _graphicsCommandBuffers)
        {
            if (_vk!.AllocateCommandBuffers(_device, in commandBufferAllocateInfo, commandBuffersPtr) != Result.Success)
            {
                throw new Exception($"Failed to create command buffers");
            }
        }

        for (int i = 0; i < _graphicsCommandBuffers.Length; i++)
        {
            CommandBufferBeginInfo commandBufferBeginInfo = new CommandBufferBeginInfo()
            {
                SType = StructureType.CommandBufferBeginInfo,
            };

            if (_vk!.BeginCommandBuffer(_graphicsCommandBuffers[i], in commandBufferBeginInfo) != Result.Success)
            {
                throw new Exception($"Failed to begin commandbuffer");
            }

            RenderPassBeginInfo renderPassBeginInfo = new RenderPassBeginInfo()
            {
                SType = StructureType.RenderPassBeginInfo,
                RenderPass = _renderPass,
                Framebuffer = _swapChainFramebuffers[i],
                RenderArea =
                {
                    Offset = { X = 0, Y = 0 },
                    Extent = _swapChainExtent
                }
            };

            ClearValue clearColor = new ClearValue()
            {
                Color = new ClearColorValue() { Float32_0 = 0, Float32_1 = 0, Float32_2 = 0, Float32_3 = 1 }
            };

            renderPassBeginInfo.ClearValueCount = 1;
            renderPassBeginInfo.PClearValues = &clearColor;
            
            _vk!.CmdBeginRenderPass(_graphicsCommandBuffers[i], renderPassBeginInfo, SubpassContents.Inline);
            
            _vk!.CmdBindPipeline(_graphicsCommandBuffers[i], PipelineBindPoint.Graphics, _graphicsPipeline);
            
            _vk!.CmdDraw(_graphicsCommandBuffers[i], 3, 1, 0 , 0);
            
            _vk!.CmdEndRenderPass(_graphicsCommandBuffers[i]);

            if (_vk!.EndCommandBuffer(_graphicsCommandBuffers[i]) != Result.Success)
            {
                throw new Exception($"Failed to end commandbuffer");
            }
        }
    }

    private void CreateSyncObjects()
    {
        _imageAvailableSemaphores = new Semaphore[MAX_FRAMES_IN_FLIGHT];
        _renderFinishedSemaphores = new Semaphore[MAX_FRAMES_IN_FLIGHT];
        _inFlightFences = new Fence[MAX_FRAMES_IN_FLIGHT];
        _imagesInFlight = new Fence[_swapChainImages!.Length];

        SemaphoreCreateInfo semaphoreCreateInfo = new SemaphoreCreateInfo()
        {
            SType = StructureType.SemaphoreCreateInfo,
        };

        FenceCreateInfo fenceCreateInfo = new FenceCreateInfo()
        {
            SType = StructureType.FenceCreateInfo,
            Flags = FenceCreateFlags.SignaledBit
        };

        for (int i = 0; i < MAX_FRAMES_IN_FLIGHT; i++)
        {
            if (_vk!.CreateSemaphore(_device, in semaphoreCreateInfo, null, out _imageAvailableSemaphores[i]) !=
                Result.Success ||
                _vk!.CreateSemaphore(_device, in semaphoreCreateInfo, null, out _renderFinishedSemaphores[i]) !=
                Result.Success ||
                _vk!.CreateFence(_device, in fenceCreateInfo, null, out _inFlightFences[i]) != Result.Success
               )
            {
                throw new Exception($"Failed to create sync objects.");
            }
        }
    }

    private void DrawFrame(double delta)
    {
        _vk!.WaitForFences(_device, 1, in _inFlightFences![_currentFrame], true, ulong.MaxValue);
        uint imageIndex = 0;
        var result = _khrSwapchain.AcquireNextImage(
            _device, _swapchain,
            ulong.MaxValue,
            _imageAvailableSemaphores![_currentFrame],
            default,
            ref imageIndex);

        if (result == Result.ErrorOutOfDateKhr)
        {
            RecreateSwapChain();
            return;
        }
        else if (result != Result.Success && result != Result.SuboptimalKhr)
        {
            throw new Exception($"Failed to acquire swap chain image");
        }
        
        if (_imagesInFlight![imageIndex].Handle != default)
        {
            _vk!.WaitForFences(_device, 1, in _imagesInFlight![imageIndex], true, ulong.MaxValue);
        }

        _imagesInFlight[imageIndex] = _inFlightFences[_currentFrame];
        
        SubmitInfo submitInfo = new SubmitInfo()
        {
            SType = StructureType.SubmitInfo,
        };

        var waitSemaphores = stackalloc[] { _imageAvailableSemaphores[_currentFrame] };
        var waitStages = stackalloc[] { PipelineStageFlags.ColorAttachmentOutputBit };

        var buffer = _graphicsCommandBuffers![imageIndex];

        submitInfo = submitInfo with
        {
            WaitSemaphoreCount = 1,
            PWaitSemaphores = waitSemaphores,
            PWaitDstStageMask = waitStages,

            CommandBufferCount = 1,
            PCommandBuffers = &buffer
        };
        
        var signalSemaphores = stackalloc[] {_renderFinishedSemaphores![_currentFrame]};
        submitInfo = submitInfo with
        {
            SignalSemaphoreCount = 1,
            PSignalSemaphores = signalSemaphores,
        };
        
        _vk!.ResetFences(_device, 1, in _inFlightFences[_currentFrame]);

        if (_vk!.QueueSubmit(_graphicsQueue, 1, in submitInfo, _inFlightFences[_currentFrame]) != Result.Success)
        {
            throw new Exception($"Failed to submit graphics queue.");
        }

        var swapChains = stackalloc[] { _swapchain };
        PresentInfoKHR presentInfo = new PresentInfoKHR()
        {
            SType = StructureType.PresentInfoKhr,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = signalSemaphores,
            SwapchainCount = 1,
            PSwapchains = swapChains,
            PImageIndices = &imageIndex
        };
        
        result = _khrSwapchain.QueuePresent(_presentQueue, in presentInfo);
        if (result == Result.ErrorOutOfDateKhr || result == Result.SuboptimalKhr || _frameBufferResized)
        {
            _frameBufferResized = false;
            RecreateSwapChain();
        }
        else if (result != Result.Success)
        {
            throw new Exception("failed to present swap chain image!");
        }
        
        _currentFrame = (_currentFrame + 1) % MAX_FRAMES_IN_FLIGHT;
    }
    
    private ShaderModule CreateShaderModule(byte[] code)
    {
        ShaderModuleCreateInfo createInfo = new ShaderModuleCreateInfo()
        {
            SType = StructureType.ShaderModuleCreateInfo,
            CodeSize = (nuint)code.Length,
        };

        ShaderModule shaderModule;

        fixed (byte* codePtr = code)
        {
            createInfo.PCode = (uint*)codePtr;
            if (_vk!.CreateShaderModule(_device, createInfo, null, out shaderModule) != Result.Success)
            {
                throw new Exception($"Failed to create shader module {createInfo}");
            }
        }

        return shaderModule;
    }
    private bool CheckValidationLayerSupport()
    {
        uint layerCount = 0;
        _vk!.EnumerateInstanceLayerProperties(ref layerCount, null);
        var availableLayers = new LayerProperties[layerCount];
        fixed (LayerProperties* availableLayersPtr = availableLayers)
        {
            _vk!.EnumerateInstanceLayerProperties(ref layerCount, availableLayersPtr);
        }

        var availableLayerNames = availableLayers.Select(layer => Marshal.PtrToStringAnsi((IntPtr)layer.LayerName))
            .ToHashSet();
        return validationLayers.All(availableLayerNames.Contains);
    }

    private string[] GetRequiredExtensions()
    {
        var glfwExtensions = _window!.VkSurface!.GetRequiredExtensions(out var glfwExtensionCount);
        var extensions = SilkMarshal.PtrToStringArray((nint)glfwExtensions,(int) glfwExtensionCount);
        if (enableValidationLayers)
        {
            return extensions.Append(ExtDebugUtils.ExtensionName).ToArray();
        }
        return extensions;
    }

    private void SetupDebugMessenger()
    {
        if (!enableValidationLayers) return;

        if (!_vk!.TryGetInstanceExtension(_instance, out _debugUtils)) return;

        DebugUtilsMessengerCreateInfoEXT createInfo = new DebugUtilsMessengerCreateInfoEXT();
        PopulateDebugMessengerCreateInfo(ref createInfo);

        if (_debugUtils!.CreateDebugUtilsMessenger(_instance, in createInfo, null, out _debugMessenger) !=
            Result.Success)
        {
            throw new Exception("Failed to create debug messenger");
        }
    }
    
    private void PopulateDebugMessengerCreateInfo(ref DebugUtilsMessengerCreateInfoEXT createInfo)
    {
        createInfo.SType = StructureType.DebugUtilsMessengerCreateInfoExt;
        createInfo.MessageSeverity = DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt |
                                     DebugUtilsMessageSeverityFlagsEXT.WarningBitExt |
                                     DebugUtilsMessageSeverityFlagsEXT.VerboseBitExt;
        createInfo.MessageType = DebugUtilsMessageTypeFlagsEXT.GeneralBitExt |
                                 DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt |
                                 DebugUtilsMessageTypeFlagsEXT.ValidationBitExt;
        createInfo.PfnUserCallback = (DebugUtilsMessengerCallbackFunctionEXT)DebugCallback;
    }

    private uint DebugCallback(
        DebugUtilsMessageSeverityFlagsEXT messageSeverity,
        DebugUtilsMessageTypeFlagsEXT messageType,
        DebugUtilsMessengerCallbackDataEXT* pCallbackData,
        void* pUserData
    )
    {
        Console.WriteLine($"Validation Layer: {Marshal.PtrToStringAnsi((nint)pCallbackData->PMessage)}");
        return Vk.False;
    }

    public static class ShaderCompiler
    {
        public static void CompileShaders(string vertexShaderPath, string fragmentShaderPath)
        {
            CompileShader(vertexShaderPath);
            CompileShader(fragmentShaderPath);
        }

        private static void CompileShader(string shaderPath)
        {
            if (!File.Exists(shaderPath))
            {
                throw new Exception($"Shader shader path {shaderPath} does not exist");
            }
            
            string? vulkanSDKPath = Environment.GetEnvironmentVariable("VULKAN_SDK");
            if (string.IsNullOrEmpty(vulkanSDKPath))
            {
                throw new Exception($"VULKAN_SDK path doesn't exist.");
            }
            
            string glslcExec = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "glslc.exe" :  "glslc";
            string binFolder = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Bin" : "bin";
            string glslcPath = Path.Combine(vulkanSDKPath, binFolder, glslcExec);

            if (!File.Exists(glslcPath))
            {
                throw new Exception($"GLSLC path {glslcPath} does not exist");
            }
            
            string outputPath = Path.ChangeExtension(shaderPath, ".spv");
            
            Console.WriteLine($"Shader path: {shaderPath}");

            var processInfo = new ProcessStartInfo
            {
                FileName = glslcPath,
                Arguments = $"{shaderPath} -o {outputPath}",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            
            using var process = Process.Start(processInfo);
            process.WaitForExit();
            
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();

            if (process.ExitCode != 0)
            {
               throw new Exception($"GLSLC compilation failed {stderr}");
            }
            else
            {
                Console.WriteLine($"Compiled {shaderPath} -> {outputPath}");
            }
        }
    }
}