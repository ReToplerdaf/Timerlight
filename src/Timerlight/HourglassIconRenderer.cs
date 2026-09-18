using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Timerlight;

/// <summary>Everything the renderer needs to know to draw one frame of the tray icon.</summary>
/// <param name="SandProgress">How far the current pour has got, 0 to 1. Sets the sand levels.</param>
/// <param name="ColorProgress">How far the whole interval has got, 0 to 1. Sets the colour.</param>
/// <param name="FlipAngle">0 while the glass stands still, 0 to 180 while it is turned over.</param>
internal readonly record struct HourglassState(
    double SandProgress,
    double ColorProgress,
    double FlipAngle,
    bool Finished,
    bool Paused,
    bool LightBackground,
    float Opacity);

/// <summary>
/// Draws the hourglass shown in the notification area. The sand drains from the upper bulb
/// into the lower one over one pour, its colour walks from green to red over the whole
/// interval, and the glass can be caught mid-turn between two pours.
/// </summary>
internal static class HourglassIconRenderer
{
    // The shape is laid out on a 100x100 design canvas and scaled to whatever the tray asks for.
    private const float DesignSize = 100f;

    // Rendered at 4x and shrunk back down: cheap supersampling keeps 16 px icons crisp.
    private const int SupersampleFactor = 4;

    private const float CenterX = 50f;
    private const float NeckY = 50f;
    private const float UpperBaseY = 14.5f;
    private const float LowerBaseY = 85.5f;
    private const float HalfBaseWidth = 29f;
    private const float BulbHeight = 35.5f;
    private const float GlassStroke = 5.5f;

    // A perfectly edge-on glass would give the transform a zero row, so keep a sliver of height.
    private const float MinimumFlipScale = 0.04f;

    private static readonly PointF[] UpperBulb =
    [
        new(CenterX - HalfBaseWidth, UpperBaseY),
        new(CenterX + HalfBaseWidth, UpperBaseY),
        new(CenterX, NeckY),
    ];

    private static readonly PointF[] LowerBulb =
    [
        new(CenterX - HalfBaseWidth, LowerBaseY),
        new(CenterX + HalfBaseWidth, LowerBaseY),
        new(CenterX, NeckY),
    ];

    private static readonly RectangleF TopCap = new(16f, 4.5f, 68f, 10f);
    private static readonly RectangleF BottomCap = new(16f, 85.5f, 68f, 10f);

    /// <summary>Renders a square icon bitmap of the requested edge length.</summary>
    internal static Bitmap Render(HourglassState state, int size)
    {
        int supersampledSize = size * SupersampleFactor;

        using var supersampled = new Bitmap(supersampledSize, supersampledSize, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(supersampled))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.Clear(Color.Transparent);
            graphics.ScaleTransform(supersampledSize / DesignSize, supersampledSize / DesignSize);

            if (state.FlipAngle != 0d)
            {
                ApplyFlip(graphics, state.FlipAngle);
            }

            DrawHourglass(graphics, state);
        }

        var icon = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(icon))
        {
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.Clear(Color.Transparent);

            using var attributes = new ImageAttributes();

            // Without this the bicubic filter samples past the edges and leaves a dark fringe.
            attributes.SetWrapMode(WrapMode.TileFlipXY);

            float opacity = Math.Clamp(state.Opacity, 0f, 1f);
            if (opacity < 0.999f)
            {
                var matrix = new ColorMatrix { Matrix33 = opacity };
                attributes.SetColorMatrix(matrix);
            }

            graphics.DrawImage(
                supersampled,
                new Rectangle(0, 0, size, size),
                0, 0, supersampledSize, supersampledSize,
                GraphicsUnit.Pixel,
                attributes);
        }

        return icon;
    }

    /// <summary>
    /// Turns the glass over by squashing it flat and letting it come back inverted, the way a
    /// real hourglass swings round its stand. A plain rotation would swing the corners outside
    /// the icon at 45 degrees; this stays within the square at every angle. The shape is
    /// symmetric about its neck, so a finished half-turn lands exactly on the mirrored drawing.
    /// </summary>
    private static void ApplyFlip(Graphics graphics, double angleDegrees)
    {
        float scaleY = (float)Math.Cos(angleDegrees * Math.PI / 180d);
        if (Math.Abs(scaleY) < MinimumFlipScale)
        {
            scaleY = angleDegrees <= 90d ? MinimumFlipScale : -MinimumFlipScale;
        }

        graphics.TranslateTransform(CenterX, NeckY);
        graphics.ScaleTransform(1f, scaleY);
        graphics.TranslateTransform(-CenterX, -NeckY);
    }

    private static void DrawHourglass(Graphics graphics, HourglassState state)
    {
        double sandProgress = Math.Clamp(state.SandProgress, 0d, 1d);
        double colorProgress = Math.Clamp(state.ColorProgress, 0d, 1d);

        Color frame = state.LightBackground
            ? Color.FromArgb(58, 58, 62)     // dark glass on a light taskbar
            : Color.FromArgb(238, 240, 244); // light glass on a dark taskbar

        Color sand = state.Paused ? Color.FromArgb(152, 155, 162) : SandColor(colorProgress);
        Color glassOutline = state.Finished && !state.Paused ? sand : frame;

        // A faint wash inside the bulbs keeps the silhouette readable at 16 px.
        using (var hollowBrush = new SolidBrush(Color.FromArgb(46, frame)))
        {
            graphics.FillPolygon(hollowBrush, UpperBulb);
            graphics.FillPolygon(hollowBrush, LowerBulb);
        }

        using (var sandBrush = new SolidBrush(sand))
        {
            PointF[]? upperSand = BuildUpperSand(sandProgress);
            if (upperSand is not null)
            {
                graphics.FillPolygon(sandBrush, upperSand);
            }

            PointF[]? lowerSand = BuildLowerSand(sandProgress);
            if (lowerSand is not null)
            {
                graphics.FillPolygon(sandBrush, lowerSand);
            }

            if (!state.Paused && sandProgress > 0.005d && sandProgress < 0.995d)
            {
                float pileTop = LowerBaseY - LowerSandDepth(sandProgress);
                using var streamPen = new Pen(Color.FromArgb(210, sand), 3f);
                graphics.DrawLine(streamPen, CenterX, NeckY, CenterX, pileTop);
            }
        }

        using (var glassPen = new Pen(glassOutline, GlassStroke) { LineJoin = LineJoin.Round })
        {
            graphics.DrawPolygon(glassPen, UpperBulb);
            graphics.DrawPolygon(glassPen, LowerBulb);
        }

        using (var frameBrush = new SolidBrush(frame))
        {
            FillRoundedRectangle(graphics, frameBrush, TopCap, 3.5f);
            FillRoundedRectangle(graphics, frameBrush, BottomCap, 3.5f);
        }
    }

    /// <summary>
    /// Sand still waiting in the upper bulb. The bulb is a triangle standing on its tip, so the
    /// area left scales with the square of the height - hence the square root.
    /// </summary>
    private static PointF[]? BuildUpperSand(double progress)
    {
        double remaining = 1d - progress;
        if (remaining <= 0.0001d)
        {
            return null;
        }

        float height = (float)(BulbHeight * Math.Sqrt(remaining));
        float halfWidth = HalfBaseWidth * height / BulbHeight;
        float top = NeckY - height;

        return
        [
            new PointF(CenterX - halfWidth, top),
            new PointF(CenterX + halfWidth, top),
            new PointF(CenterX, NeckY),
        ];
    }

    /// <summary>Sand already piled up in the lower bulb, levelled off at the top.</summary>
    private static PointF[]? BuildLowerSand(double progress)
    {
        if (progress <= 0.0001d)
        {
            return null;
        }

        float depth = LowerSandDepth(progress);
        float halfWidth = HalfBaseWidth * (BulbHeight - depth) / BulbHeight;
        float surface = LowerBaseY - depth;

        return
        [
            new PointF(CenterX - HalfBaseWidth, LowerBaseY),
            new PointF(CenterX + HalfBaseWidth, LowerBaseY),
            new PointF(CenterX + halfWidth, surface),
            new PointF(CenterX - halfWidth, surface),
        ];
    }

    /// <summary>How far the pile in the lower bulb has risen above its base.</summary>
    private static float LowerSandDepth(double progress)
    {
        double filled = Math.Clamp(progress, 0d, 1d);
        return (float)(BulbHeight * (1d - Math.Sqrt(1d - filled)));
    }

    /// <summary>Walks the hue from green through amber to red as the sitting wears on.</summary>
    private static Color SandColor(double progress)
    {
        double hue = 120d * Math.Pow(1d - progress, 1.25d);
        double value = 0.80d + (0.15d * progress);
        return FromHsv(hue, 0.92d, value);
    }

    private static Color FromHsv(double hue, double saturation, double value)
    {
        double chroma = value * saturation;
        double sector = Math.Clamp(hue, 0d, 360d) / 60d;
        double x = chroma * (1d - Math.Abs((sector % 2d) - 1d));
        double m = value - chroma;

        (double r, double g, double b) = (int)sector switch
        {
            0 => (chroma, x, 0d),
            1 => (x, chroma, 0d),
            2 => (0d, chroma, x),
            3 => (0d, x, chroma),
            4 => (x, 0d, chroma),
            _ => (chroma, 0d, x),
        };

        return Color.FromArgb(
            (int)Math.Round((r + m) * 255d),
            (int)Math.Round((g + m) * 255d),
            (int)Math.Round((b + m) * 255d));
    }

    private static void FillRoundedRectangle(Graphics graphics, Brush brush, RectangleF bounds, float radius)
    {
        float diameter = radius * 2f;
        using var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180f, 90f);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270f, 90f);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0f, 90f);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90f, 90f);
        path.CloseFigure();
        graphics.FillPath(brush, path);
    }
}
