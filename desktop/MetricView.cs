using System.Globalization;

namespace VwEcuReader;

internal sealed class MetricView : UserControl
{
    private readonly int metricIndex;
    private readonly Label reading = new()
    {
        AutoSize = true, ForeColor = Palette.Ink, Font = Palette.MetricValue, Text = "—", Margin = Padding.Empty,
    };
    private readonly TrendChart chart = new() { Dock = DockStyle.Fill, Margin = Padding.Empty };

    public MetricView(int metricIndex, Action? openWindow = null)
    {
        this.metricIndex = metricIndex;
        var metric = Metric.All[metricIndex];
        Dock = DockStyle.Fill;
        BackColor = Palette.White;
        AccessibleName = metric.Label;
        chart.AccessibleName = metric.Label + "趋势";

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3,
            Padding = new Padding(11, 7, 10, 6), Margin = Padding.Empty,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 31));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var heading = new Label
        {
            Text = metric.Label, AutoSize = true, Anchor = AnchorStyles.Left,
            Margin = Padding.Empty, ForeColor = Palette.Ink, Font = Palette.Section,
        };
        layout.Controls.Add(heading, 0, 0);
        if (openWindow is not null)
        {
            var popOut = new Button
            {
                Text = "独立窗口", AccessibleName = $"在独立窗口查看{metric.Label}",
                Anchor = AnchorStyles.Right, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlatStyle = FlatStyle.Flat, BackColor = Palette.White, ForeColor = Palette.Blue,
                Margin = new Padding(2, 0, 0, 0), UseVisualStyleBackColor = false,
            };
            popOut.FlatAppearance.BorderColor = Palette.Border;
            popOut.Click += (_, _) => openWindow();
            layout.Controls.Add(popOut, 1, 0);
        }

        var unit = new Label
        {
            Text = metric.Unit, AutoSize = true, ForeColor = Palette.Muted, Margin = new Padding(5, 13, 0, 0),
        };
        var readingRow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        readingRow.Controls.AddRange([reading, unit]);
        layout.Controls.Add(readingRow, 0, 1);
        layout.SetColumnSpan(readingRow, 2);
        layout.Controls.Add(chart, 0, 2);
        layout.SetColumnSpan(chart, 2);
        Controls.Add(layout);
    }

    public void ShowValue(double? value) =>
        reading.Text = value?.ToString(Metric.All[metricIndex].Format, CultureInfo.InvariantCulture) ?? "—";

    public void ShowTrend(Sample[] samples) => chart.SetData(metricIndex, samples);
}

internal sealed class MetricWindow : Form
{
    public MetricView View { get; }

    public MetricWindow(int metricIndex, Font font)
    {
        Text = Metric.All[metricIndex].Label + " | VW ECU Reader";
        Font = font;
        ForeColor = Palette.Ink;
        BackColor = Palette.Background;
        AutoScaleMode = AutoScaleMode.Dpi;
        Padding = new Padding(12);
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(440, 305);
        MinimumSize = new Size(300, 250);
        View = new MetricView(metricIndex);
        Controls.Add(View);
    }
}
