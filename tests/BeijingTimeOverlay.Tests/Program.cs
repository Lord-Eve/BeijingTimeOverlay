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
            TestPacificPreview();
            TestHoverDebugGate();
            TestHoverPointerSampling();
            TestHoverDebugRotation();

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

    private static void TestPacificPreview()
    {
        Check(!HoverInput.AnyMouseButtonDown(_ => 0), "Released physical buttons permit hovering");
        Check(!HoverInput.AnyMouseButtonDown(_ => 1), "Historical pressed bit does not block hovering");
        foreach (var button in new[] { 1, 2, 4, 5, 6 })
        {
            Check(HoverInput.AnyMouseButtonDown(key => key == button ? unchecked((short)0x8000) : (short)0),
                $"Physical button {button} suppresses preview while held");
        }
        var state = new HoverPreviewState();
        Check(!state.Update(true, 1000), "Hover entry waits");
        Check(!state.Update(true, 1299), "Brief pass does not show preview");
        Check(state.Update(true, 1300), "Preview shows after 300ms");
        Check(state.Update(true, 2000), "Stationary hover remains visible");
        Check(!state.Update(false, 2001), "Leave or blocked interaction hides immediately");
        Check(!state.Update(true, 2100), "Reentry starts a fresh delay");
        state.Reset();
        Check(!state.Update(true, 2400), "Explicit reset forgets old hover");
        Check(new OverlaySettings().ShowPacificTimeOnHover, "Hover preview defaults on");
        var legacy = System.Text.Json.JsonSerializer.Deserialize<OverlaySettings>("{\"TopMost\":true}")!;
        Check(legacy.ShowPacificTimeOnHover, "Existing settings acquire new default");
        var disabled = System.Text.Json.JsonSerializer.Deserialize<OverlaySettings>("{\"ShowPacificTimeOnHover\":false}")!;
        Check(!disabled.ShowPacificTimeOnHover, "Explicit disabled preference survives deserialization");

        var zone = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
        var beijingZone = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
        void Time(string utc, string time, string date, string abbreviation)
        {
            var actual = PacificTimeText.FromUtc(DateTimeOffset.Parse(utc, System.Globalization.CultureInfo.InvariantCulture), zone, beijingZone);
            Check((actual.Time, actual.Date, actual.Zone) == (time, date, abbreviation), $"Pacific date/DST: {utc} => {actual}");
        }
        Time("2026-03-08T09:59:59Z", "01:59:59", "2026/3/8", "PST");
        Time("2026-03-08T10:00:00Z", "03:00:00", "2026/3/8", "PDT");
        Time("2026-11-01T08:59:59Z", "01:59:59", "2026/11/1", "PDT");
        Time("2026-11-01T09:00:00Z", "01:00:00", "2026/11/1", "PST");
        Time("2027-01-01T00:30:00Z", "16:30:00", "2026/12/31", "PST");
        Time("2026-07-01T07:00:00Z", "00:00:00", "2026/7/1", "PDT");
        void Weekdays(string utc, string pacific, string beijing)
        {
            var actual = PacificTimeText.FromUtc(DateTimeOffset.Parse(utc, System.Globalization.CultureInfo.InvariantCulture), zone, beijingZone);
            Check(actual.Weekday == pacific && actual.BeijingWeekday == beijing, $"Weekdays at the same instant: {utc}");
            Check(actual.WeekdayComparison == $"{pacific}（北京时间：{beijing}）", "Chinese weekday comparison label");
        }
        Weekdays("2026-07-01T07:00:00Z", "星期三", "星期三");
        Weekdays("2026-07-01T15:59:59Z", "星期三", "星期三");
        Weekdays("2026-07-01T16:00:00Z", "星期三", "星期四");
        Weekdays("2026-07-02T06:59:59Z", "星期三", "星期四");
        Weekdays("2026-07-02T07:00:00Z", "星期四", "星期四");
        Weekdays("2026-12-31T16:00:00Z", "星期四", "星期五");
        Weekdays("2026-07-05T16:00:00Z", "星期日", "星期一");
        var weekdayNames = new[] { "星期日", "星期一", "星期二", "星期三", "星期四", "星期五", "星期六" };
        var sundayUtc = DateTimeOffset.Parse("2026-07-05T16:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        for (var day = 0; day < 7; day++)
            Weekdays(sundayUtc.AddDays(day).ToString("O"), weekdayNames[day], weekdayNames[(day + 1) % 7]);
        foreach (var dpi in new[] { 96, 120, 144, 168, 192, 240, 288 })
        {
            foreach (var work in new[] { new Rectangle(0, 0, 2560, 1380), new Rectangle(-2560, -1600, 2560, 1528) })
            {
                var anchorSize = OverlayGeometry.LayoutForDpi(dpi).ClientSize;
                foreach (var location in new[] { work.Location, new Point(work.Right - anchorSize.Width, work.Bottom),
                             new Point(work.Right - anchorSize.Width, work.Top), new Point(work.Left, work.Bottom) })
                {
                    var anchor = new Rectangle(location, anchorSize);
                    var card = OverlayGeometry.PacificCardBounds(anchor, work, dpi);
                    Check(work.Contains(card), $"Preview fully in work area, DPI {dpi}");
                    Check(!card.IntersectsWith(anchor), "Preview does not cover the clock");
                    Check(card.Size == OverlayGeometry.PacificCardSize(dpi), "Preview scales once from logical size");
                }
            }
            var size = OverlayGeometry.PacificCardSize(dpi);
            using var bitmap = new Bitmap(size.Width, size.Height);
            using var graphics = Graphics.FromImage(bitmap);
            using var label = new Font("Segoe UI", 12 * dpi / 96f, FontStyle.Regular, GraphicsUnit.Pixel);
            using var timeFont = new Font("Segoe UI", 26 * dpi / 96f, FontStyle.Regular, GraphicsUnit.Pixel);
            using var format = new StringFormat(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.NoWrap };
            var width = bitmap.Width - 28 * dpi / 96f;
            Check(graphics.MeasureString("太平洋时间 · 洛杉矶", label, PointF.Empty, format).Width <= width, "Preview title fits");
            Check(graphics.MeasureString("2026/12/31  ·  PST", label, PointF.Empty, format).Width <= width, "Preview date and zone fit");
            Check(graphics.MeasureString("23:59:59", timeFont, PointF.Empty, format).Width <= width, "Preview time fits");
            Check(78 * dpi / 96f + label.GetHeight(graphics) < bitmap.Height - 2, "Preview footer does not clip");
            Check(78 * dpi / 96f + label.GetHeight(graphics) < 98 * dpi / 96f, "Date and weekday rows do not overlap");
            Check(98 * dpi / 96f + label.GetHeight(graphics) < bitmap.Height - 2, "Weekday row does not clip");
            foreach (var pacific in weekdayNames)
                foreach (var beijing in weekdayNames)
                    Check(graphics.MeasureString($"{pacific}（北京时间：{beijing}）", label, PointF.Empty, format).Width <= width,
                        $"Weekday comparison fits at {dpi} DPI: {pacific}/{beijing}");
        }
    }

    private static void TestHoverDebugGate()
    {
        var gate = new HoverDebugGate();
        Check(gate.ShouldWrite("hover", "blocked", 1000), "First diagnostic snapshot is logged");
        for (var now = 1100; now < 2000; now += 100)
            Check(!gate.ShouldWrite("hover", "blocked", now), "Unchanged 100ms polls do not write again");
        Check(gate.ShouldWrite("hover", "blocked", 2000), "Unchanged state has a one-second heartbeat");
        Check(gate.ShouldWrite("hover", "eligible", 2001), "Brief eligibility transitions are not throttled away");
        Check(gate.ShouldWrite("hover", "blocked", 2002), "Brief resets remain visible in the timeline");
        Check(gate.ShouldWrite("menu", "opened", 2002), "Menu events are independent of hover snapshots");
        Check(!gate.ShouldWrite("hover", "blocked", 3001), "Transition restarts unchanged-state interval");
        Check(gate.ShouldWrite("hover", "blocked", 3002), "Heartbeat resumes one second after transition");
    }

    private static void TestHoverPointerSampling()
    {
        var point = new Point(10, 20);
        bool Unexpected() => throw new InvalidOperationException("Later input check should have been skipped");
        var missing = HoverInput.SamplePointer(null, Unexpected, _ => Unexpected(), _ => Unexpected());
        Check(!missing.Eligible && missing.Cursor is null && missing.MouseButtonsReleased is null &&
              missing.InsideBounds is null && missing.Exposed is null, "Missing cursor is distinguishable and skips all later checks");
        var pressed = HoverInput.SamplePointer(point, () => true, _ => Unexpected(), _ => Unexpected());
        Check(!pressed.Eligible && pressed.MouseButtonsReleased == false && pressed.InsideBounds is null && pressed.Exposed is null,
            "Held button is distinguishable and skips bounds and exposure queries");
        var outside = HoverInput.SamplePointer(point, () => false, _ => false, _ => Unexpected());
        Check(!outside.Eligible && outside.MouseButtonsReleased == true && outside.InsideBounds == false && outside.Exposed is null,
            "Outside cursor is distinguishable and skips exposure query");
        var order = new List<string>();
        HoverPointerState Sample(bool exposed) => HoverInput.SamplePointer(point,
            () => { order.Add("buttons"); return false; },
            value => { Check(value == point, "Bounds check uses sampled cursor"); order.Add("bounds"); return true; },
            value => { Check(value == point, "Exposure check uses sampled cursor"); order.Add("exposure"); return exposed; });
        var blocked = Sample(false);
        Check(!blocked.Eligible && blocked.InsideBounds == true && blocked.Exposed == false, "Blocked window is distinguishable");
        Check(order.SequenceEqual(new[] { "buttons", "bounds", "exposure" }), "Original query order is preserved without duplicate reads");
        order.Clear();
        Check(Sample(true).Eligible, "All pointer checks passing permits hover");
        Check(order.SequenceEqual(new[] { "buttons", "bounds", "exposure" }), "Eligible sample reads each input once");
    }

    private static void TestHoverDebugRotation()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "hover-log-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "hover-debug.log");
        var backup = path + ".1";
        const int limit = 62;
        var first = new string('甲', 10) + "\n"; // 31 UTF-8 bytes per entry, with no BOM.
        var second = new string('乙', 10) + "\n";
        var third = new string('丙', 10) + "\n";
        try
        {
            HoverDebugLog.AppendBounded(path, first, limit);
            Check(new FileInfo(path).Length == 31 && File.ReadAllText(path) == first, "UTF-8 entry is written without BOM");
            HoverDebugLog.AppendBounded(path, second, limit);
            Check(new FileInfo(path).Length == limit && !File.Exists(backup), "Exact byte limit does not rotate prematurely");
            HoverDebugLog.AppendBounded(path, third, limit);
            Check(File.ReadAllText(backup) == first + second && File.ReadAllText(path) == third,
                "Rotation preserves complete UTF-8 records in one backup");
            HoverDebugLog.AppendBounded(path, third, limit);
            HoverDebugLog.AppendBounded(path, first, limit);
            Check(File.ReadAllText(backup) == third + third && File.ReadAllText(path) == first,
                "Next rotation replaces the old backup");
            Check(Directory.GetFiles(directory).Length == 2 && new FileInfo(backup).Length <= limit && new FileInfo(path).Length <= limit,
                "Only two bounded files are retained");
            HoverDebugLog.AppendBounded(path, new string('x', limit + 1), limit);
            Check(File.ReadAllText(path) == first, "Oversized single record cannot grow the log beyond its cap");
            File.WriteAllText(path, new string('x', limit + 1));
            File.WriteAllText(backup, new string('x', limit + 1));
            HoverDebugLog.AppendBounded(path, second, limit);
            Check(File.ReadAllText(path) == second && !File.Exists(backup), "Oversized legacy logs are reset to bounded storage");
        }
        finally
        {
            File.Delete(path);
            File.Delete(backup);
            Directory.Delete(directory);
        }
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
            try { TestDirectHoverBeforeMenu(); TestLiveClock(); }
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
            TestLivePreview(clock, settings);
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
                TestLivePreview(clock, settings);
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
                // Clicks only reach other processes' windows (the taskbar clock)
                // when the window is both layered and transparent.
                var clickThroughStyle = GetWindowLong(clock.Handle, -20);
                Check((clickThroughStyle & 0x00080020) == 0x00080020, "Click-through window is layered and transparent");
                Check(GetLayeredWindowAttributes(clock.Handle, out _, out var clickThroughAlpha, out var layeredFlags) &&
                      (layeredFlags & 0x2) != 0 && clickThroughAlpha == 255,
                    "Click-through window stays fully opaque");
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
                Check((GetWindowLong(clock.Handle, -20) & 0x00080020) == 0, "Disabling click-through restores normal hit testing");

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
                TestLivePreview(clock, settings);
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

    private static void TestDirectHoverBeforeMenu()
    {
        // Inject only cursor coordinates on this non-input desktop. Button
        // reads still use the real production path; never move the user's mouse.
        ClockForm? clock = null;
        Point? cursor = null;
        var settings = new OverlaySettings { TopMost = false };
        using (clock = new ClockForm(settings, _ => { }, () => cursor))
        {
            clock.Show();
            Pump(400);
            var hoverTimer = (System.Windows.Forms.Timer)typeof(ClockForm)
                .GetField("_hoverTimer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(clock)!;
            hoverTimer.Stop();
            var keyboard = new byte[256];
            Check(GetKeyboardState(keyboard), "Read isolated thread input state");
            try
            {
                var stale = (byte[])keyboard.Clone();
                stale[1] = 0x80; // Thread thinks the left button is still down.
                Check(SetKeyboardState(stale), "Simulate stale background-thread mouse state");
                Check((Control.MouseButtons & MouseButtons.Left) != 0, "Legacy MouseButtons reads stale pressed state");
                cursor = new Point(clock.Left + clock.Width / 2, clock.Top + clock.Height / 2);
                Call(clock, "PollPacificHover");
                Thread.Sleep(350); // Preserve the stale thread snapshot; no user input.
                Call(clock, "PollPacificHover");
                var card = (PacificTimeCard?)typeof(ClockForm)
                    .GetField("_pacificCard", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(clock);
                Check(card?.Visible == true, "Direct hover works before any menu despite stale thread button state");
                cursor = null;
                Call(clock, "PollPacificHover");
                Check(!card!.Visible, "Unavailable input desktop/cursor dismisses preview");
                SetKeyboardState(keyboard);
                cursor = new Point(clock.Left + clock.Width / 2, clock.Top + clock.Height / 2);
                hoverTimer.Start();
                Pump(750);
                Check(card.Visible, "Real hover timer opens preview without visiting a menu");
                cursor = null;
                Pump(150);
                Check(!card.Visible, "Real hover timer dismisses preview after departure");
                Call(clock, "SetClickThrough", true);
                Pump(350);
                cursor = new Point(clock.Left + clock.Width / 2, clock.Top + clock.Height / 2);
                Pump(750);
                Check(card.Visible, "Direct timer hover works after click-through recreation without menu activation");
            }
            finally
            {
                SetKeyboardState(keyboard);
                // Keep the harness's outer message loop alive for the rest
                // of the suite; production ExitApplication exits that loop.
                Call(clock, "DisposeResources");
            }
        }
    }

    private static readonly WindowProcedure TestWindowProcedure = DefWindowProc;

    private static void TestLivePreview(ClockForm clock, OverlaySettings settings)
    {
        var timer = (System.Windows.Forms.Timer)typeof(ClockForm).GetField("_hoverTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(clock)!;
        PacificTimeCard? Card() => (PacificTimeCard?)typeof(ClockForm).GetField("_pacificCard", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(clock);
        void ShowPreview()
        {
            timer.Stop(); // Simulated input must not move the user's real cursor.
            clock.UpdatePacificHover(false, 0);
            clock.UpdatePacificHover(true, 1000);
            Check(Card()?.Visible != true, "Live card remains hidden before delay");
            clock.UpdatePacificHover(true, 1300);
            Check(Card()?.Visible == true, "Live card appears after delay");
        }
        var clockSize = clock.ClientSize;
        var foreground = GetForegroundWindow();
        ShowPreview();
        var card = Card()!;
        Check(GetForegroundWindow() == foreground, "Preview does not steal foreground focus");
        Check(GetWindow(card.Handle, 4) == clock.Handle, "Preview is owned by clock");
        Check((GetWindowLong(card.Handle, -20) & 0x080800A0) == 0x080800A0, "Preview is nonactivating, layered and click-through");
        Check(GetLayeredWindowAttributes(card.Handle, out _, out var alpha, out _) && alpha == 255, "Preview is actually opaque");
        Check(!card.ShowInTaskbar && Screen.FromHandle(clock.Handle).WorkingArea.Contains(card.Bounds), "Preview stays in work area");
        Check(clock.ClientSize == clockSize, "Preview leaves main clock geometry unchanged");
        Check(card.ClientSize == OverlayGeometry.PacificCardSize(MonitorDpi(clock.Handle)), "Live preview follows anchor monitor DPI");
        foreach (var topMost in new[] { true, false })
        {
            Call(clock, "SetTopMost", topMost);
            ShowPreview();
            Check(card.TopMost == topMost && ((GetWindowLong(card.Handle, -20) & 0x08) != 0) == topMost,
                "Preview initially follows the clock's topmost setting");
            var clockOwner = GetWindow(clock.Handle, 4);
            var clockStyle = GetWindowLong(clock.Handle, -20);
            var cardBounds = card.Bounds;
            // Reproduce the captured fault: managed Visible/TopMost stay set,
            // but native ownership and z-order no longer match the anchor.
            SetTestWindowOwner(card.Handle, IntPtr.Zero);
            Check(SetWindowPos(card.Handle, new IntPtr(topMost ? -2 : -1), 0, 0, 0, 0, 0x0013),
                "Can simulate native preview layer drift without hiding or activating it");
            Check(card.Visible && ((GetWindowLong(card.Handle, -20) & 0x08) != 0) != topMost,
                "Captured fault keeps the card logically visible in the wrong z-order band");
            clock.UpdatePacificHover(true, 1800);
            Check(GetWindow(card.Handle, 4) == clock.Handle, "Stationary hover restores lost native preview owner");
            Check(((GetWindowLong(card.Handle, -20) & 0x08) != 0) == topMost,
                "Stationary hover repairs native layer drift, including non-topmost mode");
            Check(card.Bounds == cardBounds && GetForegroundWindow() == foreground,
                "Preview repair preserves geometry and foreground focus");
            Check(GetWindow(clock.Handle, 4) == clockOwner && GetWindowLong(clock.Handle, -20) == clockStyle,
                "Preview repair leaves the main clock's native owner and style unchanged");
        }
        using (var image = new Bitmap(card.Width, card.Height))
        {
            card.UpdateTime(DateTimeOffset.Parse("2026-12-31T20:59:59Z"), TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time"),
                TimeZoneInfo.FindSystemTimeZoneById("China Standard Time"));
            card.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
            var captureDirectory = Environment.GetEnvironmentVariable("BJT_PREVIEW_CAPTURE_DIR");
            if (!string.IsNullOrEmpty(captureDirectory))
            {
                Directory.CreateDirectory(captureDirectory);
                image.Save(Path.Combine(captureDirectory, $"pacific-card-{MonitorDpi(clock.Handle)}dpi.png"));
            }
            var textPixels = 0;
            for (var y = image.Height * 2 / 3; y < image.Height - 3; y++)
                for (var x = 3; x < image.Width - 3; x++)
                {
                    var pixel = image.GetPixel(x, y);
                    if (pixel.R > 140 && pixel.G > 140 && pixel.B > 140) textPixels++;
                }
            Check(textPixels > 30, "Actual preview paints the footer");
        }
        clock.UpdatePacificHover(false, 1301);
        Check(!card.Visible, "Pointer departure hides live card");
        ShowPreview();
        Call(clock, "SetClickThrough", true);
        Pump(350);
        Check(!card.Visible, "Handle recreation dismisses preview");
        ShowPreview();
        Call(clock, "ToggleVisibility");
        Check(!card.Visible && !timer.Enabled, "Hidden clock stops preview and polling");
        Call(clock, "ToggleVisibility");
        Pump(350);
        ShowPreview();
        Call(clock, "SetPacificHover", false);
        Check(!card.Visible && !timer.Enabled && !settings.ShowPacificTimeOnHover, "Switch disables preview and polling");
        Call(clock, "SetPacificHover", true);
        Pump(350);
        ShowPreview();
        var menu = (ContextMenuStrip)typeof(ClockForm).GetField("_menu", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(clock)!;
        menu.Show(clock, Point.Empty);
        Check(!card.Visible, "Opening context menu hides preview");
        clock.UpdatePacificHover(true, 3000);
        clock.UpdatePacificHover(true, 3400);
        Check(!card.Visible, "Menu blocks preview from reappearing");
        menu.Close();
        foreach (var flag in new[] { "_hiddenByFullscreen", "_hiddenByUser" })
        {
            var field = typeof(ClockForm).GetField(flag, BindingFlags.Instance | BindingFlags.NonPublic)!;
            field.SetValue(clock, true);
            clock.UpdatePacificHover(true, 4000);
            clock.UpdatePacificHover(true, 4400);
            Check(!card.Visible, $"{flag} blocks preview");
            field.SetValue(clock, false);
        }
        Call(clock, "SetClickThrough", false);
        Pump(350);
        ShowPreview();
        SendMessage(clock.Handle, 0x0231, IntPtr.Zero, IntPtr.Zero);
        clock.UpdatePacificHover(true, 3000);
        clock.UpdatePacificHover(true, 3400);
        Check(!card.Visible, "Dragging suppresses preview");
        SendMessage(clock.Handle, 0x0232, IntPtr.Zero, IntPtr.Zero);
        Call(clock, "ResetPacificPreview");
        timer.Start();
    }
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

    private static void SetTestWindowOwner(IntPtr window, IntPtr owner)
    {
        if (IntPtr.Size == 8) SetWindowLongPtr64(window, -8, owner);
        else SetWindowLong32(window, -8, owner.ToInt32());
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
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetKeyboardState([Out] byte[] state);
    [DllImport("user32.dll")] private static extern bool SetKeyboardState(byte[] state);
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
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr64(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong32(IntPtr window, int index, int value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")]
    private static extern bool GetLayeredWindowAttributes(IntPtr window, out uint colorKey, out byte alpha, out uint flags);
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
