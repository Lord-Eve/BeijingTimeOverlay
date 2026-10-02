using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace BeijingTimeOverlay;

internal sealed class ClockForm : Form
{
    private const int WindowWidth = 92;
    private const int WindowHeight = 51;
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
    private readonly System.Windows.Forms.Timer _saveTimer;
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
    private readonly Font _clockFont;

    private string _timeText = string.Empty;
    private string _dateText = string.Empty;
    private bool _allowClose;
    private bool _isLoadingPosition;
    private bool _isDisposed;
    private bool _clickThrough;
    private bool _hiddenByFullscreen;
    private bool _hiddenByUser;

    public ClockForm()
    {
        _settings = SettingsStore.Load();
        _clickThrough = _settings.ClickThrough;
        _beijingTimeZone = FindBeijingTimeZone();

        Text = "北京时间";
        AccessibleName = "北京时间浮窗";
        AccessibleRole = AccessibleRole.Window;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        // This is a pixel-aligned taskbar overlay. Let the OS handle the
        // process DPI context, but do not let WinForms rescale the fixed
        // screenshot-matching client rectangle a second time.
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = new Size(WindowWidth, WindowHeight);
        BackColor = OverlayColor;
        ForeColor = Color.White;
        TopMost = _settings.TopMost;
        DoubleBuffered = true;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer,
            true);

        _clockFont = new Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point);

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
            UpdateClock();
            AttachToTaskbar();
            KeepAboveTaskbar();
        };

        // This short poll only runs when the user enables fullscreen hiding.
        _fullscreenTimer = new System.Windows.Forms.Timer { Interval = FullscreenDetectionInterval };
        _fullscreenTimer.Tick += (_, _) => UpdateFullscreenVisibility();

        _saveTimer = new System.Windows.Forms.Timer { Interval = 300 };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SaveSettings();
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
        LocationChanged += (_, _) =>
        {
            if (!_isLoadingPosition && IsHandleCreated)
            {
                _saveTimer.Stop();
                _saveTimer.Start();
            }
        };
        DpiChanged += (_, _) => BeginInvoke((Action)(EnsureWindowGeometry));
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
        SynchronizationContext.Current?.Post(_ => RestoreAfterHandleLoss(), null);
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
            AttachToTaskbar();
            KeepAboveTaskbar();
        }

        if (message.Msg == WmNcHitTest && _clickThrough)
        {
            message.Result = (IntPtr)HtTransparent;
            return;
        }

        base.WndProc(ref message);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(OverlayColor);
        e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        using var brush = new SolidBrush(Color.White);
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            FormatFlags = StringFormatFlags.NoWrap,
        };

        e.Graphics.DrawString(_timeText, _clockFont, brush, new PointF(8f, 4f), format);
        e.Graphics.DrawString(_dateText, _clockFont, brush, new PointF(8f, 25f), format);
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
        TopMost = _settings.TopMost;
        KeepAboveTaskbar();
    }

    private void ApplySavedOrDefaultPosition()
    {
        _isLoadingPosition = true;
        try
        {
            if (_settings.Left is int left && _settings.Top is int top)
            {
                var savedBounds = new Rectangle(left, top, Width, Height);
                if (Screen.AllScreens.Any(screen => screen.Bounds.IntersectsWith(savedBounds)))
                {
                    Location = new Point(left, top);
                    return;
                }
            }

            PlaceAtScreenBottomRight(Screen.PrimaryScreen ?? Screen.AllScreens.First());
        }
        finally
        {
            _isLoadingPosition = false;
        }
    }

    private void EnsureWindowGeometry()
    {
        if (ClientSize != new Size(WindowWidth, WindowHeight))
        {
            ClientSize = new Size(WindowWidth, WindowHeight);
        }

        if (_settings.Left is int left && _settings.Top is int top)
        {
            var savedBounds = new Rectangle(left, top, WindowWidth, WindowHeight);
            if (Screen.AllScreens.Any(screen => screen.Bounds.IntersectsWith(savedBounds)))
            {
                Location = new Point(left, top);
                return;
            }
        }

        PlaceAtScreenBottomRight(Screen.FromPoint(Cursor.Position));
    }

    private void PlaceAtCurrentScreenBottomRight()
    {
        var screen = Screen.FromPoint(Cursor.Position);
        PlaceAtScreenBottomRight(screen);
    }

    private void PlaceAtScreenBottomRight(Screen screen)
    {
        _isLoadingPosition = true;
        try
        {
            var bounds = screen.Bounds;
            Location = new Point(bounds.Right - Width, bounds.Bottom - Height);
            SaveSettings();
        }
        finally
        {
            _isLoadingPosition = false;
        }
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
        SettingsStore.Save(_settings);
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
        if (_foregroundHook != IntPtr.Zero)
        {
            UnhookWinEvent(_foregroundHook);
            _foregroundHook = IntPtr.Zero;
        }

        _clockTimer.Stop();
        _fullscreenTimer.Stop();
        _saveTimer.Stop();
        _reassertTimer.Stop();
        _clockTimer.Dispose();
        _fullscreenTimer.Dispose();
        _saveTimer.Dispose();
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
