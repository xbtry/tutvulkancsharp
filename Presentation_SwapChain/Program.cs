using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Maths;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;
using Silk.NET.Windowing;


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
    }

    private void MainLoop()
    {
        _window!.Run();
    }

    private void CleanUp()
    {
        _khrSwapchain!.DestroySwapchain(_device, _swapchain, null);
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
}