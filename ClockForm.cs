using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;

namespace BeijingTimeOverlay;

internal sealed class ClockForm : Form
{
    private const int WindowWidth = 92;
    private const int WindowHeight = 51;
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

    private static readonly CultureInfo DisplayCulture = CultureInfo.InvariantCulture;
    private static readonly Color OverlayColor = Color.FromArgb(40, 38, 29);
    private static readonly IntPtr HwndTopmost = new(-1);

    private readonly OverlaySettings _settings;
    private readonly TimeZoneInfo _beijingTimeZone;
    private readonly System.Windows.Forms.Timer _clockTimer;
    private readonly System.Windows.Forms.Timer _saveTimer;
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _trayIcon;
    private readonly ContextMenuStrip _menu;
    private ToolStripMenuItem _topMostItem = null!;
    private ToolStripMenuItem _clickThroughItem = null!;
    private ToolStripMenuItem _startupItem = null!;
    private readonly Font _clockFont;

    private string _timeText = string.Empty;
    private string _dateText = string.Empty;
    private bool _allowClose;
    private bool _isLoadingPosition;
    private bool _isDisposed;
    private bool _clickThrough;

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
            KeepAboveTaskbar();
        };

        _saveTimer = new System.Windows.Forms.Timer { Interval = 300 };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SaveSettings();
        };

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
        FormClosed += (_, _) => DisposeResources();

        ApplySavedOrDefaultPosition();
        UpdateClock();
        _clockTimer.Start();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        EnsureWindowGeometry();
        KeepAboveTaskbar();
    }

    protected override bool ShowWithoutActivation => true;

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
            Show();
            EnsureWindowGeometry();
            TopMost = _settings.TopMost;
            KeepAboveTaskbar();
            return;
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
            Hide();
        }
        else
        {
            Show();
            TopMost = _settings.TopMost;
            KeepAboveTaskbar();
        }
    }

    private void SetTopMost(bool enabled)
    {
        _settings.TopMost = enabled;
        TopMost = enabled;
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

    private void SetClickThrough(bool enabled)
    {
        _clickThrough = enabled;
        _settings.ClickThrough = enabled;
        SaveSettings();
        RecreateHandle();
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
        _clockTimer.Stop();
        _saveTimer.Stop();
        _clockTimer.Dispose();
        _saveTimer.Dispose();
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
    private static extern bool SetWindowPos(
        IntPtr handle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
