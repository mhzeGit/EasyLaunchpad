using System.Runtime.InteropServices;
using Launchpad.Core.Input;

namespace Launchpad.App.Services;

/// <summary>
/// Makes a lone tap of the Windows key raise <see cref="Tapped"/> and keeps the Start menu from opening.
/// Anything else is left alone: if another key goes down while Win is held (Win+E, Win+D, Win+Shift+S, ...)
/// the press is a shortcut and passes through untouched.
///
/// Implemented with a low-level keyboard hook, so it must live on a thread that pumps messages (the UI thread) and the
/// callback must stay tiny. Limitation: Windows doesn't deliver input to a non-elevated hook while an elevated
/// window has focus, so there the Start menu behaves as usual.
/// </summary>
public sealed class WinKeyHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    private const uint LLKHF_INJECTED = 0x10;
    private const ushort VK_UNASSIGNED = 0xE8;   // a key that does nothing, used to cancel the Start menu

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdHook { public uint VkCode, ScanCode, Flags, Time; public IntPtr ExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput { public int Dx, Dy; public uint MouseData, Flags, Time; public IntPtr ExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeybdInput { public ushort Vk, Scan; public uint Flags, Time; public IntPtr ExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeybdInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input { public uint Type; public InputUnion U; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);

    private readonly HookProc _proc;      // kept in a field so the delegate isn't garbage collected while hooked
    private IntPtr _hook;
    private readonly WinKeyTapDetector _detector = new();

    /// <summary>A lone Windows-key tap happened (raised on the hook's thread; keep handlers quick or hand off).</summary>
    public event Action? Tapped;

    public WinKeyHook() => _proc = Callback;

    public bool IsActive => _hook != IntPtr.Zero;

    public bool Start()
    {
        if (_hook != IntPtr.Zero) return true;
        _detector.Reset();
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        Diag.Log($"win-key hook installed={_hook != IntPtr.Zero} win32={Marshal.GetLastWin32Error()}");
        return _hook != IntPtr.Zero;
    }

    public void Stop()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _detector.Reset();
    }

    private IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var k = Marshal.PtrToStructure<KbdHook>(lParam);
            if ((k.Flags & LLKHF_INJECTED) == 0)   // ignore our own synthetic key
            {
                int msg = wParam.ToInt32();
                bool down = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
                bool up = msg is WM_KEYUP or WM_SYSKEYUP;

                if (_detector.Feed((int)k.VkCode, down, up))
                {
                    // A key press between Win down and Win up makes Windows treat it as a shortcut, not a Start tap.
                    CancelStartMenu();
                    Tapped?.Invoke();
                }
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    private static void CancelStartMenu()
    {
        var inputs = new Input[2];
        inputs[0].Type = 1; inputs[0].U.Keyboard = new KeybdInput { Vk = VK_UNASSIGNED };
        inputs[1].Type = 1; inputs[1].U.Keyboard = new KeybdInput { Vk = VK_UNASSIGNED, Flags = 0x0002 /* KEYEVENTF_KEYUP */ };
        SendInput(2, inputs, Marshal.SizeOf<Input>());
    }

    public void Dispose() => Stop();
}
