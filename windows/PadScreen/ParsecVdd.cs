using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace PadScreen;

/// <summary>
/// Controllo del driver "Parsec Virtual Display" (IddCx, firmato), che crea monitor virtuali veri:
/// Windows estende il desktop su di essi. API documentata dal progetto open source nomi-san/parsec-vdd.
/// Gli schermi aggiunti restano vivi solo finché inviamo un "update" ogni ~100 ms: se PadScreen
/// si chiude (anche per un crash), lo schermo virtuale sparisce da solo.
/// </summary>
internal sealed class ParsecVdd : IDisposable
{
    public const string DownloadUrl = "https://builds.parsec.app/vdd/parsec-vdd-0.45.0.0.exe";
    public const string DownloadUrlLegacy = "https://builds.parsec.app/vdd/parsec-vdd-0.41.0.0.exe";
    public const string AdapterName = "Parsec Virtual Display";

    private static readonly Guid AdapterInterfaceGuid = new("00b41627-04c4-429e-a26e-0265cf50c8fa");
    private static readonly Guid DisplayClassGuid = new("4d36e968-e325-11ce-bfc1-08002be10318");
    private const string HardwareId = @"Root\Parsec\VDA";

    private const uint IOCTL_ADD = 0x0022e004;
    private const uint IOCTL_REMOVE = 0x0022a008;
    private const uint IOCTL_UPDATE = 0x0022a00c;
    private const uint IOCTL_VERSION = 0x0022e010;

    private readonly SafeFileHandle _handle;
    private readonly object _io = new();
    private readonly Thread _keepAlive;
    private volatile bool _running = true;
    private readonly List<int> _displays = new();

    private ParsecVdd(SafeFileHandle handle)
    {
        _handle = handle;
        _keepAlive = new Thread(KeepAliveLoop) { IsBackground = true, Name = "ParsecVdd keepalive" };
        _keepAlive.Start();
    }

    public enum DriverState { NotInstalled, Ok, Problem }

    /// <summary>Stato del driver: installato, assente, o presente con un problema (codice Gestione dispositivi).</summary>
    public static (DriverState State, uint Problem) QueryStatus()
    {
        var guid = DisplayClassGuid;
        IntPtr set = Native.SetupDiGetClassDevsW(ref guid, IntPtr.Zero, IntPtr.Zero, Native.DIGCF_PRESENT);
        if (set == Native.INVALID_HANDLE_VALUE) return (DriverState.NotInstalled, 0);
        try
        {
            for (uint i = 0; ; i++)
            {
                var info = new Native.SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<Native.SP_DEVINFO_DATA>() };
                if (!Native.SetupDiEnumDeviceInfo(set, i, ref info)) break;
                Native.SetupDiGetDeviceRegistryPropertyW(set, ref info, Native.SPDRP_HARDWAREID, out _, null, 0, out uint size);
                if (size == 0) continue;
                var buf = new byte[size];
                if (!Native.SetupDiGetDeviceRegistryPropertyW(set, ref info, Native.SPDRP_HARDWAREID, out _, buf, size, out _)) continue;
                var ids = System.Text.Encoding.Unicode.GetString(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries);
                if (!ids.Any(id => id.Equals(HardwareId, StringComparison.OrdinalIgnoreCase))) continue;
                if (Native.CM_Get_DevNode_Status(out _, out uint problem, info.DevInst, 0) != 0)
                    return (DriverState.Problem, 0);
                return problem == 0 ? (DriverState.Ok, 0) : (DriverState.Problem, problem);
            }
        }
        finally
        {
            Native.SetupDiDestroyDeviceInfoList(set);
        }
        return (DriverState.NotInstalled, 0);
    }

    /// <summary>Descrizione per l'interfaccia: (pronto?, testo).</summary>
    public static (bool Ready, string Text) Describe()
    {
        if (FindInterfacePath() != null) return (true, "Driver schermo virtuale: installato ✓");
        var (state, problem) = QueryStatus();
        return state switch
        {
            DriverState.Problem => (false, $"Driver schermo virtuale: installato ma non attivo (codice {problem}) — riavvia il PC"),
            DriverState.Ok => (false, "Driver schermo virtuale: installato ma non raggiungibile — riavvia il PC"),
            _ => (false, "Driver schermo virtuale: non installato"),
        };
    }

    public static ParsecVdd Open()
    {
        string? path = FindInterfacePath();
        if (path == null)
        {
            var (state, problem) = QueryStatus();
            throw new InvalidOperationException(state switch
            {
                DriverState.Problem => $"Il driver Parsec VDD è installato ma non attivo (codice {problem}). Prova a riavviare il PC.",
                DriverState.Ok => "Driver Parsec VDD trovato ma non raggiungibile. Riavvia il PC.",
                _ => "Il driver per lo schermo virtuale (Parsec VDD) non è installato. Installalo dalla finestra di PadScreen oppure usa la modalità «Duplica un monitor».",
            });
        }
        var h = Native.CreateFileW(path,
            Native.GENERIC_READ | Native.GENERIC_WRITE,
            Native.FILE_SHARE_ALL,
            IntPtr.Zero,
            Native.OPEN_EXISTING,
            Native.FILE_ATTRIBUTE_NORMAL | Native.FILE_FLAG_NO_BUFFERING | Native.FILE_FLAG_OVERLAPPED | Native.FILE_FLAG_WRITE_THROUGH,
            IntPtr.Zero);
        if (h.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            h.Dispose();
            throw new InvalidOperationException($"Impossibile aprire il driver Parsec VDD (errore {err}).");
        }
        return new ParsecVdd(h);
    }

    private static string? FindInterfacePath()
    {
        var guid = AdapterInterfaceGuid;
        IntPtr set = Native.SetupDiGetClassDevsW(ref guid, IntPtr.Zero, IntPtr.Zero, Native.DIGCF_PRESENT | Native.DIGCF_DEVICEINTERFACE);
        if (set == Native.INVALID_HANDLE_VALUE) return null;
        try
        {
            for (uint i = 0; ; i++)
            {
                var data = new Native.SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<Native.SP_DEVICE_INTERFACE_DATA>() };
                if (!Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data)) break;
                Native.SetupDiGetDeviceInterfaceDetailW(set, ref data, IntPtr.Zero, 0, out uint size, IntPtr.Zero);
                if (size == 0) continue;
                IntPtr detail = Marshal.AllocHGlobal((int)size);
                try
                {
                    // cbSize di SP_DEVICE_INTERFACE_DETAIL_DATA_W: 8 a 64 bit, 6 a 32 bit.
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (Native.SetupDiGetDeviceInterfaceDetailW(set, ref data, detail, size, out _, IntPtr.Zero))
                        return Marshal.PtrToStringUni(detail + 4);
                }
                finally
                {
                    Marshal.FreeHGlobal(detail);
                }
            }
        }
        finally
        {
            Native.SetupDiDestroyDeviceInfoList(set);
        }
        return null;
    }

    public int Version() => IoControl(IOCTL_VERSION, ReadOnlySpan<byte>.Empty);

    /// <summary>Collega un nuovo monitor virtuale e ne restituisce l'indice.</summary>
    public int AddDisplay()
    {
        int idx = IoControl(IOCTL_ADD, ReadOnlySpan<byte>.Empty);
        IoControl(IOCTL_UPDATE, ReadOnlySpan<byte>.Empty);
        if (idx < 0) throw new InvalidOperationException("Il driver non ha creato lo schermo virtuale.");
        lock (_displays) _displays.Add(idx);
        return idx;
    }

    public void RemoveDisplay(int index)
    {
        Span<byte> data = stackalloc byte[2];
        data[0] = (byte)(index >> 8); // indice a 16 bit big-endian
        data[1] = (byte)index;
        IoControl(IOCTL_REMOVE, data);
        IoControl(IOCTL_UPDATE, ReadOnlySpan<byte>.Empty);
        lock (_displays) _displays.Remove(index);
    }

    private void KeepAliveLoop()
    {
        while (_running)
        {
            IoControl(IOCTL_UPDATE, ReadOnlySpan<byte>.Empty);
            Thread.Sleep(80);
        }
    }

    private unsafe int IoControl(uint code, ReadOnlySpan<byte> data)
    {
        lock (_io)
        {
            if (_handle.IsInvalid || _handle.IsClosed) return -1;
            byte* inBuf = stackalloc byte[32];
            var inSpan = new Span<byte>(inBuf, 32);
            inSpan.Clear();
            data[..Math.Min(32, data.Length)].CopyTo(inSpan);
            uint outVal = 0;
            using var ev = new ManualResetEvent(false);
            var ov = new NativeOverlapped { EventHandle = ev.SafeWaitHandle.DangerousGetHandle() };
            if (!Native.DeviceIoControl(_handle, code, inBuf, 32, (byte*)&outVal, sizeof(uint), null, &ov))
            {
                if (Marshal.GetLastWin32Error() != Native.ERROR_IO_PENDING) return -1;
            }
            if (!Native.GetOverlappedResultEx(_handle, &ov, out _, 5000, false))
            {
                Native.CancelIoEx(_handle, &ov);
                Native.GetOverlappedResult(_handle, &ov, out _, true);
                return -1;
            }
            return (int)outVal;
        }
    }

    public void Dispose()
    {
        int[] left;
        lock (_displays) left = _displays.ToArray();
        foreach (var idx in left)
        {
            try { RemoveDisplay(idx); } catch { }
        }
        _running = false;
        _keepAlive.Join(500);
        lock (_io) _handle.Dispose();
    }
}

/// <summary>
/// Risoluzioni personalizzate del driver Parsec VDD: HKLM\SOFTWARE\Parsec\vdd\{0..4} con width/height/hz.
/// Il driver le legge quando uno schermo viene collegato. Scriverle richiede i diritti di amministratore.
/// </summary>
internal static class ParsecModes
{
    private const string KeyPath = @"SOFTWARE\Parsec\vdd";
    private const int Slots = 5;

    public static List<(int Slot, int W, int H, int Hz)> Read()
    {
        var list = new List<(int, int, int, int)>();
        using var key = Registry.LocalMachine.OpenSubKey(KeyPath);
        if (key == null) return list;
        for (int i = 0; i < Slots; i++)
        {
            using var sub = key.OpenSubKey(i.ToString());
            if (sub == null) continue;
            int w = Convert.ToInt32(sub.GetValue("width", 0));
            int h = Convert.ToInt32(sub.GetValue("height", 0));
            int hz = Convert.ToInt32(sub.GetValue("hz", 0));
            if (w > 0 && h > 0) list.Add((i, w, h, hz));
        }
        return list;
    }

    public static bool ContainsAll(IEnumerable<(int W, int H)> modes)
    {
        var have = Read();
        return modes.All(m => have.Any(h => h.W == m.W && h.H == m.H));
    }

    /// <summary>Scrive le risoluzioni richieste negli slot liberi (o sovrascrive i primi). Serve l'elevazione.</summary>
    public static void Write(IReadOnlyList<(int W, int H)> modes, int hz)
    {
        using var key = Registry.LocalMachine.CreateSubKey(KeyPath, true);
        var existing = Read();
        var needed = modes.Where(m => !existing.Any(e => e.W == m.W && e.H == m.H)).Distinct().ToList();
        if (needed.Count == 0) return;

        var keep = existing.Where(e => modes.Any(m => m.W == e.W && m.H == e.H)).Select(e => e.Slot).ToHashSet();
        var free = Enumerable.Range(0, Slots).Where(s => !existing.Any(e => e.Slot == s)).ToList();
        var reusable = Enumerable.Range(0, Slots).Where(s => !keep.Contains(s) && !free.Contains(s)).ToList();
        var targets = free.Concat(reusable).ToList();

        for (int i = 0; i < needed.Count && i < targets.Count; i++)
        {
            using var sub = key.CreateSubKey(targets[i].ToString(), true);
            sub.SetValue("width", needed[i].W, RegistryValueKind.DWord);
            sub.SetValue("height", needed[i].H, RegistryValueKind.DWord);
            sub.SetValue("hz", hz, RegistryValueKind.DWord);
        }
    }
}
