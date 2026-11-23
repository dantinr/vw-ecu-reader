using System.Drawing.Drawing2D;
using System.Globalization;

namespace VwEcuReader;

internal static class Palette
{
    public static readonly Color Background = Color.FromArgb(239, 244, 250);
    public static readonly Color White = Color.White;
    public static readonly Color Ink = Color.FromArgb(22, 41, 66);
    public static readonly Color Muted = Color.FromArgb(84, 104, 128);
    public static readonly Color Border = Color.FromArgb(214, 225, 239);
    public static readonly Color Blue = Color.FromArgb(30, 99, 202);
    public static readonly Color Green = Color.FromArgb(0, 117, 93);
    public static readonly Color Error = Color.FromArgb(165, 62, 33);
    public static readonly Font Title = new("Segoe UI", 24f, FontStyle.Bold);
    public static readonly Font Section = new("Microsoft YaHei UI", 10.5f, FontStyle.Bold);
    public static readonly Font MetricValue = new("Segoe UI Semibold", 20f);
}

internal sealed class TrendChart : Control
{
    private const int Rows = 4;
    private int metricIndex;
    private Sample[] samples = [];

    public TrendChart()
    {
        BackColor = Palette.White;
        AccessibleName = "读数趋势";
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    public void SetData(int selectedMetric, Sample[] history)
    {
        metricIndex = selectedMetric;
        samples = history;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        graphics.Clear(BackColor);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var scale = DeviceDpi / 96f;
        var plot = RectangleF.FromLTRB(48 * scale, 14 * scale, Width - 17 * scale, Height - 32 * scale);
        if (plot.Width <= 20 || plot.Height <= 20)
            return;

        var values = samples.Select(sample => sample.Values[metricIndex]).OfType<double>().ToArray();
        double min = values.Length == 0 ? 0 : values.Min();
        double max = values.Length == 0 ? 1 : values.Max();
        double padding = (max - min) * 0.12;
        if (padding == 0 && values.Length > 0)
            padding = Math.Max(0.5, Math.Abs(max) * 0.06);
        double step = NiceStep((max - min + 2 * padding) / 3);
        double bottom = Math.Floor((min - padding) / step) * step;

        using var grid = new Pen(Palette.Border);
        using var labels = new SolidBrush(Palette.Muted);
        using var axisFont = new Font("Segoe UI", 8.5f);
        using var right = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };
        for (int row = 0; row <= Rows; row++)
        {
            float y = plot.Bottom - plot.Height * row / Rows;
            graphics.DrawLine(grid, plot.Left, y, plot.Right, y);
            if (values.Length > 0)
                graphics.DrawString((bottom + step * row).ToString("0.##", CultureInfo.InvariantCulture),
                    axisFont, labels, new PointF(plot.Left - 7 * scale, y), right);
        }

        if (values.Length == 0)
        {
            using var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            graphics.DrawString("等待有效读数", Font, labels, plot, center);
            return;
        }

        DateTimeOffset end = samples[^1].Time;
        DateTimeOffset start = samples[0].Time;
        if ((end - start).TotalSeconds < 12)
            start = end.AddSeconds(-12);
        double duration = (end - start).TotalSeconds;
        graphics.DrawString(start.ToString("HH:mm:ss"), axisFont, labels, plot.Left, plot.Bottom + 8 * scale);
        graphics.DrawString(end.ToString("HH:mm:ss"), axisFont, labels,
            new PointF(plot.Right, plot.Bottom + 16 * scale), right);

        Color color = Metric.All[metricIndex].Color;
        using var trend = new Pen(color, 2.4f * scale);
        using var dot = new SolidBrush(color);
        PointF? previous = null;
        foreach (var sample in samples)
        {
            if (sample.Values[metricIndex] is not double reading)
            {
                previous = null;
                continue;
            }
            var point = new PointF(plot.Left + plot.Width * (float)Math.Clamp((sample.Time - start).TotalSeconds / duration, 0, 1),
                plot.Bottom - plot.Height * (float)((reading - bottom) / (step * Rows)));
            if (previous is PointF last)
                graphics.DrawLine(trend, last, point);
            graphics.FillEllipse(dot, point.X - 2.4f * scale, point.Y - 2.4f * scale, 4.8f * scale, 4.8f * scale);
            previous = point;
        }
    }

    private static double NiceStep(double raw)
    {
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double scaled = raw / magnitude;
        return (scaled <= 1 ? 1 : scaled <= 2 ? 2 : scaled <= 5 ? 5 : 10) * magnitude;
    }
}
