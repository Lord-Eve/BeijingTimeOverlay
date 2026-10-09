using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace BeijingTimeOverlay;

internal sealed class ClockForm : Form
{
    private const int WmDisplayChange = 0x007E;
    private const int WmEnterSizeMove = 0x0231;
    private const int WmExitSizeMove = 0x0232;
    private const int FullscreenDetectionInterval = 250;
    private const int FullscreenBoundsTolerance = 8;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const int WmNcHitTest = 0x0084;
    private const int WmShowClock = Program.ShowClockMessage;
    private const int HtTransparent = -1;
    private const int WmNcLButtonDown = 0x00A1;
    private const int HtCaption = 2;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const int GwlpHwndParent = -8;
    private const uint EventSystemForeground = 0x0003;
    private const uint WinEventOutOfContext = 0x0000;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const int ForegroundReassertDelay = 150;
    private const uint GwOwner = 4;
    private const string CoreWindowClass = "Windows.UI.Core.CoreWindow";

    private static readonly CultureInfo DisplayCulture = CultureInfo.InvariantCulture;
    private static readonly Color OverlayColor = Color.FromArgb(40, 38, 29);
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly int TaskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

    // The desktop and taskbar can cover a whole monitor but are not
    // fullscreen applications.
    private static readonly string[] ShellWindowClasses =
    {
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
    };

    private static readonly string[] TaskbarWindowClasses =
    {
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
    };

    // Shell flyouts (Start, search, notification center) are CoreWindows, but
    // so are some full-screen UWP apps and games; only exempt these hosts.
    private static readonly string[] ShellFlyoutProcesses =
    {
        "StartMenuExperienceHost",
        "SearchHost",
        "SearchApp",
        "SearchUI",
        "ShellExperienceHost",
    };

    private readonly OverlaySettings _settings;
    private readonly TimeZoneInfo _beijingTimeZone;
    private readonly System.Windows.Forms.Timer _clockTimer;
    private readonly System.Windows.Forms.Timer _fullscreenTimer;
    private readonly System.Windows.Forms.Timer _displayTimer;
    private readonly System.Windows.Forms.Timer _reassertTimer;
    private readonly WinEventDelegate _foregroundChangedCallback;
    private IntPtr _foregroundHook;
    private IntPtr _frameworkOwner;
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _trayIcon;
    private readonly ContextMenuStrip _menu;
    private ToolStripMenuItem _topMostItem = null!;
    private ToolStripMenuItem _clickThroughItem = null!;
    private ToolStripMenuItem _hideWhenFullscreenItem = null!;
    private ToolStripMenuItem _startupItem = null!;
    private Font _clockFont;
    private OverlayLayout _layout;
    private readonly Action<OverlaySettings> _persistSettings;
    private bool _isDragging;
    private string _displaySignature = string.Empty;

    private string _timeText = string.Empty;
    private string _dateText = string.Empty;
    private bool _allowClose;
    private bool _isLoadingPosition;
    private bool _isDisposed;
    private bool _clickThrough;
    private bool _hiddenByFullscreen;
    private bool _hiddenByUser;

    public ClockForm() : this(SettingsStore.Load(), SettingsStore.Save)
    {
    }

    internal ClockForm(OverlaySettings settings, Action<OverlaySettings> persistSettings)
    {
        _settings = settings;
        _persistSettings = persistSettings;
        _clickThrough = _settings.ClickThrough;
        _beijingTimeZone = FindBeijingTimeZone();

        Text = "北京时间";
        AccessibleName = "北京时间浮窗";
        AccessibleRole = AccessibleRole.Window;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        // Custom painting and window geometry share one explicit DPI scale.
        // Disable WinForms autoscaling to avoid applying that scale twice.
        AutoScaleMode = AutoScaleMode.None;
        _layout = OverlayGeometry.LayoutForDpi(DeviceDpi);
        ClientSize = _layout.ClientSize;
        BackColor = OverlayColor;
        ForeColor = Color.White;
        TopMost = _settings.TopMost;
        DoubleBuffered = true;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer,
            true);

        _clockFont = new Font("Segoe UI", _layout.FontPixels, FontStyle.Regular, GraphicsUnit.Pixel);

        _menu = BuildMenu();
        ContextMenuStrip = _menu;

        _trayIcon = CreateTrayIcon();
        _notifyIcon = new NotifyIcon
        {
            Icon = _trayIcon,
            Text = "北京时间",
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _notifyIcon.DoubleClick += (_, _) => ToggleVisibility();

        // The display changes once per second; keep the fallback z-order check
        // at the same cadence instead of waking the UI thread four times per second.
        _clockTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _clockTimer.Tick += (_, _) =>
        {
            if (!IsHandleCreated)
            {
                RestoreAfterHandleLoss();
            }
            UpdateClock();
            if (_displaySignature != GetDisplaySignature())
            {
                QueueDisplayLayout();
            }
            AttachToTaskbar();
            KeepAboveTaskbar();
        };

        // This short poll only runs when the user enables fullscreen hiding.
        _fullscreenTimer = new System.Windows.Forms.Timer { Interval = FullscreenDetectionInterval };
        _fullscreenTimer.Tick += (_, _) => UpdateFullscreenVisibility();

        // Display mode changes arrive in bursts. Reflow after Explorer and
        // the monitor topology have had a chance to settle.
        _displayTimer = new System.Windows.Forms.Timer { Interval = 200 };
        _displayTimer.Tick += (_, _) =>
        {
            _displayTimer.Stop();
            if (!_isDragging)
            {
                RestoreAfterHandleLoss();
                EnsureWindowGeometry();
            }
        };

        // Explorer finishes its own z-order changes shortly after the
        // foreground switch, so re-assert once more after it settles.
        _reassertTimer = new System.Windows.Forms.Timer { Interval = ForegroundReassertDelay };
        _reassertTimer.Tick += (_, _) =>
        {
            _reassertTimer.Stop();
            KeepAboveTaskbar();
        };

        // Out-of-context WinEvents are delivered on this UI thread's message loop.
        // Keep the delegate in a field so the GC cannot collect it while hooked.
        _foregroundChangedCallback = HandleForegroundChanged;
        _foregroundHook = SetWinEventHook(
            EventSystemForeground,
            EventSystemForeground,
            IntPtr.Zero,
            _foregroundChangedCallback,
            0,
            0,
            WinEventOutOfContext);

        MouseDown += HandleMouseDown;
        DpiChanged += (_, _) =>
        {
            // This can be raised synchronously while Location is changing.
            // Always perform another deferred pass instead of dropping the
            // target monitor's notification during a display transition.
            QueueDisplayLayout();
        };
        SystemEvents.DisplaySettingsChanged += HandleDisplaySettingsChanged;
        Deactivate += (_, _) => KeepAboveTaskbar();
        FormClosing += HandleFormClosing;
        FormClosed += (_, _) =>
        {
            DisposeResources();
            Application.ExitThread();
        };

        ApplySavedOrDefaultPosition();
        UpdateClock();
        _clockTimer.Start();
        if (_settings.HideWhenFullscreen)
        {
            _fullscreenTimer.Start();
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        EnsureWindowGeometry();
        AttachToTaskbar();
        KeepAboveTaskbar();
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        // Form.CreateHandle() is still running here: with ShowInTaskbar=false
        // WinForms afterwards points the native owner at its hidden taskbar
        // owner window, so attach once handle creation has finished.
        _frameworkOwner = IntPtr.Zero;
        BeginInvoke((Action)(() =>
        {
            if (_isDisposed)
            {
                return;
            }

            QueueDisplayLayout();
            AttachToTaskbar();
            KeepAboveTaskbar();
        }));
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        base.OnHandleDestroyed(e);
        if (_allowClose || _isDisposed || RecreatingHandle)
        {
            return;
        }

        // When Explorer restarts, windows owned by the old taskbar can be
        // destroyed with it. Rebuild the overlay instead of silently vanishing.
        // base.OnHandleDestroyed can uninstall WinForms' synchronization
        // context after the last form disappears. A timer uses the UI thread's
        // independent timer window, so recovery must not depend on that context.
        QueueDisplayLayout();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= WsExToolWindow | WsExNoActivate;
            if (_clickThrough)
            {
                parameters.ExStyle |= WsExTransparent;
            }

            return parameters;
        }
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmEnterSizeMove)
        {
            _isDragging = true;
            _displayTimer.Stop();
        }

        if (message.Msg == WmExitSizeMove)
        {
            base.WndProc(ref message);
            _isDragging = false;
            RememberDraggedPosition();
            return;
        }

        if (message.Msg == WmShowClock)
        {
            _hiddenByUser = false;
            Show();
            EnsureWindowGeometry();
            TopMost = _settings.TopMost;
            KeepAboveTaskbar();
            return;
        }

        if (message.Msg == TaskbarCreatedMessage)
        {
            QueueDisplayLayout();
            AttachToTaskbar();
            KeepAboveTaskbar();
        }

        if (message.Msg == WmNcHitTest && _clickThrough)
        {
            message.Result = (IntPtr)HtTransparent;
            return;
        }

        base.WndProc(ref message);
        if (message.Msg == WmDisplayChange)
        {
            QueueDisplayLayout();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(OverlayColor);
        e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        using var brush = new SolidBrush(Color.White);
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            FormatFlags = StringFormatFlags.NoWrap,
            Alignment = StringAlignment.Center,
        };

        var textWidth = ClientSize.Width - 2 * _layout.PaddingX;
        var lineHeight = _clockFont.GetHeight(e.Graphics);
        e.Graphics.DrawString(_timeText, _clockFont, brush,
            new RectangleF(_layout.PaddingX, _layout.TimeTop, textWidth, lineHeight), format);
        e.Graphics.DrawString(_dateText, _clockFont, brush,
            new RectangleF(_layout.PaddingX, _layout.DateTop, textWidth, lineHeight), format);
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.RenderMode = ToolStripRenderMode.System;
        menu.Opening += (_, _) => RefreshMenuChecks();

        var toggleItem = new ToolStripMenuItem("显示 / 隐藏");
        toggleItem.Click += (_, _) => ToggleVisibility();

        _topMostItem = new ToolStripMenuItem("始终置顶")
        {
            CheckOnClick = true,
        };
        _topMostItem.Click += (_, _) => SetTopMost(!_settings.TopMost);

        _clickThroughItem = new ToolStripMenuItem("鼠标穿透（不拦截任务栏点击）")
        {
            CheckOnClick = true,
        };
        _clickThroughItem.Click += (_, _) => SetClickThrough(!_settings.ClickThrough);

        _hideWhenFullscreenItem = new ToolStripMenuItem("全屏应用时自动隐藏")
        {
            CheckOnClick = true,
        };
        _hideWhenFullscreenItem.Click += (_, _) => SetHideWhenFullscreen(!_settings.HideWhenFullscreen);

        var moveItem = new ToolStripMenuItem("移到当前屏幕右下角");
        moveItem.Click += (_, _) => PlaceAtCurrentScreenBottomRight();

        var resetItem = new ToolStripMenuItem("重置到主屏幕右下角");
        resetItem.Click += (_, _) => PlaceAtScreenBottomRight(Screen.PrimaryScreen ?? Screen.AllScreens.First());

        _startupItem = new ToolStripMenuItem("登录 Windows 时启动")
        {
            CheckOnClick = true,
        };
        _startupItem.Click += (_, _) => ToggleStartup();

        var exitItem = new ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => ExitApplication();

        menu.Items.Add(toggleItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_topMostItem);
        menu.Items.Add(_clickThroughItem);
        menu.Items.Add(_hideWhenFullscreenItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(moveItem);
        menu.Items.Add(resetItem);
        menu.Items.Add(_startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        return menu;
    }

    private void UpdateClock()
    {
        var beijingNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _beijingTimeZone);
        var newTimeText = beijingNow.ToString("HH:mm:ss", DisplayCulture);
        var newDateText = beijingNow.ToString("yyyy/M/d", DisplayCulture);

        if (newTimeText == _timeText && newDateText == _dateText)
        {
            return;
        }

        _timeText = newTimeText;
        _dateText = newDateText;
        Invalidate();
    }

    private void UpdateFullscreenVisibility()
    {
        if (!_settings.HideWhenFullscreen)
        {
            return;
        }

        if (IsFullscreenForegroundWindow())
        {
            if (Visible)
            {
                _hiddenByFullscreen = true;
                Hide();
            }

            return;
        }

        RestoreAfterFullscreen();
    }

    private bool IsFullscreenForegroundWindow()
    {
        var foregroundWindow = GetForegroundWindow();
        if (foregroundWindow == IntPtr.Zero || foregroundWindow == Handle ||
            !IsWindowVisible(foregroundWindow) || IsIconic(foregroundWindow) ||
            IsShellWindow(foregroundWindow) ||
            !GetWindowRect(foregroundWindow, out var windowBounds))
        {
            return false;
        }

        var foregroundScreen = Screen.FromHandle(foregroundWindow);
        var overlayScreen = Screen.FromRectangle(Bounds);
        if (!string.Equals(foregroundScreen.DeviceName, overlayScreen.DeviceName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var monitorBounds = foregroundScreen.Bounds;
        return windowBounds.Left <= monitorBounds.Left + FullscreenBoundsTolerance &&
               windowBounds.Top <= monitorBounds.Top + FullscreenBoundsTolerance &&
               windowBounds.Right >= monitorBounds.Right - FullscreenBoundsTolerance &&
               windowBounds.Bottom >= monitorBounds.Bottom - FullscreenBoundsTolerance;
    }

    private static bool IsShellWindow(IntPtr window)
    {
        var className = GetWindowClassName(window);
        if (ShellWindowClasses.Contains(className, StringComparer.Ordinal))
        {
            return true;
        }

        return className == CoreWindowClass && IsShellFlyoutProcess(window);
    }

    private static bool IsShellFlyoutProcess(IntPtr window)
    {
        GetWindowThreadProcessId(window, out var processId);
        if (processId == 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return ShellFlyoutProcesses.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            // The process exited between the foreground check and this lookup.
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static string GetWindowClassName(IntPtr window)
    {
        var className = new StringBuilder(256);
        return GetClassName(window, className, className.Capacity) == 0
            ? string.Empty
            : className.ToString();
    }

    private static bool IsTaskbarWindow(IntPtr window)
    {
        return window != IntPtr.Zero &&
               TaskbarWindowClasses.Contains(GetWindowClassName(window), StringComparer.Ordinal);
    }

    private void HandleForegroundChanged(
        IntPtr hook,
        uint eventType,
        IntPtr window,
        int objectId,
        int childId,
        uint threadId,
        uint eventTime)
    {
        if (_isDisposed)
        {
            return;
        }

        if (_settings.HideWhenFullscreen)
        {
            UpdateFullscreenVisibility();
        }

        AttachToTaskbar();
        KeepAboveTaskbar();
        _reassertTimer.Stop();
        _reassertTimer.Start();
    }

    private void RestoreAfterHandleLoss()
    {
        if (_allowClose || _isDisposed || IsDisposed || IsHandleCreated)
        {
            return;
        }

        // Reset WinForms' visible state first so Show() really creates a new window.
        Hide();
        if (_hiddenByUser || _hiddenByFullscreen)
        {
            // Keep a hidden window so a second launch can still find and wake it.
            CreateHandle();
            return;
        }

        Show();
        EnsureWindowGeometry();
        TopMost = _settings.TopMost;
        KeepAboveTaskbar();
    }

    private void RestoreAfterFullscreen()
    {
        if (!_hiddenByFullscreen)
        {
            return;
        }

        _hiddenByFullscreen = false;
        Show();
        EnsureWindowGeometry();
        TopMost = _settings.TopMost;
        KeepAboveTaskbar();
    }

    private void ApplySavedOrDefaultPosition()
    {
        if (_settings.RightOffsetDip is null || _settings.BottomOffsetDip is null)
        {
            if (_settings.Left is int left && _settings.Top is int top)
            {
                var savedBounds = new Rectangle(left, top, 92, 51);
                var screen = Screen.AllScreens.FirstOrDefault(item => item.Bounds.Contains(savedBounds));
                if (screen is not null &&
                    !OverlayGeometry.IsLegacyClockCorner(savedBounds, screen.Bounds, GetScreenDpi(screen)))
                {
                    var dpi = GetScreenDpi(screen);
                    var size = OverlayGeometry.LayoutForDpi(dpi).ClientSize;
                    var offsets = OverlayGeometry.OffsetsForPosition(new Point(left, top), size, screen.Bounds, dpi);
                    _settings.MonitorId = GetMonitorId(screen);
                    _settings.FollowPrimaryScreen = screen.Primary;
                    _settings.RightOffsetDip = offsets.Right;
                    _settings.BottomOffsetDip = offsets.Bottom;
                }
            }

            _settings.RightOffsetDip ??= 0;
            _settings.BottomOffsetDip ??= 0;
        }

        EnsureWindowGeometry();
    }

    private void EnsureWindowGeometry()
    {
        if (_isDisposed || _isDragging || _isLoadingPosition)
        {
            return;
        }

        var displays = GetCurrentDisplays();
        if (displays.Length == 0)
        {
            // Win+P can briefly have no active monitor. Do not persist a
            // guessed DPI or location; the normal clock tick retries later.
            return;
        }
        var target = OverlayGeometry.SelectDisplay(displays, _settings.MonitorId, _settings.FollowPrimaryScreen);
        var missingSavedMonitor = !_settings.FollowPrimaryScreen &&
            !string.Equals(target.Id, _settings.MonitorId, StringComparison.OrdinalIgnoreCase);

        _isLoadingPosition = true;
        try
        {
            UpdateLayout(target.Dpi);
            Location = OverlayGeometry.PositionFromOffsets(target.Bounds, _layout.ClientSize, target.Dpi,
                missingSavedMonitor ? 0 : _settings.RightOffsetDip ?? 0,
                missingSavedMonitor ? 0 : _settings.BottomOffsetDip ?? 0);
            // Crossing a DPI boundary can synchronously apply Windows' suggested
            // rectangle. Reapply our freshly computed size, not the old size.
            ClientSize = _layout.ClientSize;
            _displaySignature = GetDisplaySignature(displays);
        }
        finally
        {
            _isLoadingPosition = false;
        }

        AttachToTaskbar();
        KeepAboveTaskbar();
        Invalidate();
        SaveSettings();
        UpdateFullscreenVisibility();
    }

    private void UpdateLayout(int dpi)
    {
        var layout = OverlayGeometry.LayoutForDpi(dpi);
        if (_layout.Dpi != layout.Dpi)
        {
            var previousFont = _clockFont;
            _clockFont = new Font("Segoe UI", layout.FontPixels, FontStyle.Regular, GraphicsUnit.Pixel);
            previousFont.Dispose();
        }

        _layout = layout;
        ClientSize = layout.ClientSize;
        Invalidate();
    }

    private void RememberDraggedPosition()
    {
        var screen = Screen.FromRectangle(Bounds);
        var dpi = GetScreenDpi(screen);
        _isLoadingPosition = true;
        try
        {
            UpdateLayout(dpi);
            Location = OverlayGeometry.ClampLocation(Location, ClientSize, screen.Bounds);
            var offsets = OverlayGeometry.OffsetsForPosition(Location, ClientSize, screen.Bounds, dpi);
            _settings.MonitorId = GetMonitorId(screen);
            _settings.FollowPrimaryScreen = screen.Primary;
            _settings.RightOffsetDip = offsets.Right;
            _settings.BottomOffsetDip = offsets.Bottom;
        }
        finally
        {
            _isLoadingPosition = false;
        }

        AttachToTaskbar();
        KeepAboveTaskbar();
        SaveSettings();
    }

    private void QueueDisplayLayout()
    {
        if (_isDisposed || _isDragging)
        {
            return;
        }

        _displayTimer.Stop();
        _displayTimer.Start();
    }

    private void HandleDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (_isDisposed || !IsHandleCreated)
        {
            return;
        }

        try
        {
            BeginInvoke((Action)QueueDisplayLayout);
        }
        catch (InvalidOperationException)
        {
            // The window can be disposed or recreated while the event is queued.
        }
    }

    private static string GetDisplaySignature()
    {
        return GetDisplaySignature(GetCurrentDisplays());
    }

    private static string GetDisplaySignature(IEnumerable<OverlayDisplay> displays)
    {
        return string.Join(";", displays.Select(display =>
            $"{display.Id}:{display.IsPrimary}:{display.Bounds}:{display.Dpi}"));
    }

    private static OverlayDisplay[] GetCurrentDisplays()
    {
        // Screen.AllScreens can still describe the previous Win+P topology
        // while display events are being dispatched. Query current native
        // monitors, including DPI, rather than caching GDI display numbers.
        var previousContext = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try
        {
            var displays = new List<OverlayDisplay>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
                (IntPtr monitor, IntPtr dc, ref NativeRect bounds, IntPtr data) =>
                {
                    var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                    if (GetMonitorInfo(monitor, ref info))
                    {
                        var rectangle = Rectangle.FromLTRB(info.Bounds.Left, info.Bounds.Top,
                            info.Bounds.Right, info.Bounds.Bottom);
                        displays.Add(new OverlayDisplay(GetMonitorId(info.DeviceName), rectangle,
                            GetMonitorDpi(monitor, rectangle), (info.Flags & 1) != 0));
                    }
                    return true;
                }, IntPtr.Zero);
            return displays.ToArray();
        }
        finally
        {
            if (previousContext != IntPtr.Zero)
            {
                SetThreadDpiAwarenessContext(previousContext);
            }
        }
    }

    private static int GetScreenDpi(Screen screen)
    {
        var center = new NativePoint
        {
            X = screen.Bounds.Left + screen.Bounds.Width / 2,
            Y = screen.Bounds.Top + screen.Bounds.Height / 2,
        };
        var monitor = MonitorFromPoint(center, MonitorDefaultToNearest);
        return GetMonitorDpi(monitor, screen.Bounds);
    }

    private static int GetMonitorDpi(IntPtr monitor, Rectangle bounds)
    {
        // GetDpiForMonitor is process-awareness-dependent. Also, an overlay
        // whose framework owner was recreated on another monitor can report
        // the owner's startup DPI. A tiny hidden, unowned native window on
        // the target monitor gives GetDpiForWindow an unambiguous target.
        var previousContext = SetThreadDpiAwarenessContext(new IntPtr(-4));
        var probe = IntPtr.Zero;
        try
        {
            probe = CreateWindowEx(WsExToolWindow | WsExNoActivate, "STATIC", null,
                0x80000000, bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2,
                1, 1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            var dpi = probe == IntPtr.Zero ? 0 : GetDpiForWindow(probe);
            if (dpi > 0)
            {
                return (int)dpi;
            }
        }
        finally
        {
            if (probe != IntPtr.Zero)
            {
                DestroyWindow(probe);
            }
            if (previousContext != IntPtr.Zero)
            {
                SetThreadDpiAwarenessContext(previousContext);
            }
        }

        if (GetDpiForMonitor(monitor, 0, out var dpiX, out _) >= 0 && dpiX > 0)
        {
            return (int)dpiX;
        }

        GetScaleFactorForMonitor(monitor, out var scale);
        return scale > 0 ? (int)Math.Round(96 * scale / 100d) : 96;
    }

    private static string GetMonitorId(Screen screen)
    {
        return GetMonitorId(screen.DeviceName);
    }

    private static string GetMonitorId(string deviceName)
    {
        var device = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
        return EnumDisplayDevices(deviceName, 0, ref device, 1) &&
               !string.IsNullOrEmpty(device.DeviceId)
            ? device.DeviceId
            : deviceName;
    }

    private void PlaceAtCurrentScreenBottomRight()
    {
        var screen = Screen.FromPoint(Cursor.Position);
        PlaceAtScreenBottomRight(screen);
    }

    private void PlaceAtScreenBottomRight(Screen screen)
    {
        _settings.MonitorId = GetMonitorId(screen);
        _settings.FollowPrimaryScreen = screen.Primary;
        _settings.RightOffsetDip = 0;
        _settings.BottomOffsetDip = 0;
        EnsureWindowGeometry();
    }

    private void HandleMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || _clickThrough)
        {
            return;
        }

        ReleaseCapture();
        SendMessage(Handle, WmNcLButtonDown, (IntPtr)HtCaption, IntPtr.Zero);
    }

    private void ToggleVisibility()
    {
        if (Visible)
        {
            _hiddenByUser = true;
            Hide();
        }
        else
        {
            _hiddenByUser = false;
            Show();
            EnsureWindowGeometry();
            TopMost = _settings.TopMost;
            KeepAboveTaskbar();
        }
    }

    private void SetTopMost(bool enabled)
    {
        _settings.TopMost = enabled;
        TopMost = enabled;
        AttachToTaskbar();
        KeepAboveTaskbar();
        SaveSettings();
        RefreshMenuChecks();
    }

    private void KeepAboveTaskbar()
    {
        if (!_settings.TopMost || !Visible || !IsHandleCreated)
        {
            return;
        }

        SetWindowPos(
            Handle,
            HwndTopmost,
            0,
            0,
            0,
            0,
            SwpNoActivate | SwpNoMove | SwpNoSize | SwpShowWindow);
    }

    private void AttachToTaskbar()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        // The taskbar is itself a topmost window and Windows keeps it above
        // other topmost windows while it is the foreground window, so a plain
        // HWND_TOPMOST re-assert loses after a taskbar click. An owned window
        // always stays above its owner, so let the taskbar under the overlay
        // own it. Always compare with the real native owner: WinForms can
        // reset it (for example while creating or recreating the handle).
        var currentOwner = GetWindow(Handle, GwOwner);
        var currentIsTaskbar = IsTaskbarWindow(currentOwner);
        if (currentOwner != IntPtr.Zero && !currentIsTaskbar)
        {
            // Remember WinForms' own owner so turning TopMost off restores it.
            _frameworkOwner = currentOwner;
        }

        if (!_settings.TopMost)
        {
            if (currentIsTaskbar)
            {
                SetWindowOwner(Handle, _frameworkOwner);
            }

            return;
        }

        var taskbar = FindTaskbarForWindow(Handle);
        if (taskbar != IntPtr.Zero && taskbar != currentOwner)
        {
            SetWindowOwner(Handle, taskbar);
        }
    }

    private static IntPtr FindTaskbarForWindow(IntPtr window)
    {
        var monitor = MonitorFromWindow(window, MonitorDefaultToNearest);
        var primaryTaskbar = FindWindow("Shell_TrayWnd", null);
        if (primaryTaskbar != IntPtr.Zero &&
            MonitorFromWindow(primaryTaskbar, MonitorDefaultToNearest) == monitor)
        {
            return primaryTaskbar;
        }

        var secondaryTaskbar = IntPtr.Zero;
        while ((secondaryTaskbar = FindWindowEx(IntPtr.Zero, secondaryTaskbar, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
        {
            if (MonitorFromWindow(secondaryTaskbar, MonitorDefaultToNearest) == monitor)
            {
                return secondaryTaskbar;
            }
        }

        return primaryTaskbar;
    }

    private static void SetWindowOwner(IntPtr window, IntPtr owner)
    {
        if (IntPtr.Size == 8)
        {
            SetWindowLongPtr64(window, GwlpHwndParent, owner);
        }
        else
        {
            SetWindowLong32(window, GwlpHwndParent, owner.ToInt32());
        }
    }

    private void SetClickThrough(bool enabled)
    {
        _clickThrough = enabled;
        _settings.ClickThrough = enabled;
        SaveSettings();
        RecreateHandle();
        RefreshMenuChecks();
    }

    private void SetHideWhenFullscreen(bool enabled)
    {
        _settings.HideWhenFullscreen = enabled;
        if (enabled)
        {
            _fullscreenTimer.Start();
            UpdateFullscreenVisibility();
        }
        else
        {
            _fullscreenTimer.Stop();
            RestoreAfterFullscreen();
        }

        SaveSettings();
        RefreshMenuChecks();
    }

    private void ToggleStartup()
    {
        var enabled = !StartupManager.IsEnabled();
        if (!StartupManager.SetEnabled(enabled))
        {
            MessageBox.Show(
                this,
                "无法更新开机启动设置。你仍然可以手动运行程序。",
                "北京时间浮窗",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        _settings.StartWithWindows = enabled;
        SaveSettings();
        RefreshMenuChecks();
    }

    private void RefreshMenuChecks()
    {
        _topMostItem.Checked = _settings.TopMost;
        _clickThroughItem.Checked = _settings.ClickThrough;
        _hideWhenFullscreenItem.Checked = _settings.HideWhenFullscreen;
        _startupItem.Checked = _settings.StartWithWindows || StartupManager.IsEnabled();
    }

    private void SaveSettings()
    {
        if (_isDisposed)
        {
            return;
        }

        _settings.Left = Left;
        _settings.Top = Top;
        _persistSettings(_settings);
    }

    private void ExitApplication()
    {
        _allowClose = true;
        Close();
    }

    private void HandleFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        _hiddenByUser = true;
        Hide();
        SaveSettings();
    }

    private void DisposeResources()
    {
        if (_isDisposed)
        {
            return;
        }

        SaveSettings();
        _isDisposed = true;
        SystemEvents.DisplaySettingsChanged -= HandleDisplaySettingsChanged;
        if (_foregroundHook != IntPtr.Zero)
        {
            UnhookWinEvent(_foregroundHook);
            _foregroundHook = IntPtr.Zero;
        }

        _clockTimer.Stop();
        _fullscreenTimer.Stop();
        _displayTimer.Stop();
        _reassertTimer.Stop();
        _clockTimer.Dispose();
        _fullscreenTimer.Dispose();
        _displayTimer.Dispose();
        _reassertTimer.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _trayIcon.Dispose();
        _menu.Dispose();
        _clockFont.Dispose();
    }

    private static Icon CreateTrayIcon()
    {
        using var bitmap = new Bitmap(16, 16, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);

        using var faceBrush = new SolidBrush(Color.FromArgb(28, 76, 108));
        using var facePen = new Pen(Color.FromArgb(123, 214, 255), 1.2f);
        using var handPen = new Pen(Color.White, 1.25f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };

        graphics.FillEllipse(faceBrush, 2f, 2f, 12f, 12f);
        graphics.DrawEllipse(facePen, 2f, 2f, 12f, 12f);
        graphics.DrawLine(handPen, 8f, 8f, 8f, 4.8f);
        graphics.DrawLine(handPen, 8f, 8f, 10.8f, 9.5f);

        var iconHandle = bitmap.GetHicon();
        try
        {
            using var sourceIcon = Icon.FromHandle(iconHandle);
            return (Icon)sourceIcon.Clone();
        }
        finally
        {
            DestroyIcon(iconHandle);
        }
    }

    private static TimeZoneInfo FindBeijingTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.CreateCustomTimeZone(
                "Beijing Standard Time",
                TimeSpan.FromHours(8),
                "Beijing Standard Time",
                "Beijing Standard Time");
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.CreateCustomTimeZone(
                "Beijing Standard Time",
                TimeSpan.FromHours(8),
                "Beijing Standard Time",
                "Beijing Standard Time");
        }
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr handle, out NativeRect windowBounds);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr handle, StringBuilder className, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? className, string? windowName);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr handle, uint command);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr handle, uint flags);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr handle, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(IntPtr handle, int index, int value);

    private delegate void WinEventDelegate(
        IntPtr hook,
        uint eventType,
        IntPtr window,
        int objectId,
        int childId,
        uint threadId,
        uint eventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr module,
        WinEventDelegate callback,
        uint processId,
        uint threadId,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Bounds;
        public NativeRect WorkingArea;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
    }

    private delegate bool MonitorEnumDelegate(IntPtr monitor, IntPtr dc, ref NativeRect bounds, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumDelegate callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int extendedStyle, string className, string? name,
        uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetScaleFactorForMonitor(IntPtr monitor, out int scale);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string deviceName, uint index, ref DisplayDevice device, uint flags);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr handle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
