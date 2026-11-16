using System.Text.Json;
using Microsoft.Win32;

namespace SideScreen;

/// <summary>User choices, remembered in %LOCALAPPDATA%\SideScreen\settings.json.</summary>
public sealed class Settings
{
    public string Mode { get; set; } = "extend";   // "extend" or the device name of the mirrored display
    public int Resolution { get; set; }            // 0 native, 1 = 2560x1600, 2 = 1920x1200
    public int Position { get; set; }              // 0 right, 1 left, 2 above, 3 below
    public int Rate { get; set; }                  // 0 = 120 Hz, 1 = 90 Hz, 2 = 60 Hz
    public int Quality { get; set; } = 1;          // 0 max … 3 light
    public bool Touch { get; set; } = true;
    public bool AutoConnect { get; set; }
    public bool CloseToTray { get; set; } = true;
    public bool TrayHintShown { get; set; }
    public bool Notifications { get; set; } = true;
    public bool Hotkey { get; set; } = true;

    static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SideScreen", "settings.json");

    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings(); }
        catch { return new Settings(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    // ---- "Start with Windows": a per-user Run entry that launches straight into the tray ----

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool RunAtLogin
    {
        get
        {
            try { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue("SideScreen") != null; }
            catch { return false; }
        }
        set
        {
            try
            {
                using var k = Registry.CurrentUser.CreateSubKey(RunKey);
                if (value) k.SetValue("SideScreen", $"\"{Environment.ProcessPath}\" --tray");
                else k.DeleteValue("SideScreen", false);
            }
            catch { }
        }
    }
}
