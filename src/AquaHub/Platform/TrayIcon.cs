using System.Runtime.InteropServices;
using System.Windows.Interop;
using AquaHub.Core.Util;
using AquaHub.Services;

namespace AquaHub.Platform;

/// <summary>
/// Notification-area icon implemented directly on Shell_NotifyIcon (no WinForms dependency).
/// Owns a hidden message window that also serves global hotkeys and system broadcasts.
/// Balloon notifications are rendered by Windows 10/11 as native toasts.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int CallbackMessage = Native.WM_APP + 1;
    private readonly HwndSource _window;
    private readonly int _taskbarCreated;
    private IntPtr _icon;
    private IntPtr _largeIcon;
    private string _tooltip = "Aqua Hub";
    private bool _added;
    private Action? _balloonClick;
    private int _shownSinceClick;

    public event Action<int, int>? LeftClick;
    public event Action<int, int>? RightClick;
    public event Action? DoubleClick;
    public event Action? MiddleClick;
    public event Action? BalloonClicked;
    /// <summary>A notification was clicked, but several were shown since the last click, so it can't be told which.</summary>
    public event Action? SeveralClicked;
    public event Action<int>? Hotkey;
    public event Action<string?>? SettingChanged;
    public event Action? PowerChanged;

    public IntPtr Handle => _window.Handle;

    public TrayIcon()
    {
        var p = new HwndSourceParameters("AquaHubMessageWindow") { Width = 0, Height = 0, WindowStyle = 0, ExtendedWindowStyle = Native.WS_EX_TOOLWINDOW };
        _window = new HwndSource(p);
        _window.AddHook(WndProc);
        _taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
    }

    public void Show(IntPtr icon, IntPtr largeIcon, string tooltip)
    {
        _icon = icon;
        _largeIcon = largeIcon;
        _tooltip = tooltip;
        var data = NewData(Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP | Native.NIF_SHOWTIP);
        _added = Native.Shell_NotifyIconW(Native.NIM_ADD, ref data);
        data.uTimeoutOrVersion = Native.NOTIFYICON_VERSION_4;
        Native.Shell_NotifyIconW(Native.NIM_SETVERSION, ref data);
        if (!_added) Log.Warn("tray", "Shell_NotifyIcon(NIM_ADD) failed");
    }

    public void Update(IntPtr? icon = null, string? tooltip = null)
    {
        if (icon is { } i) _icon = i;
        if (tooltip is not null) _tooltip = tooltip;
        if (!_added) return;
        var data = NewData(Native.NIF_ICON | Native.NIF_TIP | Native.NIF_SHOWTIP);
        Native.Shell_NotifyIconW(Native.NIM_MODIFY, ref data);
    }

    /// <summary>
    /// Shows a notification (Windows renders it as a toast attributed to Aqua Hub). Clicking it runs
    /// <paramref name="onClick"/>, or raises <see cref="BalloonClicked"/> when there's none. Windows reports a click
    /// for the icon, not for a particular notification, so after several it raises <see cref="SeveralClicked"/>.
    /// </summary>
    public void Notify(string title, string body, bool quiet = false, Action? onClick = null)
    {
        if (Sandbox.Intercept("toast", title)) return;
        if (!_added) return;
        _balloonClick = onClick;
        _shownSinceClick++;
        var data = NewData(Native.NIF_INFO);
        data.szInfoTitle = Trim(title, 63);
        data.szInfo = Trim(string.IsNullOrWhiteSpace(body) ? " " : body, 255);
        data.dwInfoFlags = Native.NIIF_USER | Native.NIIF_LARGE_ICON | Native.NIIF_RESPECT_QUIET_TIME | (quiet ? Native.NIIF_NOSOUND : 0);
        data.hBalloonIcon = _largeIcon;
        Native.Shell_NotifyIconW(Native.NIM_MODIFY, ref data);
    }

    /// <summary>Screen rectangle of the icon (physical pixels), if visible on the taskbar.</summary>
    public Native.RECT? GetIconRect()
    {
        var id = new Native.NOTIFYICONIDENTIFIER { cbSize = Marshal.SizeOf<Native.NOTIFYICONIDENTIFIER>(), hWnd = _window.Handle, uID = 1 };
        return Native.Shell_NotifyIconGetRect(ref id, out var rect) == 0 ? rect : null;
    }

    private Native.NOTIFYICONDATAW NewData(int flags) => new()
    {
        cbSize = Marshal.SizeOf<Native.NOTIFYICONDATAW>(),
        hWnd = _window.Handle,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon,
        szTip = Trim(_tooltip, 127),
        szInfo = "",
        szInfoTitle = "",
    };

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == CallbackMessage)
        {
            var evt = (int)(lParam.ToInt64() & 0xFFFF);
            var x = (short)(wParam.ToInt64() & 0xFFFF);
            var y = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
            switch (evt)
            {
                case Native.NIN_SELECT:
                case Native.NIN_KEYSELECT:
                    LeftClick?.Invoke(x, y);
                    break;
                case Native.WM_CONTEXTMENU:
                    RightClick?.Invoke(x, y);
                    break;
                case Native.WM_LBUTTONDBLCLK:
                    DoubleClick?.Invoke();
                    break;
                case Native.WM_MBUTTONUP:
                    MiddleClick?.Invoke();
                    break;
                case Native.NIN_BALLOONUSERCLICK:
                    var shown = _shownSinceClick;
                    _shownSinceClick = 0;
                    if (shown > 1 && SeveralClicked is { } several) several();
                    else if (_balloonClick is { } click) click();
                    else BalloonClicked?.Invoke();
                    break;
            }
            handled = true;
        }
        else if (msg == _taskbarCreated && _taskbarCreated != 0)
        {
            // Explorer restarted: re-add the icon.
            _added = false;
            Show(_icon, _largeIcon, _tooltip);
        }
        else if (msg == Native.WM_HOTKEY)
        {
            Hotkey?.Invoke(wParam.ToInt32());
            handled = true;
        }
        else if (msg == Native.WM_SETTINGCHANGE)
        {
            SettingChanged?.Invoke(lParam != IntPtr.Zero ? Marshal.PtrToStringUni(lParam) : null);
        }
        else if (msg == Native.WM_POWERBROADCAST)
        {
            PowerChanged?.Invoke();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_added)
        {
            var data = NewData(0);
            Native.Shell_NotifyIconW(Native.NIM_DELETE, ref data);
            _added = false;
        }
        _window.Dispose();
    }
}

/// <summary>System-wide hotkeys registered on the tray's message window.</summary>
public sealed class HotkeyManager : IDisposable
{
    private readonly TrayIcon _tray;
    private readonly Dictionary<int, Action> _actions = new();
    private int _nextId = 0x5A00;

    public HotkeyManager(TrayIcon tray)
    {
        _tray = tray;
        _tray.Hotkey += id => { if (_actions.TryGetValue(id, out var a)) a(); };
    }

    public void Clear()
    {
        foreach (var id in _actions.Keys) Native.UnregisterHotKey(_tray.Handle, id);
        _actions.Clear();
    }

    /// <summary>Registers e.g. "Ctrl+Alt+H". Returns an error message on failure (conflict / parse).</summary>
    public string? Register(string gesture, Action action)
    {
        if (string.IsNullOrWhiteSpace(gesture)) return null;
        if (!TryParse(gesture, out var mods, out var vk)) return $"Couldn't understand hotkey “{gesture}”";
        var id = _nextId++;
        if (!Native.RegisterHotKey(_tray.Handle, id, mods | Native.MOD_NOREPEAT, vk))
            return $"“{gesture}” is already used by another app";
        _actions[id] = action;
        return null;
    }

    public static bool TryParse(string gesture, out int modifiers, out int vk)
    {
        modifiers = 0;
        vk = 0;
        foreach (var raw in gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= Native.MOD_CONTROL; break;
                case "alt": modifiers |= Native.MOD_ALT; break;
                case "shift": modifiers |= Native.MOD_SHIFT; break;
                case "win" or "windows": modifiers |= Native.MOD_WIN; break;
                case "space": vk = 0x20; break;
                case "enter": vk = 0x0D; break;
                case var k when k.Length == 1 && char.IsLetterOrDigit(k[0]): vk = char.ToUpperInvariant(k[0]); break;
                case var f when f.StartsWith('f') && int.TryParse(f[1..], out var n) && n is >= 1 and <= 24: vk = 0x70 + n - 1; break;
                default: return false;
            }
        }
        return vk != 0 && modifiers != 0;
    }

    public void Dispose() => Clear();
}
