using System.Drawing;
using System.Runtime.InteropServices;
using PadScreen.Core;

namespace PadScreen;

/// <summary>
/// Trasforma l'input dell'iPad in input Windows sullo schermo trasmesso:
/// dita → touch multi-punto (come un monitor touch), Apple Pencil → penna con pressione e inclinazione,
/// trackpad dell'iPad → mouse. In modalità "mouse" anche le dita muovono il puntatore.
/// </summary>
internal sealed class InputInjector : IDisposable
{
    private const int MaxContacts = 10;
    private const int ERROR_NOT_READY = 21;
    private const int ERROR_INVALID_PARAMETER = 87;
    private const uint Contact = Native.POINTER_FLAG_INRANGE | Native.POINTER_FLAG_INCONTACT;
    private const uint PenContact = Native.POINTER_FLAG_INRANGE | Native.POINTER_FLAG_INCONTACT | Native.POINTER_FLAG_FIRSTBUTTON;

    private readonly object _lock = new();
    private readonly Action<string> _log;
    private readonly Dictionary<uint, Point> _active = new();
    private readonly System.Threading.Timer _keepAlive;
    private Func<Rectangle?> _boundsProvider = () => null;
    private Rectangle _bounds;
    private long _boundsTicks;
    private long _lastTouchTicks;
    private bool _touchReady;
    private bool _touchInitTried;

    private IntPtr _pen;
    private bool _penInitTried;
    private bool _penDown;
    private Point _penPos;
    private uint _penPressure;
    private int _penTiltX, _penTiltY;
    private long _lastPenTicks;

    private bool _mouseDown;
    private uint? _mouseFinger;
    private float _wheelAccY, _wheelAccX;
    private int _errors;

    public bool MouseMode { get; set; }

    public InputInjector(Action<string> log)
    {
        _log = log;
        _keepAlive = new System.Threading.Timer(_ => KeepAlive(), null, 50, 50);
    }

    public void SetTarget(Func<Rectangle?> boundsProvider)
    {
        lock (_lock)
        {
            _boundsProvider = boundsProvider;
            _boundsTicks = 0;
        }
    }

    public void Handle(IReadOnlyList<TouchSample> samples)
    {
        lock (_lock)
        {
            if (!RefreshBounds()) return;
            Dictionary<uint, TouchSample>? fingers = null;
            foreach (var s in samples)
            {
                switch (s.Kind)
                {
                    case TouchKind.Finger when !MouseMode && EnsureTouch():
                        (fingers ??= new())[s.Id] = s;
                        break;
                    case TouchKind.Finger:
                        MouseLike(s, isFinger: true);
                        break;
                    case TouchKind.Pencil when !MouseMode && EnsurePen():
                        InjectPen(s);
                        break;
                    default:
                        MouseLike(s, isFinger: false);
                        break;
                }
            }
            if (fingers != null) InjectTouchFrame(fingers);
        }
    }

    public void Scroll(float dx, float dy)
    {
        lock (_lock)
        {
            // Scorrimento "naturale" dell'iPad: dita verso il basso = contenuto verso il basso = rotella in su.
            _wheelAccY += dy * 3f;
            _wheelAccX += -dx * 3f;
            int wy = (int)_wheelAccY, wx = (int)_wheelAccX;
            _wheelAccY -= wy;
            _wheelAccX -= wx;
            if (wy != 0) SendMouse(0, 0, Native.MOUSEEVENTF_WHEEL, unchecked((uint)wy));
            if (wx != 0) SendMouse(0, 0, Native.MOUSEEVENTF_HWHEEL, unchecked((uint)wx));
        }
    }

    /// <summary>Rilascia tutto (fine sessione o errore): niente dita o tasti "incollati".</summary>
    public void ReleaseAll()
    {
        lock (_lock)
        {
            if (_active.Count > 0 && _touchReady)
            {
                var up = _active.Select(kv => MakeTouch(kv.Key, kv.Value, Native.POINTER_FLAG_UP | Native.POINTER_FLAG_CANCELED)).ToArray();
                InjectTouch(up);
            }
            _active.Clear();
            if (_penDown && _pen != IntPtr.Zero)
                InjectPenRaw(_penPos, Native.POINTER_FLAG_UP | Native.POINTER_FLAG_CANCELED, Native.POINTER_CHANGE_FIRSTBUTTON_UP, 0, 0, 0);
            _penDown = false;
            if (_mouseDown) SendMouse(0, 0, Native.MOUSEEVENTF_LEFTUP, 0);
            _mouseDown = false;
            _mouseFinger = null;
        }
    }

    // ---------- Coordinate ----------

    private bool RefreshBounds()
    {
        long now = Environment.TickCount64;
        if (now - _boundsTicks > 1000 || _bounds.Width == 0)
        {
            var b = _boundsProvider();
            if (b is { Width: > 0, Height: > 0 }) _bounds = b.Value;
            _boundsTicks = now;
        }
        return _bounds.Width > 0;
    }

    private Point Map(float x, float y) => new(
        _bounds.X + (int)Math.Round(Math.Clamp(x, 0f, 1f) * (_bounds.Width - 1)),
        _bounds.Y + (int)Math.Round(Math.Clamp(y, 0f, 1f) * (_bounds.Height - 1)));

    // ---------- Touch ----------

    private bool EnsureTouch()
    {
        if (_touchReady || _touchInitTried) return _touchReady;
        _touchInitTried = true;
        _touchReady = Native.InitializeTouchInjection(MaxContacts, Native.TOUCH_FEEDBACK_DEFAULT);
        if (!_touchReady)
            _log($"Iniezione touch non disponibile (errore {Marshal.GetLastWin32Error()}): uso il mouse.");
        return _touchReady;
    }

    private static Native.POINTER_TOUCH_INFO MakeTouch(uint id, Point p, uint flags) => new()
    {
        pointerInfo = new Native.POINTER_INFO
        {
            pointerType = Native.PT_TOUCH,
            pointerId = id,
            pointerFlags = flags,
            ptPixelLocation = new Native.POINT(p.X, p.Y),
        },
        touchMask = Native.TOUCH_MASK_CONTACTAREA | Native.TOUCH_MASK_ORIENTATION | Native.TOUCH_MASK_PRESSURE,
        rcContact = new Native.RECT(p.X - 2, p.Y - 2, p.X + 2, p.Y + 2),
        orientation = 90,
        pressure = 512,
    };

    /// <summary>
    /// Ogni frame deve contenere tutti i contatti appoggiati, e un "su" deve avvenire nella stessa
    /// posizione dell'ultimo frame: se il dito si è spostato, prima un UPDATE e poi l'UP in un secondo frame.
    /// </summary>
    private void InjectTouchFrame(Dictionary<uint, TouchSample> changed)
    {
        var frame = new List<Native.POINTER_TOUCH_INFO>();
        var inFrame = new HashSet<uint>();
        var ups = new List<(uint Id, uint Flags)>();
        bool hasChange = false;

        foreach (var (id, s) in changed)
        {
            if (id >= MaxContacts) continue;
            var p = Map(s.X, s.Y);
            bool known = _active.TryGetValue(id, out var last);
            switch (s.Phase)
            {
                case TouchPhase.Down:
                case TouchPhase.Move:
                    frame.Add(MakeTouch(id, p, (known ? Native.POINTER_FLAG_UPDATE : Native.POINTER_FLAG_DOWN) | Contact));
                    inFrame.Add(id);
                    _active[id] = p;
                    hasChange = true;
                    break;
                case TouchPhase.Up:
                case TouchPhase.Cancel:
                    if (!known) break;
                    if (last != p)
                    {
                        frame.Add(MakeTouch(id, p, Native.POINTER_FLAG_UPDATE | Contact));
                        inFrame.Add(id);
                        _active[id] = p;
                        hasChange = true;
                    }
                    ups.Add((id, Native.POINTER_FLAG_UP | (s.Phase == TouchPhase.Cancel ? Native.POINTER_FLAG_CANCELED : 0)));
                    break;
            }
        }

        if (hasChange)
        {
            foreach (var (id, p) in _active)
                if (!inFrame.Contains(id)) frame.Add(MakeTouch(id, p, Native.POINTER_FLAG_UPDATE | Contact));
            if (!InjectTouch(frame.ToArray())) return;
        }

        if (ups.Count > 0)
        {
            var upIds = ups.Select(u => u.Id).ToHashSet();
            var frame2 = ups.Select(u => MakeTouch(u.Id, _active[u.Id], u.Flags)).ToList();
            foreach (var (id, p) in _active)
                if (!upIds.Contains(id)) frame2.Add(MakeTouch(id, p, Native.POINTER_FLAG_UPDATE | Contact));
            foreach (var id in upIds) _active.Remove(id);
            InjectTouch(frame2.ToArray());
        }
    }

    private bool InjectTouch(Native.POINTER_TOUCH_INFO[] frame)
    {
        for (int attempt = 0; ; attempt++)
        {
            if (Native.InjectTouchInput((uint)frame.Length, frame))
            {
                _lastTouchTicks = Environment.TickCount64;
                return true;
            }
            int err = Marshal.GetLastWin32Error();
            if (err == ERROR_NOT_READY && attempt < 5)
            {
                Thread.Sleep(1); // chiamate troppo ravvicinate: riprova
                continue;
            }
            if (_errors++ % 50 == 0) _log($"InjectTouchInput non riuscito (errore {err}).");
            if (err == ERROR_INVALID_PARAMETER) _active.Clear(); // stato non più allineato con Windows
            return false;
        }
    }

    /// <summary>Un dito o una penna fermi devono continuare a "esistere" per Windows: ripetiamo la posizione.</summary>
    private void KeepAlive()
    {
        lock (_lock)
        {
            long now = Environment.TickCount64;
            if (_active.Count > 0 && _touchReady && now - _lastTouchTicks >= 90)
            {
                var frame = _active.Select(kv => MakeTouch(kv.Key, kv.Value, Native.POINTER_FLAG_UPDATE | Contact)).ToArray();
                InjectTouch(frame);
            }
            if (_penDown && _pen != IntPtr.Zero && now - _lastPenTicks >= 90)
                InjectPenRaw(_penPos, PenContact | Native.POINTER_FLAG_UPDATE, Native.POINTER_CHANGE_NONE, _penPressure, _penTiltX, _penTiltY);
        }
    }

    // ---------- Penna ----------

    private bool EnsurePen()
    {
        if (_pen != IntPtr.Zero || _penInitTried) return _pen != IntPtr.Zero;
        _penInitTried = true;
        try
        {
            _pen = Native.CreateSyntheticPointerDevice(Native.PT_PEN, 1, Native.POINTER_FEEDBACK_DEFAULT);
        }
        catch (EntryPointNotFoundException)
        {
            _pen = IntPtr.Zero;
        }
        if (_pen == IntPtr.Zero)
            _log("Penna sintetica non disponibile (serve Windows 10 1809+): l'Apple Pencil farà da mouse.");
        return _pen != IntPtr.Zero;
    }

    private void InjectPen(TouchSample s)
    {
        var p = Map(s.X, s.Y);
        var (tx, ty) = Tilt(s.Altitude, s.Azimuth);
        switch (s.Phase)
        {
            case TouchPhase.Down:
                if (_penDown) goto case TouchPhase.Move;
                _penDown = true;
                SetPenState(p, s.Pressure, tx, ty);
                InjectPenRaw(p, PenContact | Native.POINTER_FLAG_DOWN, Native.POINTER_CHANGE_FIRSTBUTTON_DOWN, _penPressure, tx, ty);
                break;
            case TouchPhase.Move:
                if (_penDown)
                {
                    SetPenState(p, s.Pressure, tx, ty);
                    InjectPenRaw(p, PenContact | Native.POINTER_FLAG_UPDATE, Native.POINTER_CHANGE_NONE, _penPressure, tx, ty);
                }
                else
                {
                    InjectPenRaw(p, Native.POINTER_FLAG_INRANGE | Native.POINTER_FLAG_UPDATE, Native.POINTER_CHANGE_NONE, 0, tx, ty);
                }
                break;
            case TouchPhase.Up:
            case TouchPhase.Cancel:
                if (!_penDown) return;
                if (p != _penPos)
                {
                    // L'UP deve avvenire dove si trovava l'ultimo aggiornamento.
                    SetPenState(p, s.Pressure, tx, ty);
                    InjectPenRaw(p, PenContact | Native.POINTER_FLAG_UPDATE, Native.POINTER_CHANGE_NONE, _penPressure, tx, ty);
                }
                InjectPenRaw(_penPos, Native.POINTER_FLAG_UP | (s.Phase == TouchPhase.Cancel ? Native.POINTER_FLAG_CANCELED : 0),
                    Native.POINTER_CHANGE_FIRSTBUTTON_UP, 0, tx, ty);
                _penDown = false;
                break;
            case TouchPhase.Hover:
                if (_penDown) return;
                InjectPenRaw(p, Native.POINTER_FLAG_INRANGE | Native.POINTER_FLAG_UPDATE, Native.POINTER_CHANGE_NONE, 0, tx, ty);
                break;
            case TouchPhase.HoverExit:
                if (_penDown) return;
                InjectPenRaw(p, Native.POINTER_FLAG_UPDATE, Native.POINTER_CHANGE_NONE, 0, 0, 0); // fuori portata
                break;
        }
    }

    private void SetPenState(Point p, float pressure, int tx, int ty)
    {
        _penPos = p;
        _penPressure = (uint)Math.Clamp(pressure * 1024f, 1f, 1024f);
        _penTiltX = tx;
        _penTiltY = ty;
    }

    private void InjectPenRaw(Point p, uint flags, int change, uint pressure, int tiltX, int tiltY)
    {
        // Per InjectSyntheticPointerInput le coordinate partono dall'angolo in alto a sinistra del desktop virtuale.
        int vx = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
        int vy = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
        var info = new Native.POINTER_TYPE_INFO
        {
            type = Native.PT_PEN,
            penInfo = new Native.POINTER_PEN_INFO
            {
                pointerInfo = new Native.POINTER_INFO
                {
                    pointerType = Native.PT_PEN,
                    pointerFlags = flags,
                    ptPixelLocation = new Native.POINT(p.X - vx, p.Y - vy),
                    ButtonChangeType = change,
                },
                penMask = Native.PEN_MASK_PRESSURE | Native.PEN_MASK_TILT_X | Native.PEN_MASK_TILT_Y,
                pressure = pressure,
                tiltX = tiltX,
                tiltY = tiltY,
            },
        };
        var arr = new[] { info };
        for (int attempt = 0; ; attempt++)
        {
            if (Native.InjectSyntheticPointerInput(_pen, arr, 1))
            {
                _lastPenTicks = Environment.TickCount64;
                return;
            }
            int err = Marshal.GetLastWin32Error();
            if (err == ERROR_NOT_READY && attempt < 5)
            {
                Thread.Sleep(1);
                continue;
            }
            if (_errors++ % 50 == 0) _log($"Iniezione penna non riuscita (errore {err}).");
            return;
        }
    }

    /// <summary>Altitudine/azimut dell'Apple Pencil (radianti) → inclinazione X/Y di Windows (gradi).</summary>
    private static (int X, int Y) Tilt(float altitude, float azimuth)
    {
        if (altitude <= 0) return (0, 0);
        double horiz = Math.Cos(altitude), vert = Math.Sin(altitude);
        double tx = Math.Atan2(Math.Cos(azimuth) * horiz, vert) * 180 / Math.PI;
        double ty = Math.Atan2(Math.Sin(azimuth) * horiz, vert) * 180 / Math.PI;
        return ((int)Math.Clamp(Math.Round(tx), -90, 90), (int)Math.Clamp(Math.Round(ty), -90, 90));
    }

    // ---------- Mouse ----------

    private void MouseLike(TouchSample s, bool isFinger)
    {
        if (isFinger)
        {
            // Modalità mouse: conta solo il primo dito.
            if (_mouseFinger == null && s.Phase == TouchPhase.Down) _mouseFinger = s.Id;
            if (_mouseFinger != s.Id) return;
        }
        var p = Map(s.X, s.Y);
        switch (s.Phase)
        {
            case TouchPhase.Down:
                MoveTo(p);
                if (!_mouseDown) SendMouse(0, 0, Native.MOUSEEVENTF_LEFTDOWN, 0);
                _mouseDown = true;
                break;
            case TouchPhase.Move:
            case TouchPhase.Hover:
                MoveTo(p);
                break;
            case TouchPhase.Up:
            case TouchPhase.Cancel:
                MoveTo(p);
                if (_mouseDown) SendMouse(0, 0, Native.MOUSEEVENTF_LEFTUP, 0);
                _mouseDown = false;
                if (isFinger) _mouseFinger = null;
                break;
        }
    }

    private static void MoveTo(Point p)
    {
        int vx = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
        int vy = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
        int vw = Math.Max(2, Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN));
        int vh = Math.Max(2, Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN));
        int dx = (int)Math.Round((p.X - vx) * 65535.0 / (vw - 1));
        int dy = (int)Math.Round((p.Y - vy) * 65535.0 / (vh - 1));
        SendMouse(dx, dy, Native.MOUSEEVENTF_MOVE | Native.MOUSEEVENTF_ABSOLUTE | Native.MOUSEEVENTF_VIRTUALDESK, 0);
    }

    private static void SendMouse(int dx, int dy, uint flags, uint data)
    {
        var input = new[]
        {
            new Native.INPUT
            {
                type = Native.INPUT_MOUSE,
                mi = new Native.MOUSEINPUT { dx = dx, dy = dy, dwFlags = flags, mouseData = data },
            },
        };
        Native.SendInput(1, input, Marshal.SizeOf<Native.INPUT>());
    }

    public void Dispose()
    {
        _keepAlive.Dispose();
        ReleaseAll();
        lock (_lock)
        {
            if (_pen != IntPtr.Zero) Native.DestroySyntheticPointerDevice(_pen);
            _pen = IntPtr.Zero;
        }
    }
}
