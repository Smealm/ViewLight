using Avalonia;
using ViewLight.Services;

namespace ViewLight;

internal static class Program {

    [STAThread]
    public static void Main(string[] args) {
        // Before anything else, because portable mode decides where every path points: the settings file
        // itself lives under the root it is about to choose. Reading a path first would fix the wrong root
        // for the whole run.
        PortableMode.Apply();

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
