namespace BeijingTimeOverlay;

internal readonly record struct OverlayLayout(
    int Dpi,
    Size ClientSize,
    float FontPixels,
    float PaddingX,
    float TimeTop,
    float DateTop);

internal readonly record struct OverlayDisplay(string Id, Rectangle Bounds, int Dpi, bool IsPrimary);

internal static class OverlayGeometry
{
    // 96-DPI logical pixels. Scale from these constants, never from the
    // previously scaled size, so repeated monitor changes cannot compound.
    public static OverlayLayout LayoutForDpi(int dpi)
    {
        dpi = dpi > 0 ? dpi : 96;
        var scale = dpi / 96f;
        // Preserve the original 10-point text, expressed in physical pixels
        // so painting does not apply the cached window DPI a second time.
        return new OverlayLayout(dpi,
            new Size((int)Math.Round(84 * scale), (int)Math.Round(48 * scale)),
            (10 * 96f / 72f) * scale, 6 * scale, 7 * scale, 26 * scale);
    }

    public static OverlayDisplay SelectDisplay(
        IReadOnlyList<OverlayDisplay> displays, string? savedId, bool followPrimary)
    {
        if (displays.Count == 0)
        {
            throw new ArgumentException("At least one display is required.", nameof(displays));
        }

        if (!followPrimary && !string.IsNullOrEmpty(savedId))
        {
            foreach (var display in displays)
            {
                if (string.Equals(display.Id, savedId, StringComparison.OrdinalIgnoreCase))
                {
                    return display;
                }
            }
        }

        foreach (var display in displays)
        {
            if (display.IsPrimary)
            {
                return display;
            }
        }

        return displays[0];
    }

    public static Point PositionFromOffsets(
        Rectangle screen, Size size, int dpi, double rightDip, double bottomDip)
    {
        var scale = (dpi > 0 ? dpi : 96) / 96d;
        var maxRight = Math.Max(0, screen.Width - size.Width);
        var maxBottom = Math.Max(0, screen.Height - size.Height);
        rightDip = double.IsFinite(rightDip) ? rightDip : 0;
        bottomDip = double.IsFinite(bottomDip) ? bottomDip : 0;
        var right = (int)Math.Round(Math.Clamp(rightDip * scale, 0, maxRight));
        var bottom = (int)Math.Round(Math.Clamp(bottomDip * scale, 0, maxBottom));
        return ClampLocation(new Point(screen.Right - size.Width - right,
            screen.Bottom - size.Height - bottom), size, screen);
    }

    public static Point ClampLocation(Point location, Size size, Rectangle screen)
    {
        return new Point(
            Math.Clamp(location.X, screen.Left, Math.Max(screen.Left, screen.Right - size.Width)),
            Math.Clamp(location.Y, screen.Top, Math.Max(screen.Top, screen.Bottom - size.Height)));
    }

    public static (double Right, double Bottom) OffsetsForPosition(
        Point location, Size size, Rectangle screen, int dpi)
    {
        location = ClampLocation(location, size, screen);
        var scale = (dpi > 0 ? dpi : 96) / 96d;
        return ((screen.Right - location.X - size.Width) / scale,
            (screen.Bottom - location.Y - size.Height) / scale);
    }

    public static bool IsLegacyClockCorner(Rectangle window, Rectangle screen, int dpi)
    {
        var tolerance = 32 * (dpi > 0 ? dpi : 96) / 96d;
        return screen.Contains(window) &&
            screen.Right - window.Right <= tolerance &&
            screen.Bottom - window.Bottom <= tolerance;
    }
}
