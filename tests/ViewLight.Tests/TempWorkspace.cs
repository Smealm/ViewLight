using ViewLight.Services;

namespace ViewLight.Tests;

/// <summary>
/// A throwaway directory that also redirects the launcher's own data root into it, so tests never
/// touch the real <c>%LOCALAPPDATA%\ViewLight</c>.
/// </summary>
public sealed class TempWorkspace : IDisposable {

    public string Root { get; }

    public TempWorkspace() {
        Root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "viewlight-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        LauncherPaths.DataRoot = System.IO.Path.Combine(Root, "launcher-data");
    }

    public string Path(string relative) => System.IO.Path.Combine(Root, relative);

    public string CreateDirectory(string relative) {
        var full = Path(relative);
        Directory.CreateDirectory(full);
        return full;
    }

    public string WriteFile(string relative, string content) {
        var full = Path(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public string WriteFile(string relative, byte[] content) {
        var full = Path(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        return full;
    }

    public void Dispose() {
        try {
            if (Directory.Exists(Root)) {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException) {
        }
    }
}
