using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;
using WindowMute.App.Core;
using WindowMute.App.Services;

namespace WindowMute.App;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\WindowMute.SingleInstance";
    private const string ShowWindowEventName = @"Local\WindowMute.ShowWindow";
    private const int SwRestore = 9;

    private static Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showWindowEvent;
    private MainWindow? _window;

    public App()
    {
        AppDiagnostics.Log("App constructor");
        InitializeComponent();
        UnhandledException += (_, args) =>
        {
            AppDiagnostics.Log($"Unhandled XAML exception: {args.Exception}");
        };
        AppDiagnostics.Log("App constructor complete");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        AppDiagnostics.Log("OnLaunched");
        var startInTray = LaunchOptions.ShouldStartInTray(Environment.GetCommandLineArgs().Skip(1));
        new StartupService().ReconcileOnLaunch();

        _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            AppDiagnostics.Log("Existing WindowMute instance detected");
            if (!startInTray)
            {
                SignalExistingInstance();
            }

            Exit();
            return;
        }

        StartShowWindowListener();
        _window = new MainWindow();
        if (startInTray)
        {
            _window.StartHiddenToTray();
            AppDiagnostics.Log("MainWindow started hidden to tray");
        }
        else
        {
            _window.Activate();
            AppDiagnostics.Log("MainWindow activated");
        }
    }

    private void StartShowWindowListener()
    {
        _showWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);
        var showWindowEvent = _showWindowEvent;
        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    showWindowEvent.WaitOne();
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                var window = _window;
                if (window is null)
                {
                    continue;
                }

                _ = window.DispatcherQueue.TryEnqueue(window.ShowFromExternalActivation);
            }
        })
        {
            IsBackground = true,
            Name = "WindowMute activation listener"
        };
        thread.Start();
    }

    private static void SignalExistingInstance()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                using var showWindowEvent = EventWaitHandle.OpenExisting(ShowWindowEventName);
                showWindowEvent.Set();
                return;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                Thread.Sleep(100);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }

        TryShowExistingWindowByTitle();
    }

    private static void TryShowExistingWindowByTitle()
    {
        var hwnd = FindWindow(null, "WindowMute");
        if (hwnd == 0)
        {
            return;
        }

        ShowWindow(hwnd, SwRestore);
        SetForegroundWindow(hwnd);
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hwnd, int commandShow);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hwnd);
}
