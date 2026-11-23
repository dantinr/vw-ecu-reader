using System.Drawing.Drawing2D;

namespace VwEcuReader;

internal enum ThemeMode { Classic, F1, Wrc }
internal enum StatusTone { Neutral, Success, Error }

internal sealed record ReaderTheme(
    ThemeMode Mode, string Name, Color Background, Color Surface, Color Plot,
    Color Text, Color Muted, Color Border, Color Accent, Color OnAccent,
    Color Success, Color Error, Color[] Traces)
{
    public static readonly Font TitleFont = new("Segoe UI", 24f, FontStyle.Bold);
    public static readonly Font SectionFont = new("Microsoft YaHei UI", 10.5f, FontStyle.Bold);
    public static readonly Font ReadingFont = new("Segoe UI Semibold", 20f);
    public static readonly Font RacingReadingFont = new("Consolas", 22f, FontStyle.Bold);

    public static readonly ReaderTheme Classic = new(
        ThemeMode.Classic, "经典", Rgb(0xEFF4FA), Color.White, Color.White,
        Rgb(0x162942), Rgb(0x546880), Rgb(0xD6E1EF), Rgb(0x1E63CA), Color.White,
        Rgb(0x00755D), Rgb(0xA53E21),
        [Rgb(0x1A65D3), Rgb(0xCB4D21), Rgb(0x007E7C), Rgb(0x7B54BA), Rgb(0x9E5E19), Rgb(0x1479A8)]);

    public static readonly ReaderTheme[] All =
    [
        Classic,
        new(ThemeMode.F1, "F1", Rgb(0x101116), Rgb(0x1C1E26), Rgb(0x15171E),
            Rgb(0xF4F4F8), Rgb(0xB0B5C3), Rgb(0x343846), Rgb(0xFF5558), Rgb(0x190D11),
            Rgb(0x78E4AB), Rgb(0xFFA29A),
            [Rgb(0xFF6867), Rgb(0xFFAD74), Rgb(0x70D7FF), Rgb(0xB2AAF8), Rgb(0xFBDA87), Rgb(0x7CDEC0)]),
        new(ThemeMode.Wrc, "WRC", Rgb(0x111A19), Rgb(0x1C2825), Rgb(0x14201D),
            Rgb(0xF1F4EC), Rgb(0xB4C5BC), Rgb(0x394E46), Rgb(0xFFC454), Rgb(0x251B0C),
            Rgb(0x9EE4B0), Rgb(0xFFAD96),
            [Rgb(0xFFD071), Rgb(0xFF997B), Rgb(0x83D9B9), Rgb(0xB7E78B), Rgb(0xD0B6F2), Rgb(0x9ACAF1)]),
    ];

    public override string ToString() => Name;

    public Color StatusColor(StatusTone tone) => tone switch
    {
        StatusTone.Success => Success,
        StatusTone.Error => Error,
        _ => Muted,
    };

    public void StyleButton(Button button, bool primary = false)
    {
        if (button is ReaderButton action)
            action.ApplyTheme(this);
        button.BackColor = primary ? Accent : Surface;
        button.ForeColor = primary ? OnAccent : Text;
        button.FlatAppearance.BorderColor = primary ? Accent : Border;
    }

    private static Color Rgb(int hex) => Color.FromArgb(unchecked((int)0xFF000000) | hex);
}

internal sealed class ReaderButton : Button
{
    private ReaderTheme theme = ReaderTheme.Classic;

    public void ApplyTheme(ReaderTheme value)
    {
        theme = value;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Enabled || theme.Mode == ThemeMode.Classic)
        {
            base.OnPaint(e);
            return;
        }

        e.Graphics.Clear(theme.Surface);
        using var border = new Pen(theme.Border);
        e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, theme.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}

internal sealed class ReaderComboBox : ComboBox
{
    private ReaderTheme theme = ReaderTheme.Classic;

    public ReaderComboBox()
    {
        DropDownStyle = ComboBoxStyle.DropDownList;
        FlatStyle = FlatStyle.Flat;
        DrawMode = DrawMode.OwnerDrawFixed;
    }

    public void ApplyTheme(ReaderTheme value)
    {
        theme = value;
        BackColor = theme.Surface;
        ForeColor = theme.Text;
        Invalidate();
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0)
            return;

        bool selected = (e.State & (DrawItemState.Selected | DrawItemState.ComboBoxEdit)) == DrawItemState.Selected;
        using var fill = new SolidBrush(selected ? theme.Accent : theme.Surface);
        e.Graphics.FillRectangle(fill, e.Bounds);
        TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), Font, e.Bounds,
            selected ? theme.OnAccent : Enabled ? theme.Text : theme.Muted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        if (selected)
            e.DrawFocusRectangle();
    }
}

internal sealed class ReaderHeader : Panel
{
    private ReaderTheme theme = ReaderTheme.Classic;

    public void ApplyTheme(ReaderTheme value)
    {
        theme = value;
        BackColor = theme.Background;
        Invalidate(true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (theme.Mode == ThemeMode.Classic)
            return;

        float scale = DeviceDpi / 96f;
        int bottom = Height - (int)(8 * scale);
        using var line = new Pen(theme.Border);
        using var accent = new SolidBrush(theme.Accent);
        e.Graphics.DrawLine(line, 0, bottom, Width, bottom);
        e.Graphics.FillRectangle(accent, 0, bottom - 2 * scale, 62 * scale, 3 * scale);

        if (Width < 680 * scale)
            return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        float endX = Width - 212 * scale;
        float width = (theme.Mode == ThemeMode.F1 ? 19 : 31) * scale;
        float lean = (theme.Mode == ThemeMode.F1 ? 39 : 17) * scale;
        using var stripe = new SolidBrush(Color.FromArgb(38, theme.Accent));
        for (int index = 0; index < 3; index++)
        {
            float x = endX - index * (width + 8 * scale);
            e.Graphics.FillPolygon(stripe,
                [new PointF(x, 8 * scale), new PointF(x + width, 8 * scale),
                 new PointF(x + width - lean, bottom), new PointF(x - lean, bottom)]);
        }
    }
}

internal sealed class ReaderTabs : TabControl
{
    private ReaderTheme theme = ReaderTheme.Classic;

    public ReaderTabs()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SelectedIndexChanged += (_, _) => Invalidate();
    }

    public void ApplyTheme(ReaderTheme value)
    {
        theme = value;
        BackColor = theme.Background;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(theme.Background);
        using var border = new Pen(theme.Border);
        int bottom = DisplayRectangle.Top - 1;
        e.Graphics.DrawLine(border, 0, bottom, Width, bottom);
        for (int index = 0; index < TabCount; index++)
        {
            var bounds = GetTabRect(index);
            bool selected = index == SelectedIndex;
            using var fill = new SolidBrush(selected ? theme.Surface : theme.Plot);
            e.Graphics.FillRectangle(fill, bounds);
            if (selected)
            {
                using var accent = new SolidBrush(theme.Accent);
                float height = 2 * DeviceDpi / 96f;
                e.Graphics.FillRectangle(accent, bounds.Left, bounds.Bottom - height, bounds.Width, height);
            }
            TextRenderer.DrawText(e.Graphics, TabPages[index].Text, Font, bounds,
                selected ? theme.Text : theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            if (selected && Focused && ShowFocusCues)
                ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(bounds, -3, -3), theme.Text, theme.Surface);
        }
    }
}
