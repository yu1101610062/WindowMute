using System.Runtime.InteropServices;

namespace WindowMute.App.Services;

internal sealed class AutoMuteService : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint WinEventOutOfContext = 0x0000;
    private const uint WinEventSkipOwnProcess = 0x0002;
    private const uint WmQuit = 0x0012;

    private readonly Action _onForegroundChanged;
    private readonly WinEventProc _callback;
    private Thread? _thread;
    private nint _hook;
    private uint _threadId;
    private bool _disposed;

    public AutoMuteService(Action onForegroundChanged)
    {
        _onForegroundChanged = onForegroundChanged;
        _callback = OnWinEvent;
    }

    public void Start()
    {
        if (_thread is not null)
        {
            return;
        }

        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "WindowMute foreground listener"
        };
        _thread.Start();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_threadId != 0)
        {
            PostThreadMessage(_threadId, WmQuit, 0, 0);
        }
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        _hook = SetWinEventHook(
            EventSystemForeground,
            EventSystemForeground,
            0,
            _callback,
            0,
            0,
            WinEventOutOfContext | WinEventSkipOwnProcess);

        if (_hook == 0)
        {
            return;
        }

        while (!_disposed && GetMessage(out var message, 0, 0, 0))
        {
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }

        UnhookWinEvent(_hook);
        _hook = 0;
    }

    private void OnWinEvent(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint eventThread, uint eventTime)
    {
        _onForegroundChanged();
    }

    private delegate void WinEventProc(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint eventThread, uint eventTime);

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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint module, WinEventProc callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(nint hook);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadId, uint message, nint wParam, nint lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMessage(out Msg message, nint hwnd, uint messageFilterMin, uint messageFilterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref Msg message);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessage(ref Msg message);
}
