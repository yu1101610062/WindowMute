using System.ComponentModel;
using System.Runtime.InteropServices;
using WindowMute.App.Core;

namespace WindowMute.App.Services;

internal sealed class HotkeyService : IDisposable
{
    private const int HotkeyToggleForeground = 0x4D01;
    private const uint WmHotkey = 0x0312;
    private const nuint SubclassId = 2;

    private readonly nint _hwnd;
    private readonly Action _onToggleForeground;
    private readonly SubclassProc _subclassProc;
    private bool _disposed;
    private HotkeyConfig? _registered;

    public HotkeyService(nint hwnd, Action onToggleForeground)
    {
        _hwnd = hwnd;
        _onToggleForeground = onToggleForeground;
        _subclassProc = WindowSubclassProc;
        SetWindowSubclass(_hwnd, _subclassProc, SubclassId, 0);
    }

    public string? Register(HotkeyConfig hotkey)
    {
        UnregisterCurrent();
        if (RegisterHotKey(_hwnd, HotkeyToggleForeground, hotkey.ModifierFlags(), hotkey.VirtualKey()))
        {
            _registered = hotkey;
            return null;
        }

        return new Win32Exception(Marshal.GetLastWin32Error()).Message;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        UnregisterCurrent();
        RemoveWindowSubclass(_hwnd, _subclassProc, SubclassId);
    }

    private void UnregisterCurrent()
    {
        if (_registered is null)
        {
            return;
        }

        UnregisterHotKey(_hwnd, HotkeyToggleForeground);
        _registered = null;
    }

    private nint WindowSubclassProc(nint hwnd, uint message, nint wParam, nint lParam, nuint subclassId, nuint refData)
    {
        if (message == WmHotkey && wParam.ToInt32() == HotkeyToggleForeground)
        {
            _onToggleForeground();
            return 0;
        }

        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    private delegate nint SubclassProc(nint hwnd, uint message, nint wParam, nint lParam, nuint subclassId, nuint refData);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint hwnd, int id);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint hwnd, SubclassProc subclassProc, nuint subclassId, nuint refData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc subclassProc, nuint subclassId);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern nint DefSubclassProc(nint hwnd, uint message, nint wParam, nint lParam);
}
