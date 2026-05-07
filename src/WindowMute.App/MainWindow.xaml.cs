using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Text;
using Windows.System;
using Windows.Graphics;
using WinRT.Interop;
using WindowMute.App.Models;
using WindowMute.App.Services;

namespace WindowMute.App;

public sealed partial class MainWindow : Window
{
    private const string AppIconRelativePath = "Assets\\AppIcon.ico";
    private const int VersionPollMilliseconds = 150;
    private const int SafetySnapshotPollTicks = 7;

    private readonly WindowMuteCoreService _core;
    private readonly DispatcherTimer _refreshTimer;
    private readonly TrayIconService _tray;
    private readonly UiSettingsService _uiSettings;
    private readonly LocalizationService _loc;
    private readonly nint _hwnd;
    private readonly AppWindow _appWindow;
    private SnapshotDto _snapshot = new();
    private string _currentPage = "dashboard";
    private string? _lastPageSignature;
    private string? _lastStatusSignature;
    private ulong _knownCoreVersion;
    private int _snapshotPollTicks;
    private bool _isRendering;
    private bool _allowClose;
    private bool _disposed;
    private bool _handledStartupHotkeyConflict;
    private string? _startupHotkeyConflictMessage;

    public MainWindow()
    {
        InitializeComponent();
        _uiSettings = new UiSettingsService();
        _loc = new LocalizationService(_uiSettings);
        Title = _loc.T("app.title");

        _hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        ConfigureWindow();
        ApplyTheme();
        ApplyLanguage();

        _core = new WindowMuteCoreService(_hwnd);
        _core.StateChanged += Core_StateChanged;
        _core.Start();

        var appIconPath = GetAppIconPath();
        _tray = new TrayIconService(
            _hwnd,
            DispatcherQueue,
            ShowWindowFromTray,
            EnterSelectionMode,
            ToggleAutoMute,
            ExitApplication,
            appIconPath,
            key => _loc.T(key));

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(VersionPollMilliseconds) };
        _refreshTimer.Tick += (_, _) => PollCoreState();
        _refreshTimer.Start();

        RootNav.SelectedItem = RootNav.MenuItems[0];
        RefreshSnapshot();
    }

    private void ConfigureWindow()
    {
        try
        {
            SystemBackdrop = new MicaBackdrop();
        }
        catch
        {
            // Mica is unavailable on older Windows builds.
        }

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        ConfigureTitleBarButtons();
        _appWindow.Title = _loc.T("app.title");
        var appIconPath = GetAppIconPath();
        if (appIconPath is not null)
        {
            _appWindow.SetIcon(appIconPath);
        }

        _appWindow.Resize(new SizeInt32(1040, 720));
        _appWindow.Closing += (_, args) =>
        {
            if (_allowClose)
            {
                return;
            }

            args.Cancel = true;
            HideToTray();
        };
    }

    private static string? GetAppIconPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, AppIconRelativePath);
        return File.Exists(path) ? path : null;
    }

    private void ApplyLanguage()
    {
        _loc.ApplyThreadCulture();
        Title = _loc.T("app.title");
        AppTitleText.Text = _loc.T("app.title");
        _appWindow.Title = _loc.T("app.title");
        RootNav.PaneTitle = "";
        DashboardNavItem.Content = _loc.T("nav.dashboard");
        MutedNavItem.Content = _loc.T("nav.muted_apps");
        WhitelistNavItem.Content = _loc.T("nav.whitelist");
        SessionsNavItem.Content = _loc.T("nav.sessions");
        SettingsNavItem.Content = _loc.T("nav.settings");
    }

    private void ApplyTheme()
    {
        RootGrid.RequestedTheme = _uiSettings.Current.Theme switch
        {
            UiSettingsService.LightTheme => ElementTheme.Light,
            UiSettingsService.DarkTheme => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
    }

    private void RootNav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
        {
            _currentPage = tag;
        }

        RenderCurrentPage(force: true);
    }

    private void PollCoreState()
    {
        if (_disposed)
        {
            return;
        }

        _snapshotPollTicks++;
        if (_snapshotPollTicks >= SafetySnapshotPollTicks)
        {
            RefreshSnapshot();
            return;
        }

        var version = _core.GetStateVersion();
        if (version != _knownCoreVersion)
        {
            RefreshSnapshot();
        }
    }

    private void RefreshSnapshot()
    {
        try
        {
            _snapshot = _core.GetSnapshot();
            _knownCoreVersion = _snapshot.Version;
            ClearResolvedHotkeyConflict();
            HandleStartupHotkeyConflict();
        }
        catch (Exception error)
        {
            ShowError(error.Message);
            return;
        }

        _snapshotPollTicks = 0;
        RenderCurrentPage();
    }

    private void Core_StateChanged(object? sender, EventArgs args)
    {
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (!_disposed)
            {
                RefreshSnapshot();
            }
        });
    }

    private void RenderCurrentPage(bool force = false)
    {
        UpdateStatusBar(force);

        var signature = BuildPageSignature();
        if (!force && signature == _lastPageSignature)
        {
            return;
        }

        _lastPageSignature = signature;
        _isRendering = true;
        try
        {
            PageHost.Children.Clear();
            switch (_currentPage)
            {
                case "muted":
                    RenderMutedApps();
                    break;
                case "whitelist":
                    RenderWhitelist();
                    break;
                case "sessions":
                    RenderSessions();
                    break;
                case "settings":
                    RenderSettings();
                    break;
                default:
                    RenderDashboard();
                    break;
            }
        }
        finally
        {
            _isRendering = false;
        }
    }

    private void UpdateStatusBar(bool force)
    {
        var latest = _snapshot.Messages.LastOrDefault();
        if (_startupHotkeyConflictMessage is not null)
        {
            latest = _startupHotkeyConflictMessage;
        }
        var hasMessage = !string.IsNullOrWhiteSpace(latest);
        if (!_snapshot.SelectionActive && !hasMessage)
        {
            const string closedSignature = "closed";
            if (!force && _lastStatusSignature == closedSignature)
            {
                return;
            }

            _lastStatusSignature = closedSignature;
            StatusInfoBar.IsOpen = false;
            return;
        }

        var severity = _startupHotkeyConflictMessage is not null
            ? InfoBarSeverity.Warning
            : _snapshot.SelectionActive ? InfoBarSeverity.Warning : InfoBarSeverity.Informational;
        var title = _snapshot.SelectionActive
            ? _loc.T("status.selection.title")
            : _startupHotkeyConflictMessage is not null ? _loc.T("settings.shortcuts.title") : _loc.T("app.title");
        var message = _snapshot.SelectionActive ? SelectionStatusMessage() : _loc.CoreMessage(latest ?? "");
        var signature = $"open|{severity}|{title}|{message}";
        if (!force && signature == _lastStatusSignature)
        {
            return;
        }

        _lastStatusSignature = signature;
        StatusInfoBar.IsOpen = true;
        StatusInfoBar.Severity = severity;
        StatusInfoBar.Title = title;
        StatusInfoBar.Message = message;
        StatusInfoBar.Background = TryBrush("CardBackgroundFillColorDefaultBrush");
        StatusInfoBar.BorderBrush = TryBrush("CardStrokeColorDefaultBrush");
        StatusInfoBar.BorderThickness = new Thickness(1);
    }

    private void ConfigureTitleBarButtons()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        var titleBar = _appWindow.TitleBar;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonHoverBackgroundColor = Colors.Transparent;
        titleBar.ButtonPressedBackgroundColor = Colors.Transparent;
    }

    private void HandleStartupHotkeyConflict()
    {
        if (_handledStartupHotkeyConflict)
        {
            return;
        }

        var conflict = _snapshot.Messages.FirstOrDefault(IsHotkeyRegistrationFailure);
        if (conflict is null)
        {
            return;
        }

        _handledStartupHotkeyConflict = true;
        _startupHotkeyConflictMessage = conflict;
        _currentPage = "settings";
        RootNav.SelectedItem = SettingsNavItem;
    }

    private void ClearResolvedHotkeyConflict()
    {
        if (_startupHotkeyConflictMessage is null)
        {
            return;
        }

        if (_snapshot.Messages.Any(message =>
                message.Contains("Hotkey updated", StringComparison.OrdinalIgnoreCase)))
        {
            _startupHotkeyConflictMessage = null;
            _lastStatusSignature = null;
        }
    }

    private static bool IsHotkeyRegistrationFailure(string message)
    {
        return message.Contains("Failed to register", StringComparison.OrdinalIgnoreCase)
            && message.Contains("shortcut", StringComparison.OrdinalIgnoreCase);
    }

    private string SelectionStatusMessage()
    {
        return _snapshot.SelectionMode == "add_whitelist"
            ? _loc.T("status.selection.whitelist.message")
            : _loc.T("status.selection.message");
    }

    private string BuildPageSignature()
    {
        var builder = new StringBuilder();
        builder
            .Append(_currentPage).Append('|')
            .Append(_uiSettings.Current.Language).Append('|')
            .Append(_loc.ResolvedLanguage).Append('|')
            .Append(_uiSettings.Current.Theme).Append('|');

        switch (_currentPage)
        {
            case "muted":
                foreach (var app in _snapshot.MutedApps.OrderBy(app => app.Key, StringComparer.OrdinalIgnoreCase))
                {
                    AppendAppSignature(builder, app);
                }
                break;
            case "whitelist":
                AppendAppSignature(builder, _snapshot.Foreground);
                foreach (var process in _snapshot.Whitelist.OrderBy(process => process, StringComparer.OrdinalIgnoreCase))
                {
                    builder.Append(process).Append(';');
                }
                break;
            case "sessions":
                foreach (var session in _snapshot.Sessions.OrderBy(session => session.SessionId, StringComparer.OrdinalIgnoreCase))
                {
                    builder
                        .Append(session.SessionId).Append(';')
                        .Append(session.DisplayName).Append(';')
                        .Append(Math.Round(session.Volume * 100)).Append(';')
                        .Append(session.Muted).Append(';')
                        .Append(session.ManualMuted).Append(';')
                        .Append(session.AutoMuted).Append(';')
                        .Append(session.Whitelisted).Append(';')
                        .Append(session.IsForeground).Append(';')
                        .Append(session.EffectiveReason).Append(';');
                    AppendAppSignature(builder, session.App);
                }
                break;
            case "settings":
                builder
                    .Append(_uiSettings.Current.Language).Append(';')
                    .Append(_uiSettings.Current.Theme).Append(';')
                    .Append(_snapshot.Hotkeys.ToggleForeground);
                break;
            default:
                AppendAppSignature(builder, _snapshot.Foreground);
                builder
                    .Append(_snapshot.AutoEnabled).Append(';')
                    .Append(_snapshot.Sessions.Count).Append(';')
                    .Append(_snapshot.MutedApps.Count).Append(';');
                break;
        }

        return builder.ToString();
    }

    private static void AppendAppSignature(StringBuilder builder, AppInfoDto? app)
    {
        if (app is null)
        {
            builder.Append("<none>;");
            return;
        }

        builder
            .Append(app.Key).Append(';')
            .Append(app.ProcessName).Append(';')
            .Append(app.Pid).Append(';')
            .Append(app.Title).Append(';')
            .Append(app.ClassName).Append(';');
    }

    private void RenderDashboard()
    {
        PageHost.Children.Add(PageTitle(_loc.T("dashboard.title"), _loc.T("dashboard.subtitle")));
        PageHost.Children.Add(BuildCommandBar());

        var foreground = _snapshot.Foreground is null
            ? _loc.T("dashboard.foreground.none")
            : $"{_snapshot.Foreground.DisplayTitle} - {_snapshot.Foreground.ProcessName}";
        PageHost.Children.Add(InfoPanel(_loc.T("dashboard.foreground.title"), foreground, "\uE8B7"));

        var autoSwitch = new ToggleSwitch
        {
            Header = _loc.T("dashboard.auto.header"),
            OnContent = _loc.T("dashboard.auto.on"),
            OffContent = _loc.T("dashboard.auto.off"),
            IsOn = _snapshot.AutoEnabled
        };
        autoSwitch.Toggled += (_, _) =>
        {
            if (!_isRendering)
            {
                RunCoreAndRefresh(() => _core.ToggleAutoMute(autoSwitch.IsOn));
            }
        };
        PageHost.Children.Add(WrapCard(autoSwitch));

        var metrics = new Grid { ColumnSpacing = 12 };
        metrics.ColumnDefinitions.Add(new ColumnDefinition());
        metrics.ColumnDefinitions.Add(new ColumnDefinition());
        metrics.Children.Add(MetricCard(_loc.T("dashboard.metric.sessions"), _snapshot.Sessions.Count.ToString(), "\uE995", 0));
        metrics.Children.Add(MetricCard(_loc.T("dashboard.metric.muted"), _snapshot.MutedApps.Count.ToString(), "\uE74F", 1));
        PageHost.Children.Add(metrics);
    }

    private CommandBar BuildCommandBar()
    {
        var bar = new CommandBar
        {
            DefaultLabelPosition = CommandBarDefaultLabelPosition.Right,
            Background = new SolidColorBrush(Colors.Transparent)
        };
        bar.PrimaryCommands.Add(CommandButton(_loc.T("command.select_window"), "\uE8B7", EnterSelectionMode));
        bar.PrimaryCommands.Add(CommandButton(_loc.T("command.refresh"), "\uE72C", RefreshSnapshot));
        return bar;
    }

    private AppBarButton CommandButton(string label, string glyph, Action action)
    {
        var button = new AppBarButton
        {
            Label = label,
            Icon = new FontIcon { Glyph = glyph }
        };
        button.Click += (_, _) => action();
        return button;
    }

    private void RenderMutedApps()
    {
        PageHost.Children.Add(PageTitle(_loc.T("muted.title"), _loc.T("muted.subtitle")));
        PageHost.Children.Add(BuildCommandBar());

        if (_snapshot.MutedApps.Count == 0)
        {
            PageHost.Children.Add(EmptyState(_loc.T("muted.empty")));
            return;
        }

        foreach (var app in _snapshot.MutedApps)
        {
            PageHost.Children.Add(AppRow(app, _loc.T("muted.status"), () => RunCoreAndRefresh(() => _core.SetAppMute(app.Key, false))));
        }
    }

    private void RenderWhitelist()
    {
        PageHost.Children.Add(PageTitle(_loc.T("whitelist.title"), _loc.T("whitelist.subtitle")));

        var selectWindow = new Button
        {
            Content = _loc.T("whitelist.select_window"),
            Style = TryStyle("AccentButtonStyle")
        };
        selectWindow.Click += (_, _) => EnterWhitelistSelectionMode();
        PageHost.Children.Add(WrapCard(selectWindow));

        if (_snapshot.Whitelist.Count == 0)
        {
            PageHost.Children.Add(EmptyState(_loc.T("whitelist.empty")));
            return;
        }

        foreach (var process in _snapshot.Whitelist)
        {
            PageHost.Children.Add(WhitelistRow(process));
        }
    }

    private void RenderSessions()
    {
        PageHost.Children.Add(PageTitle(_loc.T("sessions.title"), _loc.T("sessions.subtitle")));
        PageHost.Children.Add(BuildCommandBar());

        if (_snapshot.Sessions.Count == 0)
        {
            PageHost.Children.Add(EmptyState(_loc.T("sessions.empty")));
            return;
        }

        foreach (var session in _snapshot.Sessions.OrderByDescending(s => s.IsForeground).ThenBy(s => s.DisplayName))
        {
            PageHost.Children.Add(SessionRow(session));
        }
    }

    private void RenderSettings()
    {
        PageHost.Children.Add(PageTitle(_loc.T("settings.title"), _loc.T("settings.subtitle")));
        PageHost.Children.Add(LanguageSettingPanel());
        PageHost.Children.Add(ThemeSettingPanel());
        PageHost.Children.Add(HotkeySettingPanel());
        PageHost.Children.Add(InfoPanel(_loc.T("settings.close.title"), _loc.T("settings.close.message"), "\uE711"));
        PageHost.Children.Add(InfoPanel(_loc.T("settings.configuration.title"), _loc.T("settings.configuration.message"), "\uE8A5"));
        PageHost.Children.Add(InfoPanel(_loc.T("settings.audio.title"), _loc.T("settings.audio.message"), "\uE995"));
    }

    private FrameworkElement LanguageSettingPanel()
    {
        var options = new List<LanguageOption>
        {
            new(LocalizationService.SystemLanguage, _loc.T("language.system")),
            new(LocalizationService.English, _loc.T("language.en")),
            new(LocalizationService.SimplifiedChinese, _loc.T("language.zh"))
        };

        var combo = new ComboBox
        {
            Header = _loc.T("settings.language.title"),
            ItemsSource = options,
            DisplayMemberPath = nameof(LanguageOption.DisplayName),
            MinWidth = 260,
            SelectedItem = options.First(option => option.Code == _uiSettings.Current.Language)
        };

        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is not LanguageOption option || option.Code == _uiSettings.Current.Language)
            {
                return;
            }

            _uiSettings.SetLanguage(option.Code);
            ApplyLanguage();
            RenderCurrentPage(force: true);
        };

        var panel = new Grid { ColumnSpacing = 16 };
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { Spacing = 3 };
        text.Children.Add(new TextBlock
        {
            Text = _loc.T("settings.language.title"),
            Style = TryStyle("BodyStrongTextBlockStyle")
        });
        text.Children.Add(new TextBlock
        {
            Text = _loc.T("settings.language.description"),
            Opacity = 0.72,
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(text);

        Grid.SetColumn(combo, 1);
        panel.Children.Add(combo);
        return WrapCard(panel);
    }

    private FrameworkElement ThemeSettingPanel()
    {
        var options = new List<ThemeOption>
        {
            new(UiSettingsService.SystemTheme, _loc.T("theme.system")),
            new(UiSettingsService.LightTheme, _loc.T("theme.light")),
            new(UiSettingsService.DarkTheme, _loc.T("theme.dark"))
        };

        var combo = new ComboBox
        {
            Header = _loc.T("settings.theme.title"),
            ItemsSource = options,
            DisplayMemberPath = nameof(ThemeOption.DisplayName),
            MinWidth = 260,
            SelectedItem = options.First(option => option.Code == _uiSettings.Current.Theme)
        };

        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is not ThemeOption option || option.Code == _uiSettings.Current.Theme)
            {
                return;
            }

            _uiSettings.SetTheme(option.Code);
            ApplyTheme();
            RenderCurrentPage(force: true);
        };

        var panel = new Grid { ColumnSpacing = 16 };
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { Spacing = 3 };
        text.Children.Add(new TextBlock
        {
            Text = _loc.T("settings.theme.title"),
            Style = TryStyle("BodyStrongTextBlockStyle")
        });
        text.Children.Add(new TextBlock
        {
            Text = _loc.T("settings.theme.description"),
            Opacity = 0.72,
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(text);

        Grid.SetColumn(combo, 1);
        panel.Children.Add(combo);
        return WrapCard(panel);
    }

    private FrameworkElement HotkeySettingPanel()
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = _loc.T("settings.shortcuts.title"),
            Style = TryStyle("BodyStrongTextBlockStyle")
        });
        panel.Children.Add(new TextBlock
        {
            Text = _loc.T("settings.shortcuts.message"),
            Opacity = 0.72,
            TextWrapping = TextWrapping.Wrap
        });

        panel.Children.Add(HotkeyRow(
            _loc.T("settings.shortcuts.toggle"),
            _snapshot.Hotkeys.ToggleForeground,
            "toggle_foreground"));

        return WrapCard(panel);
    }

    private FrameworkElement HotkeyRow(string label, string value, string action)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center
        });

        var textBox = new TextBox
        {
            Text = value,
            MinWidth = 220,
            IsReadOnly = true,
            PlaceholderText = value
        };
        textBox.GotFocus += (_, _) => textBox.SelectAll();
        textBox.KeyDown += (_, args) =>
        {
            var hotkey = FormatPressedHotkey(args.Key);
            if (hotkey is null)
            {
                return;
            }

            textBox.Text = hotkey;
            args.Handled = true;
        };
        Grid.SetColumn(textBox, 1);
        grid.Children.Add(textBox);

        var apply = new Button
        {
            Content = _loc.T("settings.shortcuts.apply"),
            Style = TryStyle("AccentButtonStyle")
        };
        apply.Click += (_, _) => RunCoreAndRefresh(() => _core.SetHotkey(action, textBox.Text));
        Grid.SetColumn(apply, 2);
        grid.Children.Add(apply);

        return grid;
    }

    private static string? FormatPressedHotkey(VirtualKey key)
    {
        if (key is VirtualKey.Control or VirtualKey.Menu or VirtualKey.Shift
            or VirtualKey.LeftWindows or VirtualKey.RightWindows)
        {
            return null;
        }

        var keyName = HotkeyKeyName(key);
        if (keyName is null)
        {
            return null;
        }

        var parts = new List<string>();
        if (IsKeyDown(0x11))
        {
            parts.Add("Ctrl");
        }
        if (IsKeyDown(0x12))
        {
            parts.Add("Alt");
        }
        if (IsKeyDown(0x10))
        {
            parts.Add("Shift");
        }
        if (IsKeyDown(0x5B) || IsKeyDown(0x5C))
        {
            parts.Add("Win");
        }

        parts.Add(keyName);
        return string.Join("+", parts);
    }

    private static string? HotkeyKeyName(VirtualKey key)
    {
        var code = (int)key;
        if (code is >= 0x41 and <= 0x5A)
        {
            return ((char)code).ToString();
        }
        if (code is >= 0x30 and <= 0x39)
        {
            return ((char)code).ToString();
        }
        if (code is >= 0x70 and <= 0x87)
        {
            return $"F{code - 0x6F}";
        }

        return null;
    }

    private FrameworkElement PageTitle(string title, string subtitle)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            Style = TryStyle("TitleTextBlockStyle")
        });
        panel.Children.Add(new TextBlock
        {
            Text = subtitle,
            TextWrapping = TextWrapping.Wrap,
            Style = TryStyle("BodyTextBlockStyle"),
            Opacity = 0.76
        });
        return panel;
    }

    private FrameworkElement InfoPanel(string title, string value, string glyph)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12
        };
        panel.Children.Add(new FontIcon
        {
            Glyph = glyph,
            FontSize = 22,
            Width = 32,
            Height = 32
        });
        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock
        {
            Text = title,
            Style = TryStyle("CaptionTextBlockStyle"),
            Opacity = 0.72
        });
        text.Children.Add(new TextBlock
        {
            Text = value,
            TextWrapping = TextWrapping.Wrap,
            Style = TryStyle("BodyStrongTextBlockStyle")
        });
        panel.Children.Add(text);
        return WrapCard(panel);
    }

    private FrameworkElement MetricCard(string label, string value, string glyph, int column)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new FontIcon { Glyph = glyph, FontSize = 20, HorizontalAlignment = HorizontalAlignment.Left });
        panel.Children.Add(new TextBlock { Text = value, FontSize = 28, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = label, Opacity = 0.72 });
        var card = WrapCard(panel);
        Grid.SetColumn(card, column);
        return card;
    }

    private FrameworkElement SessionRow(SessionInfoDto session)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { Spacing = 3 };
        text.Children.Add(new TextBlock
        {
            Text = session.DisplayName,
            Style = TryStyle("BodyStrongTextBlockStyle")
        });
        text.Children.Add(new TextBlock
        {
            Text = $"{session.App.ProcessName} - {_loc.T("sessions.pid")} {session.App.Pid}" + (session.EffectiveReason is null ? "" : $" - {_loc.SessionReason(session.EffectiveReason)}"),
            Opacity = 0.72,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        grid.Children.Add(text);

        var slider = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            Value = Math.Round(session.Volume * 100),
            Width = 180,
            VerticalAlignment = VerticalAlignment.Center
        };
        slider.ValueChanged += (_, args) =>
        {
            if (!_isRendering && Math.Abs(args.NewValue - args.OldValue) >= 1)
            {
                RunCoreAndRefresh(() => _core.SetAppVolume(session.App.Key, (float)(args.NewValue / 100.0)));
            }
        };
        Grid.SetColumn(slider, 1);
        grid.Children.Add(slider);

        var mute = new ToggleSwitch
        {
            IsOn = session.ManualMuted || session.Muted,
            OnContent = _loc.T("session.muted"),
            OffContent = _loc.T("session.sound"),
            MinWidth = 96
        };
        mute.Toggled += (_, _) =>
        {
            if (!_isRendering)
            {
                RunCoreAndRefresh(() => _core.SetAppMute(session.App.Key, mute.IsOn));
            }
        };
        Grid.SetColumn(mute, 2);
        grid.Children.Add(mute);

        return WrapCard(grid);
    }

    private FrameworkElement AppRow(AppInfoDto app, string label, Action action)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { Spacing = 3 };
        text.Children.Add(new TextBlock { Text = app.DisplayTitle, Style = TryStyle("BodyStrongTextBlockStyle") });
        text.Children.Add(new TextBlock { Text = $"{app.ProcessName} - {label}", Opacity = 0.72 });
        grid.Children.Add(text);
        var button = new Button { Content = _loc.T("button.unmute") };
        button.Click += (_, _) => action();
        Grid.SetColumn(button, 1);
        grid.Children.Add(button);
        return WrapCard(grid);
    }

    private FrameworkElement WhitelistRow(string process)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock
        {
            Text = process,
            VerticalAlignment = VerticalAlignment.Center,
            Style = TryStyle("BodyStrongTextBlockStyle")
        });
        var remove = new Button { Content = _loc.T("button.remove") };
        remove.Click += (_, _) => RunCoreAndRefresh(() => _core.RemoveWhitelist(process));
        Grid.SetColumn(remove, 1);
        grid.Children.Add(remove);
        return WrapCard(grid);
    }

    private FrameworkElement EmptyState(string text)
    {
        var panel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(32),
            Spacing = 8
        };
        panel.Children.Add(new FontIcon { Glyph = "\uE946", FontSize = 28 });
        panel.Children.Add(new TextBlock { Text = text, Opacity = 0.72, TextAlignment = TextAlignment.Center });
        return WrapCard(panel);
    }

    private Border WrapCard(UIElement child)
    {
        return new Border
        {
            Child = child,
            Padding = new Thickness(16),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = TryBrush("CardStrokeColorDefaultBrush"),
            Background = TryBrush("CardBackgroundFillColorDefaultBrush")
        };
    }

    private static Style? TryStyle(string key)
    {
        return Application.Current.Resources.TryGetValue(key, out var value) ? value as Style : null;
    }

    private static Brush? TryBrush(string key)
    {
        return Application.Current.Resources.TryGetValue(key, out var value) ? value as Brush : null;
    }

    private void EnterSelectionMode() => RunCoreAndRefresh(_core.EnterSelectionMode);

    private void EnterWhitelistSelectionMode() => RunCoreAndRefresh(_core.EnterWhitelistSelectionMode);

    private void ToggleAutoMute() => RunCoreAndRefresh(() => _core.ToggleAutoMute(!_snapshot.AutoEnabled));

    private void RunCoreAndRefresh(Action action)
    {
        try
        {
            action();
            RefreshSnapshot();
        }
        catch (Exception error)
        {
            ShowError(error.Message);
            return;
        }
    }

    private void ShowError(string error)
    {
        var message = _loc.CoreMessage(error);
        StatusInfoBar.IsOpen = true;
        StatusInfoBar.Severity = InfoBarSeverity.Error;
        StatusInfoBar.Title = _loc.T("app.title");
        StatusInfoBar.Message = message;
        StatusInfoBar.Background = TryBrush("CardBackgroundFillColorDefaultBrush");
        StatusInfoBar.BorderBrush = TryBrush("CardStrokeColorDefaultBrush");
        StatusInfoBar.BorderThickness = new Thickness(1);
        _lastStatusSignature = $"error|{message}";
    }

    private void HideToTray()
    {
        ShowWindow(_hwnd, 0);
    }

    public void ShowFromExternalActivation() => ShowWindowFromTray();

    private void ShowWindowFromTray()
    {
        ShowWindow(_hwnd, 5);
        SetForegroundWindow(_hwnd);
        Activate();
    }

    private void ExitApplication()
    {
        _allowClose = true;
        DisposeResources();
        Application.Current.Exit();
    }

    private void DisposeResources()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _refreshTimer.Stop();
        _tray.Dispose();
        _core.Dispose();
    }

    private sealed record LanguageOption(string Code, string DisplayName);

    private sealed record ThemeOption(string Code, string DisplayName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hwnd, int commandShow);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKey);

    private static bool IsKeyDown(int virtualKey) => (GetKeyState(virtualKey) & 0x8000) != 0;
}
