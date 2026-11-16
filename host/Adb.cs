using System.Diagnostics;

namespace SideScreen;

public static class Tools
{
    public static string BaseDir => AppContext.BaseDirectory;

    /// <summary>Find a bundled tool next to the exe, in the dev tree, or fall back to PATH.</summary>
    static string Locate(string exe, params string[] relativeCandidates)
    {
        foreach (var rel in relativeCandidates)
        {
            var dir = new DirectoryInfo(BaseDir);
            for (int up = 0; dir != null && up < 7; up++, dir = dir.Parent)
            {
                var p = Path.Combine(dir.FullName, rel, exe);
                if (File.Exists(p)) return p;
            }
        }
        return exe;
    }

    public static string AdbPath => _adb ??= Locate("adb.exe", "adb", Path.Combine("tools", "android-sdk", "platform-tools"));
    public static string CapturePath => _capture ??= Locate("sscapture.exe", "", Path.Combine("capture", "build"));
    static string? _adb, _capture;

    public static string? FindFile(string relative)
    {
        var dir = new DirectoryInfo(BaseDir);
        for (int up = 0; dir != null && up < 7; up++, dir = dir.Parent)
        {
            var p = Path.Combine(dir.FullName, relative);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    public static (int code, string output) Run(string exe, string args, int timeoutMs = 20000)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEndAsync();
            var outp = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return (-1, "timeout"); }
            return (p.ExitCode, (outp.Result + err.Result).Trim());
        }
        catch (Exception e) { return (-1, e.Message); }
    }
}

public static class Adb
{
    public const string Package = "com.sidescreen.app";

    public static (int code, string output) Run(string args, int timeoutMs = 20000) => Tools.Run(Tools.AdbPath, args, timeoutMs);

    /// <summary>Serial + model of the first authorised device, or a hint about what is wrong.</summary>
    public static (string? serial, string info) FindDevice()
    {
        var (code, output) = Run("devices -l", 8000);
        if (code != 0) return (null, "adb not available");
        string? hint = null;
        foreach (var line in output.Split('\n').Skip(1))
        {
            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            if (parts[1] == "device")
            {
                var model = parts.FirstOrDefault(p => p.StartsWith("model:"))?[6..].Replace('_', '-') ?? parts[0];
                return (parts[0], model);
            }
            if (parts[1] == "unauthorized") hint = "Allow USB debugging on the tablet";
            else if (parts[1] == "offline") hint = "Tablet offline – replug the cable";
        }
        return (null, hint ?? "No tablet found – connect USB and enable USB debugging");
    }

    /// <summary>Install the tablet app when it is missing or the bundled APK changed.</summary>
    public static string? EnsureApp(string serial)
    {
        var apk = Tools.FindFile("SideScreen.apk") ?? Tools.FindFile(Path.Combine("android", "build", "SideScreen.apk"));
        var installed = Run($"-s {serial} shell pm list packages {Package}").output.Contains(Package);
        if (apk == null) return installed ? null : "SideScreen.apk not found next to the exe";

        var stamp = $"{new FileInfo(apk).Length}-{File.GetLastWriteTimeUtc(apk).Ticks}";
        var markerDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SideScreen");
        var marker = Path.Combine(markerDir, $"apk-{serial}.txt");
        if (installed && File.Exists(marker) && File.ReadAllText(marker) == stamp) return null;

        var (code, output) = Run($"-s {serial} install -r \"{apk}\"", 120000);
        if (code != 0 || !output.Contains("Success")) return "APK install failed: " + output.Split('\n').Last();
        Directory.CreateDirectory(markerDir);
        File.WriteAllText(marker, stamp);
        return null;
    }

    public static void Reverse(string serial, int port) => Run($"-s {serial} reverse tcp:{port} tcp:{port}", 8000);
    public static void RemoveReverse(string serial, int port) => Run($"-s {serial} reverse --remove tcp:{port}", 5000);
    public static void LaunchApp(string serial) => Run($"-s {serial} shell am start -n {Package}/.MainActivity", 8000);
}
