using System.Runtime.InteropServices;
using WindowMute.App.Core;

namespace WindowMute.App.Services;

internal sealed class SelectionService : IDisposable
{
    private const int WhMouseLl = 14;
    private const int WhKeyboardLl = 13;
    private const uint WmLbuttonDown = 0x0201;
    private const uint WmKeyDown = 0x0100;
    private const uint WmQuit = 0x0012;
    private const int VkEscape = 0x1B;
    private const int VkLbutton = 0x01;

    private readonly WindowService _windows;
    private readonly Func<SelectionAction, bool> _isActive;
    private readonly Action<SelectionAction> _cancel;
    private readonly Action<SelectionAction, string> _failed;
    private readonly Action<SelectionAction, string> _timeout;
    private readonly Action<WindowMute.App.Models.AppInfoDto?, SelectionAction, string> _complete;
    private readonly LowLevelMouseProc _mouseProc;
    private readonly LowLevelKeyboardProc _keyboardProc;
    private readonly object _sync = new();
    private Thread? _thread;
    private nint _mouseHook;
    private nint _keyboardHook;
    private uint _threadId;
    private SelectionAction _action;

    public SelectionService(
        WindowService windows,
        Func<SelectionAction, bool> isActive,
        Action<SelectionAction> cancel,
        Action<SelectionAction, string> failed,
        Action<SelectionAction, string> timeout,
        Action<WindowMute.App.Models.AppInfoDto?, SelectionAction, string> complete)
    {
        _windows = windows;
        _isActive = isActive;
        _cancel = cancel;
        _failed = failed;
        _timeout = timeout;
        _complete = complete;
        _mouseProc = OnMouseHook;
        _keyboardProc = OnKeyboardHook;
    }

    public void Start(SelectionAction action)
    {
        lock (_sync)
        {
            StopHooks();
            _action = action;
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "WindowMute selection listener"
            };
            _thread.Start();
        }

        _ = Task.Run(() => PollSelection(action));
    }

    public void Dispose()
    {
        lock (_sync)
        {
            StopHooks();
        }
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseProc, 0, 0);
        _keyboardHook = SetWindowsHookEx(WhKeyboardLl, _keyboardProc, 0, 0);

        if (_mouseHook == 0 || _keyboardHook == 0)
        {
            StopHooks();
            _failed(_action, "Failed to enter selection mode.");
            return;
        }

        while (_isActive(_action) && GetMessage(out var message, 0, 0, 0))
        {
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }

        StopHooks();
    }

    private async Task PollSelection(SelectionAction action)
    {
        await Task.Delay(150).ConfigureAwait(false);
        _ = GetAsyncKeyState(VkLbutton);
        _ = GetAsyncKeyState(VkEscape);
        var started = DateTimeOffset.UtcNow;

        while (_isActive(action))
        {
            if (DateTimeOffset.UtcNow - started > TimeSpan.FromSeconds(15))
            {
                _timeout(action, "Selection mode timed out.");
                StopHooks();
                return;
            }

            if ((GetAsyncKeyState(VkEscape) & 0x0001) != 0)
            {
                _cancel(action);
                StopHooks();
                return;
            }

            if ((GetAsyncKeyState(VkLbutton) & unchecked((short)0x8000)) != 0)
            {
                if (GetCursorPos(out var point))
                {
                    _complete(_windows.AppFromPoint(point), action, "Selection polled click");
                }
                else
                {
                    _complete(null, action, "Selection polled click");
                }

                StopHooks();
                return;
            }

            await Task.Delay(20).ConfigureAwait(false);
        }
    }

    private nint OnMouseHook(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && unchecked((uint)wParam.ToInt64()) == WmLbuttonDown)
        {
            var hook = Marshal.PtrToStructure<MouseHookStruct>(lParam);
            _complete(_windows.AppFromPoint(hook.Point), _action, "Selection mouse hook");
            StopHooks();
        }

        return CallNextHookEx(0, code, wParam, lParam);
    }

    private nint OnKeyboardHook(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && unchecked((uint)wParam.ToInt64()) == WmKeyDown)
        {
            var hook = Marshal.PtrToStructure<KeyboardHookStruct>(lParam);
            if (hook.VirtualKeyCode == VkEscape)
            {
                _cancel(_action);
                StopHooks();
                return 1;
            }
        }

        return CallNextHookEx(0, code, wParam, lParam);
    }

    private void StopHooks()
    {
        if (_mouseHook != 0)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = 0;
        }
        if (_keyboardHook != 0)
        {
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = 0;
        }
        if (_threadId != 0)
        {
            PostThreadMessage(_threadId, WmQuit, 0, 0);
        }
    }

    private delegate nint LowLevelMouseProc(int code, nint wParam, nint lParam);

    private delegate nint LowLevelKeyboardProc(int code, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseHookStruct
    {
        public WindowService.Point Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardHookStruct
    {
        public int VirtualKeyCode;
        public int ScanCode;
        public int Flags;
        public int Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint Hwnd;
        public uint Message;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public int PointX;
        public int PointY;
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int hookId, LowLevelMouseProc callback, nint module, uint threadId);

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int hookId, LowLevelKeyboardProc callback, nint module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMessage(out Msg message, nint hwnd, uint messageFilterMin, uint messageFilterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref Msg message);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessage(ref Msg message);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadId, uint message, nint wParam, nint lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out WindowService.Point point);
}
