using System.Windows.Input;
using System.Windows.Interop;
using Launchpad.App.Native;

namespace Launchpad.App.Services;

/// <summary>Owns a message-only window that receives the global hot key.</summary>
public sealed class HotkeyHost : IDisposable
{
    private const int HotkeyId = 0x4C50;
    private readonly HwndSource _source;
    private bool _registered;

    public event Action? Pressed;

    public HotkeyHost()
    {
        var p = new HwndSourceParameters("Launchpad.HotkeyHost") { ParentWindow = new IntPtr(-3) /* HWND_MESSAGE */, Width = 0, Height = 0 };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
    }

    /// <summary>Registers (replacing any previous) shortcut. Returns false if the spec is invalid or already taken.</summary>
    public bool Register(string spec)
    {
        Unregister();
        if (!TryParse(spec, out uint mods, out uint vk)) return false;
        _registered = NativeMethods.RegisterHotKey(_source.Handle, HotkeyId, mods | NativeMethods.MOD_NOREPEAT, vk);
        Diag.Log($"hotkey \"{spec}\" registered={_registered} win32={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
        return _registered;
    }

    public void Unregister()
    {
        if (_registered) NativeMethods.UnregisterHotKey(_source.Handle, HotkeyId);
        _registered = false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            Pressed?.Invoke();
        }
        return IntPtr.Zero;
    }

    public static bool TryParse(string spec, out uint modifiers, out uint vk)
    {
        modifiers = 0; vk = 0;
        if (string.IsNullOrWhiteSpace(spec)) return false;
        foreach (string raw in spec.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": modifiers |= NativeMethods.MOD_CONTROL; continue;
                case "alt": modifiers |= NativeMethods.MOD_ALT; continue;
                case "shift": modifiers |= NativeMethods.MOD_SHIFT; continue;
                case "win": case "windows": case "meta": modifiers |= NativeMethods.MOD_WIN; continue;
            }
            string name = raw.Length == 1 && char.IsDigit(raw[0]) ? "D" + raw : raw;
            if (!Enum.TryParse<Key>(name, true, out var key) || key == Key.None) return false;
            vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        }
        return vk != 0;
    }

    /// <summary>Formats a key press for display / storage ("Ctrl+Alt+Space").</summary>
    public static string Format(ModifierKeys mods, Key key)
    {
        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        string k = key.ToString();
        if (k.Length == 2 && k[0] == 'D' && char.IsDigit(k[1])) k = k[1..];
        parts.Add(k);
        return string.Join("+", parts);
    }

    public void Dispose()
    {
        Unregister();
        _source.Dispose();
    }
}
