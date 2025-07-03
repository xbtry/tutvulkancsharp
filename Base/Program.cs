using Silk.NET.Maths;
using Silk.NET.Windowing;

var app = new VulkanTutorialApp();
app.Run();

unsafe class VulkanTutorialApp
{
    private IWindow? _window;
    private IMonitor? _monitor;

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
        int width, height;
        GetMonitorBounds(out width, out height);
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
        
    }

    private void MainLoop()
    {
        
    }

    private void CleanUp()
    {
        _window?.Dispose();
    }
}