using System.IO.Pipes;
using System.Text;
using System.Xml;

namespace SideScreen;

/// <summary>
/// Makes sure the virtual display offers a given resolution and refresh rate. New modes are added to the
/// driver's settings file and the driver is told to reload through its control pipe; no admin rights needed.
/// </summary>
public static class DriverModes
{
    const string SettingsFile = @"C:\VirtualDisplayDriver\vdd_settings.xml";
    const string PipeName = "MTTVirtualDisplayPipe";

    /// <summary>Returns null when the mode is available, otherwise a short reason.</summary>
    public static string? Ensure(int width, int height, int hz, Action<string>? status = null)
    {
        var vd = Displays.FindVirtual();
        if (vd == null) return "virtual display driver not installed";
        if (Displays.HasMode(vd.DeviceName, width, height, hz)) return null;

        status?.Invoke($"Adding {width}×{height} @ {hz} Hz to the virtual display…");
        Log.Write($"adding driver mode {width}x{height}@{hz}");
        try { AddToSettings(width, height, hz); }
        catch (Exception e) { return "could not update the driver settings (" + e.Message + ")"; }

        if (!SendCommand("RELOAD_DRIVER")) return "the virtual display driver did not respond";

        // The driver comes back as a new display device within a few seconds.
        for (int i = 0; i < 40; i++)
        {
            Thread.Sleep(250);
            vd = Displays.FindVirtual();
            if (vd != null && Displays.HasMode(vd.DeviceName, width, height, hz)) return null;
        }
        return $"the driver did not accept {width}×{height} @ {hz} Hz";
    }

    static void AddToSettings(int width, int height, int hz)
    {
        var doc = new XmlDocument();
        doc.Load(SettingsFile);
        var root = doc.DocumentElement ?? throw new InvalidDataException("empty settings file");

        // resolutions are combined with every global refresh rate by the driver
        var resolutions = root["resolutions"] ?? (XmlElement)root.AppendChild(doc.CreateElement("resolutions"))!;
        bool haveRes = resolutions.ChildNodes.OfType<XmlElement>().Any(r =>
            r["width"]?.InnerText.Trim() == width.ToString() && r["height"]?.InnerText.Trim() == height.ToString());
        if (!haveRes)
        {
            var r = doc.CreateElement("resolution");
            foreach (var (name, value) in new[] { ("width", width), ("height", height), ("refresh_rate", 60) })
            {
                var e = doc.CreateElement(name);
                e.InnerText = value.ToString();
                r.AppendChild(e);
            }
            resolutions.AppendChild(r);
        }

        var global = root["global"] ?? (XmlElement)root.AppendChild(doc.CreateElement("global"))!;
        bool haveHz = global.ChildNodes.OfType<XmlElement>().Any(g => g.Name == "g_refresh_rate" && g.InnerText.Trim() == hz.ToString());
        if (!haveHz)
        {
            var g = doc.CreateElement("g_refresh_rate");
            g.InnerText = hz.ToString();
            global.AppendChild(g);
        }

        if (!haveRes || !haveHz) doc.Save(SettingsFile);
    }

    static bool SendCommand(string command)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
            pipe.Connect(3000);
            var bytes = Encoding.Unicode.GetBytes(command);
            pipe.Write(bytes, 0, bytes.Length);
            pipe.Flush();
            return true;
        }
        catch (Exception e)
        {
            Log.Write($"driver pipe: {e.Message}");
            return false;
        }
    }
}
