using Microsoft.UI.Dispatching;
using System.Runtime.InteropServices;

namespace WindowMute.App.Services;

internal sealed class TrayIconService : IDisposable
{
    private const uint CallbackMessage = 0x8000 + 42;
    private const uint IconId = 1;
    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint WmLButtonDblClk = 0x0203;
    private const uint WmRButtonUp = 0x0205;
    private const uint MfString = 0x00000000;
    private const uint MfSeparator = 0x00000800;
    private const uint TpmReturNcmd = 0x0100;
    private const uint TpmNonotify = 0x0080;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmBottomAlign = 0x0020;
    private const uint ImageIcon = 1;
    private const uint LrLoadFromFile = 0x00000010;
    private const uint LrDefaultSize = 0x00000040;
    private const nuint MenuShow = 1001;
    private const nuint MenuSelectWindow = 1003;
    private const nuint MenuToggleAuto = 1004;
    private const nuint MenuExit = 1005;

    private readonly nint _hwnd;
    private readonly DispatcherQueue _dispatcher;
    private readonly Action _showWindow;
    private readonly Action _enterSelectionMode;
    private readonly Action _toggleAutoMute;
    private readonly Action _exitApp;
    private readonly string? _iconPath;
    private readonly Func<string, string> _translate;
    private readonly SubclassProc _subclassProc;
    private nint _iconHandle;
    private bool _ownsIconHandle;
    private bool _disposed;

    public TrayIconService(
        nint hwnd,
        DispatcherQueue dispatcher,
        Action showWindow,
        Action enterSelectionMode,
        Action toggleAutoMute,
        Action exitApp,
        string? iconPath,
        Func<string, string> translate)
    {
        _hwnd = hwnd;
        _dispatcher = dispatcher;
        _showWindow = showWindow;
        _enterSelectionMode = enterSelectionMode;
        _toggleAutoMute = toggleAutoMute;
        _exitApp = exitApp;
        _iconPath = iconPath;
        _translate = translate;
        _subclassProc = WindowSubclassProc;

        SetWindowSubclass(_hwnd, _subclassProc, IconId, 0);
        UpsertIcon(NimAdd);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        UpsertIcon(NimDelete);
        RemoveWindowSubclass(_hwnd, _subclassProc, IconId);
        if (_ownsIconHandle && _iconHandle != 0)
        {
            DestroyIcon(_iconHandle);
        }
    }

    private nint WindowSubclassProc(nint hwnd, uint message, nint wParam, nint lParam, nuint subclassId, nuint refData)
    {
        if (message == CallbackMessage)
        {
            var mouseMessage = unchecked((uint)lParam.ToInt64());
            if (mouseMessage == WmLButtonDblClk)
            {
                _dispatcher.TryEnqueue(() => _showWindow());
                return 0;
            }

            if (mouseMessage == WmRButtonUp)
            {
                ShowContextMenu();
                return 0;
            }
        }

        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        if (!GetCursorPos(out var point))
        {
            return;
        }

        var menu = CreatePopupMenu();
        if (menu == 0)
        {
            return;
        }

        AppendMenu(menu, MfString, MenuShow, _translate("tray.show"));
        AppendMenu(menu, MfString, MenuSelectWindow, _translate("tray.select_window"));
        AppendMenu(menu, MfString, MenuToggleAuto, _translate("tray.toggle_auto"));
        AppendMenu(menu, MfSeparator, 0, null);
        AppendMenu(menu, MfString, MenuExit, _translate("tray.exit"));

        SetForegroundWindow(_hwnd);
        var command = TrackPopupMenuEx(
            menu,
            TpmReturNcmd | TpmNonotify | TpmRightButton | TpmBottomAlign,
            point.X,
            point.Y,
            _hwnd,
            0);
        DestroyMenu(menu);

        if (command == 0)
        {
            return;
        }

        _dispatcher.TryEnqueue(() =>
        {
            switch ((nuint)command)
            {
                case MenuShow:
                    _showWindow();
                    break;
                case MenuSelectWindow:
                    _enterSelectionMode();
                    break;
                case MenuToggleAuto:
                    _toggleAutoMute();
                    break;
                case MenuExit:
                    _exitApp();
                    break;
            }
        });
    }

    private void UpsertIcon(uint message)
    {
        var data = new NotifyIconData
        {
            cbSize = Marshal.SizeOf<NotifyIconData>(),
            hWnd = _hwnd,
            uID = IconId,
            uFlags = NifMessage | NifIcon | NifTip,
            uCallbackMessage = CallbackMessage,
            hIcon = GetIconHandle(),
            szTip = "WindowMute",
            szInfo = "",
            szInfoTitle = ""
        };

        ShellNotifyIcon(message, ref data);
        if (message == NimAdd)
        {
            ShellNotifyIcon(NimModify, ref data);
        }
    }

    private nint GetIconHandle()
    {
        if (_iconHandle != 0)
        {
            return _iconHandle;
        }

        if (!string.IsNullOrWhiteSpace(_iconPath) && File.Exists(_iconPath))
        {
            _iconHandle = LoadImage(0, _iconPath, ImageIcon, 0, 0, LrLoadFromFile | LrDefaultSize);
            _ownsIconHandle = _iconHandle != 0;
        }

        if (_iconHandle == 0)
        {
            _iconHandle = LoadIcon(0, new nint(32512));
            _ownsIconHandle = false;
        }

        return _iconHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    private delegate nint SubclassProc(nint hwnd, uint message, nint wParam, nint lParam, nuint subclassId, nuint refData);

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", EntryPoint = "LoadIconW", SetLastError = true)]
    private static extern nint LoadIcon(nint instance, nint iconName);

    [DllImport("user32.dll", EntryPoint = "LoadImageW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadImage(nint instance, string name, uint type, int cx, int cy, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint hwnd, SubclassProc subclassProc, nuint subclassId, nuint refData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc subclassProc, nuint subclassId);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern nint DefSubclassProc(nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(nint menu, uint flags, nuint itemId, string? itemText);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(nint menu);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint hwnd, nint parameters);
}
