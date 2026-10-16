using Avalonia;

namespace Gallery.Desktop;

internal static class Program
{
    // Nothing that needs Avalonia or a SynchronizationContext may run before AppMain.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    // Also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
