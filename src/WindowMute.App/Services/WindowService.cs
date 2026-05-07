using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using WindowMute.App.Models;

namespace WindowMute.App.Services;

internal sealed class WindowService
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint ProcessNameWin32 = 0;
    private const uint GaRoot = 2;

    public AppInfoDto? ForegroundApp()
    {
        var hwnd = GetForegroundWindow();
        return AppFromWindow(hwnd);
    }

    public AppInfoDto? AppFromPoint(Point point)
    {
        return AppFromWindow(WindowFromPoint(point));
    }

    public AppInfoDto? AppFromWindow(nint hwnd)
    {
        if (hwnd == 0)
        {
            return null;
        }

        var root = GetAncestor(hwnd, GaRoot);
        if (root == 0)
        {
            root = hwnd;
        }

        var target = ResolveAppWindow(hwnd, root);
        _ = GetWindowThreadProcessId(target, out var pid);
        if (pid == 0)
        {
            return null;
        }

        var exePath = ProcessPath(pid);
        var processName = exePath is null
            ? $"pid-{pid}"
            : Path.GetFileName(exePath);
        var key = (exePath ?? processName).ToLowerInvariant();

        return new AppInfoDto
        {
            Key = key,
            ProcessName = processName,
            ExePath = exePath,
            Pid = pid,
            Title = WindowText(root) ?? WindowText(target),
            ClassName = WindowClass(target)
        };
    }

    public AppInfoDto CurrentProcessApp()
    {
        return ProcessInfo((uint)Environment.ProcessId);
    }

    public AppInfoDto ProcessInfo(uint pid)
    {
        var exePath = ProcessPath(pid);
        var processName = exePath is null
            ? $"pid-{pid}"
            : Path.GetFileName(exePath);

        return new AppInfoDto
        {
            Key = (exePath ?? processName).ToLowerInvariant(),
            ProcessName = processName,
            ExePath = exePath,
            Pid = pid
        };
    }

    public bool IsCurrentProcessApp(AppInfoDto app)
    {
        var current = CurrentProcessApp();
        return (app.Pid != 0 && app.Pid == current.Pid)
            || (!string.IsNullOrWhiteSpace(app.ProcessName) && app.ProcessName.Equals(current.ProcessName, StringComparison.OrdinalIgnoreCase))
            || (app.ExePath is not null && current.ExePath is not null && app.ExePath.Equals(current.ExePath, StringComparison.OrdinalIgnoreCase));
    }

    private nint ResolveAppWindow(nint hwnd, nint root)
    {
        var directPid = WindowProcessId(hwnd);
        var rootPid = WindowProcessId(root);

        if (directPid != 0 && rootPid != 0 && directPid != rootPid)
        {
            return hwnd;
        }

        if (rootPid != 0 && IsWindowHostProcess(rootPid) && FindChildWindowFromDifferentProcess(root, rootPid) is { } child)
        {
            return child;
        }

        return root;
    }

    private static uint WindowProcessId(nint hwnd)
    {
        if (hwnd == 0)
        {
            return 0;
        }

        _ = GetWindowThreadProcessId(hwnd, out var pid);
        return pid;
    }

    private static bool IsWindowHostProcess(uint pid)
    {
        var name = ProcessPath(pid) is { } path ? Path.GetFileName(path).ToLowerInvariant() : "";
        return name is "applicationframehost.exe"
            or "shellexperiencehost.exe"
            or "searchhost.exe"
            or "startmenuexperiencehost.exe";
    }

    private static nint? FindChildWindowFromDifferentProcess(nint root, uint rootPid)
    {
        var state = new ChildWindowSearch(rootPid);
        var handle = GCHandle.Alloc(state);
        try
        {
            _ = EnumChildWindows(root, EnumChildWindowProc, GCHandle.ToIntPtr(handle));
            return state.Selected == 0 ? null : state.Selected;
        }
        finally
        {
            handle.Free();
        }
    }

    private static bool EnumChildWindowProc(nint hwnd, nint lparam)
    {
        var state = (ChildWindowSearch)GCHandle.FromIntPtr(lparam).Target!;
        var pid = WindowProcessId(hwnd);
        if (pid != 0 && pid != state.RootPid && IsWindowVisible(hwnd))
        {
            state.Selected = hwnd;
            return false;
        }

        return true;
    }

    private static string? ProcessPath(uint pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == 0)
        {
            return null;
        }

        try
        {
            var builder = new StringBuilder(32768);
            var length = builder.Capacity;
            return QueryFullProcessImageName(handle, ProcessNameWin32, builder, ref length)
                ? builder.ToString()
                : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static string? WindowText(nint hwnd)
    {
        var length = GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return null;
        }

        var builder = new StringBuilder(length + 1);
        var copied = GetWindowText(hwnd, builder, builder.Capacity);
        return copied <= 0 ? null : builder.ToString();
    }

    private static string? WindowClass(nint hwnd)
    {
        var builder = new StringBuilder(256);
        var copied = GetClassName(hwnd, builder, builder.Capacity);
        return copied <= 0 ? null : builder.ToString();
    }

    private sealed class ChildWindowSearch(uint rootPid)
    {
        public uint RootPid { get; } = rootPid;

        public nint Selected { get; set; }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        public int X;
        public int Y;
    }

    private delegate bool EnumWindowsProc(nint hwnd, nint lparam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint WindowFromPoint(Point point);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetAncestor(nint hwnd, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(nint hwndParent, EnumWindowsProc callback, nint lparam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(nint hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowTextLength(nint hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(nint hwnd, StringBuilder className, int maxCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(nint process, uint flags, StringBuilder exeName, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
