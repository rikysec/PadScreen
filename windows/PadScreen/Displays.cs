using System.Drawing;
using Vortice.DXGI;

namespace PadScreen;

internal sealed record MonitorInfo(string DeviceName, string AdapterName, string MonitorName, bool Primary, Rectangle Bounds)
{
    public override string ToString()
    {
        string label = string.IsNullOrWhiteSpace(MonitorName) ? AdapterName : MonitorName;
        return $"{DeviceName.Replace(@"\\.\", "")} — {label} — {Bounds.Width}×{Bounds.Height}{(Primary ? " (principale)" : "")}";
    }
}

/// <summary>Elenco schermi, modalità video e corrispondenza con le uscite DXGI usate da FFmpeg (ddagrab).</summary>
internal static class Displays
{
    public static IEnumerable<Native.DISPLAY_DEVICE> Adapters()
    {
        for (uint i = 0; ; i++)
        {
            var dd = Native.DISPLAY_DEVICE.Create();
            if (!Native.EnumDisplayDevicesW(null, i, ref dd, 0)) yield break;
            yield return dd;
        }
    }

    public static List<MonitorInfo> GetAttached()
    {
        var list = new List<MonitorInfo>();
        foreach (var dd in Adapters())
        {
            if ((dd.StateFlags & Native.DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0) continue;
            if ((dd.StateFlags & Native.DISPLAY_DEVICE_MIRRORING_DRIVER) != 0) continue;
            var b = GetBounds(dd.DeviceName);
            if (b == null) continue;
            var mon = Native.DISPLAY_DEVICE.Create();
            string monName = Native.EnumDisplayDevicesW(dd.DeviceName, 0, ref mon, 0) ? mon.DeviceString : "";
            list.Add(new MonitorInfo(dd.DeviceName, dd.DeviceString, monName,
                (dd.StateFlags & Native.DISPLAY_DEVICE_PRIMARY_DEVICE) != 0, b.Value));
        }
        return list;
    }

    /// <summary>Nomi (\\.\DISPLAYn) degli schermi del driver Parsec, con lo stato "collegato al desktop".</summary>
    public static List<(string Name, bool Attached)> ParsecDevices() =>
        Adapters()
            .Where(d => d.DeviceString.Contains(ParsecVdd.AdapterName, StringComparison.OrdinalIgnoreCase))
            .Select(d => (d.DeviceName, (d.StateFlags & Native.DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0))
            .ToList();

    /// <summary>Area dello schermo in pixel fisici, coordinate del desktop virtuale.</summary>
    public static Rectangle? GetBounds(string deviceName)
    {
        var dm = Native.DEVMODE.Create();
        if (!Native.EnumDisplaySettingsExW(deviceName, Native.ENUM_CURRENT_SETTINGS, ref dm, 0)) return null;
        if (dm.dmPelsWidth == 0 || dm.dmPelsHeight == 0) return null;
        return new Rectangle(dm.dmPositionX, dm.dmPositionY, (int)dm.dmPelsWidth, (int)dm.dmPelsHeight);
    }

    public static List<(int W, int H, int Hz)> GetModes(string deviceName)
    {
        var set = new HashSet<(int, int, int)>();
        for (int i = 0; ; i++)
        {
            var dm = Native.DEVMODE.Create();
            if (!Native.EnumDisplaySettingsExW(deviceName, i, ref dm, 0)) break;
            set.Add(((int)dm.dmPelsWidth, (int)dm.dmPelsHeight, (int)dm.dmDisplayFrequency));
        }
        return set.ToList();
    }

    /// <summary>Sceglie la modalità più adatta: esatta se c'è, altrimenti stesso rapporto d'aspetto e area più vicina.</summary>
    public static (int W, int H, int Hz)? PickMode(string deviceName, int w, int h, int fps)
    {
        var modes = GetModes(deviceName).Where(m => m.W > 0 && m.H > 0).ToList();
        if (modes.Count == 0) return null;
        int Rate(int hz) => hz >= fps ? 1000 - (hz - fps) : hz; // preferisci la frequenza >= fps più vicina
        var exact = modes.Where(m => m.W == w && m.H == h).OrderByDescending(m => Rate(m.Hz)).ToList();
        if (exact.Count > 0) return exact[0];

        double target = (double)w / h;
        bool portrait = h > w;
        return modes
            .Where(m => (m.H > m.W) == portrait)
            .DefaultIfEmpty(modes[0])
            .OrderBy(m => Math.Round(Math.Abs((double)m.W / m.H - target), 2))
            .ThenBy(m => Math.Abs((long)m.W * m.H - (long)w * h))
            .ThenByDescending(m => Rate(m.Hz))
            .First();
    }

    public static int SetMode(string deviceName, int w, int h, int hz)
    {
        var dm = Native.DEVMODE.Create();
        dm.dmPelsWidth = (uint)w;
        dm.dmPelsHeight = (uint)h;
        dm.dmFields = Native.DM_PELSWIDTH | Native.DM_PELSHEIGHT;
        if (hz > 0)
        {
            dm.dmDisplayFrequency = (uint)hz;
            dm.dmFields |= Native.DM_DISPLAYFREQUENCY;
        }
        return Native.ChangeDisplaySettingsExW(deviceName, ref dm, IntPtr.Zero, Native.CDS_UPDATEREGISTRY, IntPtr.Zero);
    }

    /// <summary>Indici scheda video/uscita DXGI dello schermo (servono a ddagrab di FFmpeg).</summary>
    public static (int Adapter, int Output, string AdapterName)? FindDxgiOutput(string deviceName)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint a = 0; factory.EnumAdapters1(a, out IDXGIAdapter1? adapter).Success; a++)
        {
            using (adapter)
            {
                for (uint o = 0; adapter!.EnumOutputs(o, out IDXGIOutput? output).Success; o++)
                {
                    using (output)
                    {
                        if (string.Equals(output!.Description.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
                            return ((int)a, (int)o, adapter.Description1.Description);
                    }
                }
            }
        }
        return null;
    }
}
