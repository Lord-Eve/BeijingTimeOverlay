using System.Reflection;
using System.Runtime.InteropServices;
using System.Drawing.Text;
using BeijingTimeOverlay;

internal static class Program
{
    private static int _assertions;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Contains("--live-ui"))
            {
                return RunOnPrivateDesktop();
            }
            var liveUi = args.Contains("--desktop-child");
            if (liveUi)
            {
                TestLiveWindows();
            }

            TestLayouts();
            TestPositions();

            Console.WriteLine($"PASS: {_assertions} assertions; live-ui={liveUi}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void Check(bool condition, string description)
    {
        if (!condition)
        {
            throw new InvalidOperationException(description);
        }

        _assertions++;
    }

    private static void TestLayouts()
    {
        foreach (var dpi in new[] { 96, 120, 144, 168, 192, 240, 288 })
        {
            var layout = OverlayGeometry.LayoutForDpi(dpi);
            using var image = new Bitmap(layout.ClientSize.Width, layout.ClientSize.Height);
            image.SetResolution(dpi, dpi);
            using var graphics = Graphics.FromImage(image);
            graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            using var font = new Font("Segoe UI", layout.FontPixels, FontStyle.Regular, GraphicsUnit.Pixel);
            using var format = new StringFormat(StringFormat.GenericTypographic)
            {
                FormatFlags = StringFormatFlags.NoWrap,
            };

            var height = font.GetHeight(graphics);
            Check(layout.TimeTop + height < layout.DateTop, $"Line overlap at {dpi} DPI");
            Check(layout.DateTop + height <= layout.ClientSize.Height, $"Vertical clipping at {dpi} DPI");
            for (var month = 1; month <= 12; month++)
            {
                for (var day = 1; day <= DateTime.DaysInMonth(2026, month); day++)
                {
                    var text = $"2026/{month}/{day}";
                    var measured = graphics.MeasureString(text, font, PointF.Empty, format);
                    Check(measured.Width <= layout.ClientSize.Width - 2 * layout.PaddingX,
                        $"Date clipping: {text}, {dpi} DPI, width={measured.Width}");
                }
            }

            foreach (var text in new[] { "00:00:00", "11:22:24", "23:59:59" })
            {
                Check(graphics.MeasureString(text, font, PointF.Empty, format).Width <=
                      layout.ClientSize.Width - 2 * layout.PaddingX, $"Time clipping at {dpi} DPI");
            }
        }

        Check(OverlayGeometry.LayoutForDpi(120).ClientSize == new Size(105, 60), "125% geometry");
        Check(OverlayGeometry.LayoutForDpi(144).ClientSize == new Size(126, 72), "150% geometry");
        for (var iteration = 0; iteration < 100; iteration++)
        {
            Check(OverlayGeometry.LayoutForDpi(120).ClientSize == new Size(105, 60), "Repeated scale drift");
            Check(OverlayGeometry.LayoutForDpi(144).FontPixels == 20, "Repeated font drift");
        }
        Check(OverlayGeometry.LayoutForDpi(0).Dpi == 96, "Invalid DPI fallback");
    }

    private static void TestPositions()
    {
        var external = new OverlayDisplay("HKC2533", new Rectangle(0, 0, 2560, 1440), 120, true);
        var internalScreen = new OverlayDisplay("BOE0B11", new Rectangle(-2560, 0, 2560, 1600), 144, false);
        var displays = new[] { external, internalScreen };
        Check(OverlayGeometry.SelectDisplay(displays, internalScreen.Id, true) == external, "Default follows primary");
        Check(OverlayGeometry.SelectDisplay(displays, "boe0b11", false) == internalScreen, "Stable monitor identity");

        var internalOnly = internalScreen with { Bounds = new Rectangle(0, 0, 2560, 1600), IsPrimary = true };
        Check(OverlayGeometry.SelectDisplay(new[] { internalOnly }, external.Id, true) == internalOnly, "Win+P internal only");
        Check(OverlayGeometry.SelectDisplay(new[] { external }, internalScreen.Id, false) == external, "Disconnected monitor fallback");
        Check(OverlayGeometry.SelectDisplay(displays, internalScreen.Id, false) == internalScreen, "Reconnected monitor restoration");

        var reversed = new[] { external with { IsPrimary = false }, internalScreen with { IsPrimary = true } };
        Check(OverlayGeometry.SelectDisplay(reversed, external.Id, true).Id == internalScreen.Id, "Primary screen changed");
        foreach (var display in new[] { external, internalScreen, internalOnly,
                     new OverlayDisplay("above", new Rectangle(0, -1600, 2560, 1600), 144, false) })
        {
            var size = OverlayGeometry.LayoutForDpi(display.Dpi).ClientSize;
            var corner = OverlayGeometry.PositionFromOffsets(display.Bounds, size, display.Dpi, 0, 0);
            Check(new Rectangle(corner, size).Right == display.Bounds.Right, "Right anchoring");
            Check(new Rectangle(corner, size).Bottom == display.Bounds.Bottom, "Bottom anchoring");
            foreach (var point in new[] { new Point(int.MinValue / 2, int.MinValue / 2),
                         new Point(display.Bounds.Right - 3, display.Bounds.Bottom - 2),
                         new Point(int.MaxValue / 2, int.MaxValue / 2), new Point(100, 100) })
            {
                var clamped = OverlayGeometry.ClampLocation(point, size, display.Bounds);
                Check(display.Bounds.Contains(new Rectangle(clamped, size)), "Whole window must be visible");
                var offsets = OverlayGeometry.OffsetsForPosition(clamped, size, display.Bounds, display.Dpi);
                Check(OverlayGeometry.PositionFromOffsets(display.Bounds, size, display.Dpi, offsets.Right, offsets.Bottom) == clamped,
                    "Relative position round trip");
            }
            foreach (var offset in new[] { -1d, 0d, 500d, double.MaxValue, double.NaN, double.PositiveInfinity })
            {
                var location = OverlayGeometry.PositionFromOffsets(display.Bounds, size, display.Dpi, offset, offset);
                Check(display.Bounds.Contains(new Rectangle(location, size)), "Malformed offsets cannot put window off-screen");
            }
        }
        Check(OverlayGeometry.IsLegacyClockCorner(new Rectangle(2457, 1385, 92, 51), external.Bounds, 120), "Legacy position migration");
        Check(!OverlayGeometry.IsLegacyClockCorner(new Rectangle(2457, 1385, 92, 51), internalOnly.Bounds, 144), "Do not treat old Y as new bottom");
        Check(!OverlayGeometry.IsLegacyClockCorner(new Rectangle(2550, 1430, 92, 51), external.Bounds, 120), "Partial intersection rejected");
    }

    private static void TestLiveWindows()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        using var context = new ApplicationContext();
        using var kickoff = new System.Windows.Forms.Timer { Interval = 1 };
        Exception? failure = null;
        kickoff.Tick += (_, _) =>
        {
            kickoff.Stop();
            try { TestLiveClock(); }
            catch (Exception exception) { failure = exception; }
            finally { context.ExitThread(); }
        };
        kickoff.Start();
        Application.Run(context);
        if (failure is not null) throw new InvalidOperationException("Live UI regression failed.", failure);
    }

    private static void TestLiveClock()
    {
        {
            var settings = new OverlaySettings { TopMost = false, FollowPrimaryScreen = true };
            var writes = 0;
            using var clock = new ClockForm(settings, _ => writes++);
            clock.Show();
            Pump(400);
            foreach (var screen in Screen.AllScreens)
            {
                Call(clock, "PlaceAtScreenBottomRight", screen);
                Pump(400);
                var dpi = MonitorDpi(clock.Handle);
                Console.WriteLine($"Before size check: {screen.DeviceName}, monitor DPI={dpi}, native DPI={GetDpiForWindow(clock.Handle)}, framework DPI={clock.DeviceDpi}, client={clock.ClientSize}, bounds={clock.Bounds}");
                Check(clock.ClientSize == OverlayGeometry.LayoutForDpi(dpi).ClientSize, $"Live size at {dpi} DPI");
                Console.WriteLine($"Display {screen.DeviceName}: {screen.Bounds.Width}x{screen.Bounds.Height}, DPI={dpi}, clock={clock.ClientSize}");
                Check(screen.Bounds.Contains(clock.Bounds), "Live full visibility");
                Check(clock.Right == screen.Bounds.Right && clock.Bottom == screen.Bounds.Bottom, "Live bottom-right placement");
                using var image = new Bitmap(clock.Width, clock.Height);
                typeof(ClockForm).GetField("_dateText", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(clock, "2026/12/31");
                clock.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
                Check(image.Size == OverlayGeometry.LayoutForDpi(dpi).ClientSize, "Actual OnPaint bitmap geometry");
                var datePixels = 0;
                var dateTop = (int)OverlayGeometry.LayoutForDpi(dpi).DateTop;
                for (var y = dateTop; y < image.Height; y++)
                {
                    for (var x = 0; x < image.Width; x++)
                    {
                        var pixel = image.GetPixel(x, y);
                        if (pixel.R > 180 && pixel.G > 180 && pixel.B > 180)
                        {
                            datePixels++;
                            Check(x > 0 && x < image.Width - 1 && y < image.Height - 1, "Actual date ink stays away from clipping edges");
                        }
                    }
                }
                Check(datePixels > 30, "Actual OnPaint renders the date row");

                // Exercise a real native drag-completion handler without mouse input.
                clock.Location = new Point(screen.Bounds.Right - clock.Width - 30, screen.Bounds.Bottom - clock.Height - 40);
                Call(clock, "RememberDraggedPosition");
                var custom = clock.Location;
                Call(clock, "EnsureWindowGeometry");
                Pump(300);
                Check(clock.Location == custom, "Manual relative position survives reflow");
                Call(clock, "SetClickThrough", true);
                Pump(500);
                Console.WriteLine($"Recreate {screen.DeviceName}: native DPI={GetDpiForWindow(clock.Handle)}, WinForms DPI={clock.DeviceDpi}, client={clock.ClientSize}, bounds={clock.Bounds}");
                // A recreated WinForms window may retain its framework owner's
                // startup DPI. Layout is intentionally based on the monitor's
                // scale, and pixel-unit fonts do not depend on that cached DPI.
                Check(clock.IsHandleCreated && clock.ClientSize == OverlayGeometry.LayoutForDpi(MonitorDpi(clock.Handle)).ClientSize,
                    "Handle recreation preserves geometry");
                Check(AreDpiAwarenessContextsEqual(GetWindowDpiAwarenessContext(clock.Handle), new IntPtr(-4)),
                    "Recreated window remains PerMonitorV2, not bitmap-scaled");
                GetWindowRect(clock.Handle, out var nativeBounds);
                Check(new Rectangle(nativeBounds.Left, nativeBounds.Top,
                    nativeBounds.Right - nativeBounds.Left, nativeBounds.Bottom - nativeBounds.Top) == clock.Bounds,
                    "Managed geometry matches physical native pixels after recreation");
                Check(screen.Bounds.Contains(clock.Bounds), "Recreated window stays on its selected monitor");
                Call(clock, "SetClickThrough", false);
                Pump(300);

                // A WM_DISPLAYCHANGE must queue a reflow, not blindly accept the
                // stale location after a primary/display-mode change.
                settings.FollowPrimaryScreen = true;
                settings.RightOffsetDip = 0;
                settings.BottomOffsetDip = 0;
                SendMessage(clock.Handle, 0x007E, IntPtr.Zero, IntPtr.Zero);
                Pump(400);
                var primary = Screen.PrimaryScreen!;
                Check(clock.Right == primary.Bounds.Right && clock.Bottom == primary.Bounds.Bottom, "WM_DISPLAYCHANGE reanchors primary");
            }
            var taskbar = CreateTestTaskbar();
            try
            {
                Call(clock, "SetTopMost", true);
                Pump(300);
                var caption = new System.Text.StringBuilder(256);
                GetWindowText(clock.Handle, caption, caption.Capacity);
                Console.WriteLine($"Native clock caption: '{caption}', FindWindow matches={FindWindow(null, "北京时间") == clock.Handle}");
                Check(GetWindow(clock.Handle, 4) == taskbar, "Taskbar owner after placement");
                Call(clock, "SetClickThrough", true);
                Pump(500);
                Check(GetWindow(clock.Handle, 4) == taskbar, "Taskbar owner after handle recreation");
                Call(clock, "SetTopMost", false);
                Check(GetWindow(clock.Handle, 4) != taskbar, "Framework owner restored when topmost disabled");
                Call(clock, "SetTopMost", true);
                var previousHandle = clock.Handle;
                // Match production's last-window destruction: base WinForms
                // cleanup may remove SynchronizationContext.Current entirely.
                var previousContext = SynchronizationContext.Current;
                SynchronizationContext.SetSynchronizationContext(null);
                DestroyWindow(taskbar);
                SynchronizationContext.SetSynchronizationContext(previousContext);
                taskbar = CreateTestTaskbar();
                Pump(700);
                Check(clock.IsHandleCreated && clock.Handle != previousHandle, "Owned window restored after taskbar handle loss");
                Check(GetWindow(clock.Handle, 4) == taskbar, "Restored window attaches to replacement taskbar");
                Check(clock.ClientSize == OverlayGeometry.LayoutForDpi(MonitorDpi(clock.Handle)).ClientSize,
                    "Owner restoration retains correct screen geometry");
                // Exercise both directions while topmost ownership is active,
                // including the framework-owner DPI retained after recreation.
                foreach (var screen in Screen.AllScreens.Reverse().Concat(Screen.AllScreens))
                {
                    Call(clock, "PlaceAtScreenBottomRight", screen);
                    Pump(350);
                    var monitorDpi = MonitorDpi(clock.Handle);
                    Check(clock.ClientSize == OverlayGeometry.LayoutForDpi(monitorDpi).ClientSize,
                        "Topmost cross-monitor reflow uses destination DPI");
                    Check(clock.Right == screen.Bounds.Right && clock.Bottom == screen.Bounds.Bottom,
                        "Topmost cross-monitor reflow anchors destination");
                    Call(clock, "SetClickThrough", true);
                    Pump(350);
                    Check(clock.ClientSize == OverlayGeometry.LayoutForDpi(MonitorDpi(clock.Handle)).ClientSize,
                        "Topmost recreated window does not reuse the other display scale");
                    Call(clock, "SetClickThrough", false);
                    Pump(350);
                }
            }
            finally
            {
                Call(clock, "SetTopMost", false);
                DestroyWindow(taskbar);
            }
            Check(writes > 0, "Injected persistence exercised without user-profile writes");
            Console.WriteLine($"Live displays: {Screen.AllScreens.Length}; settings writes isolated: {writes}");
            Call(clock, "ExitApplication");
            Pump(50);
        }
    }

    private static readonly WindowProcedure TestWindowProcedure = DefWindowProc;
    private static IntPtr CreateTestTaskbar()
    {
        var instance = GetModuleHandle(null);
        var info = new WindowClass
        {
            Size = (uint)Marshal.SizeOf<WindowClass>(), Instance = instance,
            Procedure = Marshal.GetFunctionPointerForDelegate(TestWindowProcedure), ClassName = "Shell_TrayWnd",
        };
        RegisterClassEx(ref info);
        var bounds = Screen.PrimaryScreen!.Bounds;
        var taskbar = CreateWindowEx(0x08, "Shell_TrayWnd", "Isolated test taskbar", 0x80000000,
            bounds.Left, bounds.Bottom - 60, bounds.Width, 60, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        if (taskbar == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return taskbar;
    }

    private static int RunOnPrivateDesktop()
    {
        // Assign the desktop at process creation, before WinForms/COM can
        // create hidden windows that prevent SetThreadDesktop.
        var name = "BeijingTimeOverlayTests-" + Environment.ProcessId;
        var desktop = CreateDesktop(name, IntPtr.Zero, IntPtr.Zero, 0, 0x01FF, IntPtr.Zero);
        if (desktop == IntPtr.Zero)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        var startup = new StartupInfo
        {
            Size = Marshal.SizeOf<StartupInfo>(), Desktop = name, Flags = 0x100,
            StdInput = GetStdHandle(-10), StdOutput = GetStdHandle(-11), StdError = GetStdHandle(-12),
        };
        var executable = Environment.ProcessPath!;
        var assembly = Assembly.GetExecutingAssembly().Location;
        var command = new System.Text.StringBuilder(
            $"\"{executable}\" " + (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                ? $"\"{assembly}\" " : "") + "--desktop-child");
        ProcessInformation process = default;
        try
        {
            if (!CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, true, 0x08000000,
                    IntPtr.Zero, null, ref startup, out process))
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
            if (WaitForSingleObject(process.Process, 30000) != 0)
            {
                TerminateProcess(process.Process, 1);
                WaitForSingleObject(process.Process, 5000);
                throw new TimeoutException("Isolated UI test did not finish within 30 seconds.");
            }
            GetExitCodeProcess(process.Process, out var exitCode);
            return (int)exitCode;
        }
        finally
        {
            if (process.Thread != IntPtr.Zero) CloseHandle(process.Thread);
            if (process.Process != IntPtr.Zero) CloseHandle(process.Process);
            CloseDesktop(desktop);
        }
    }

    private static void Call(ClockForm clock, string method, params object[] arguments)
    {
        typeof(ClockForm).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(clock, arguments);
    }

    private static int MonitorDpi(IntPtr window)
    {
        var monitor = MonitorFromWindow(window, 2);
        // Use a separate API from production's unowned GetDpiForWindow probe.
        // The scale-category API returns 100% in this self-contained runtime
        // even while Windows reports 120/144 effective DPI for the monitor.
        if (GetDpiForMonitor(monitor, 0, out var dpi, out _) != 0 || dpi == 0)
        {
            throw new InvalidOperationException("Cannot read the live window's effective monitor DPI.");
        }
        GetScaleFactorForMonitor(monitor, out var scale);
        if ((int)Math.Round(96 * scale / 100d) != dpi)
        {
            Console.WriteLine($"Scale-category API reports {scale}%; effective monitor DPI={dpi}; HWND DPI={GetDpiForWindow(window)}");
        }
        return (int)dpi;
    }

    private static void Pump(int milliseconds)
    {
        var until = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < until)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateDesktop(string name, IntPtr device, IntPtr mode, uint flags, uint access, IntPtr security);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("shcore.dll")] private static extern int GetScaleFactorForMonitor(IntPtr monitor, out int scale);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowDpiAwarenessContext(IntPtr window);
    [DllImport("user32.dll")] private static extern bool AreDpiAwarenessContextsEqual(IntPtr first, IntPtr second);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XChars, YChars, FillAttribute, Flags;
        public short ShowWindow, ReservedSize;
        public IntPtr ReservedPointer, StdInput, StdOutput, StdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(string executable, System.Text.StringBuilder command, IntPtr processSecurity,
        IntPtr threadSecurity, bool inheritHandles, uint flags, IntPtr environment, string? directory,
        ref StartupInfo startup, out ProcessInformation process);
    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int which);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr process, uint exitCode);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? cls, string title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, System.Text.StringBuilder title, int size);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
    private delegate IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size, Style;
        public IntPtr Procedure;
        public int ClassExtra, WindowExtra;
        public IntPtr Instance, Icon, Cursor, Background;
        public string? MenuName, ClassName;
        public IntPtr SmallIcon;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? module);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WindowClass info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string title, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
}
