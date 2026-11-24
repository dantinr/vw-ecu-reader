namespace VwEcuReader;

internal sealed class CanCapturePanel : UserControl
{
    private readonly Button toggle;
    private readonly Label status = new() { Font = ReaderTheme.SectionFont, Dock = DockStyle.Fill };
    private readonly Label details = new() { Dock = DockStyle.Fill, AutoEllipsis = true };
    private readonly Label[] counters = new Label[3];
    private readonly TextBox pathField = new() { Dock = DockStyle.Fill, ReadOnly = true, AccessibleName = "CAN 帧数据库路径" };
    private readonly DataGridView grid = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, VirtualMode = true,
        AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        BorderStyle = BorderStyle.None, EnableHeadersVisualStyles = false,
        AccessibleName = "最近收到的 CAN 帧",
    };
    private ReaderTheme theme = ReaderTheme.Classic;
    private StatusTone tone;
    private CanCapture? displayed;
    private CanFrame[] preview = [];
    private long lastUpdate;
    private long lastReceived;

    public CanCapturePanel(Button toggle, string path)
    {
        this.toggle = toggle;
        Dock = DockStyle.Fill;
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new Padding(12, 8, 12, 10),
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 29));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 67));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 33));

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        actions.Controls.Add(toggle);
        actions.Controls.Add(new Label
        {
            Text = "CAN B · 500 kbit/s · 只听", AutoSize = true, Margin = new Padding(8, 11, 0, 0),
        });
        layout.Controls.Add(actions, 0, 0);
        layout.Controls.Add(status, 0, 1);
        status.AutoEllipsis = true;

        var metrics = new TableLayoutPanel { ColumnCount = 3, RowCount = 2, Dock = DockStyle.Fill, Margin = Padding.Empty };
        metrics.RowStyles.Add(new RowStyle(SizeType.Absolute, 23));
        metrics.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        string[] titles = ["接收速率（帧/秒）", "本次收到（帧）", "本次已保存（帧）"];
        for (int i = 0; i < counters.Length; i++)
        {
            metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / counters.Length));
            metrics.Controls.Add(new Label { Text = titles[i], AutoSize = true, Anchor = AnchorStyles.Left }, i, 0);
            counters[i] = new Label { Font = ReaderTheme.ReadingFont, Dock = DockStyle.Fill, Text = "—" };
            metrics.Controls.Add(counters[i], i, 1);
        }
        layout.Controls.Add(metrics, 0, 2);
        layout.Controls.Add(details, 0, 3);
        layout.Controls.Add(new Label
        {
            Text = $"最近 {CanCaptureLog.PreviewSize} 帧", Dock = DockStyle.Fill, Font = ReaderTheme.SectionFont,
        }, 0, 4);
        AddColumn("板端时间（µs）", 25, 96);
        AddColumn("CAN ID", 16, 78);
        AddColumn("类型", 19, 84);
        AddColumn("DLC", 8, 41);
        AddColumn("数据（十六进制）", 32, 145);
        grid.CellValueNeeded += ShowFrame;
        layout.Controls.Add(grid, 0, 5);

        var location = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        location.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        location.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        location.Controls.Add(new Label { Text = "SQLite", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        pathField.Text = path;
        pathField.Margin = new Padding(5, 7, 0, 0);
        location.Controls.Add(pathField, 1, 0);
        layout.Controls.Add(location, 0, 6);
        Controls.Add(layout);
    }

    public void ApplyTheme(ReaderTheme value)
    {
        theme = value;
        BackColor = theme.Surface;
        ForeColor = theme.Text;
        pathField.BackColor = theme.Background;
        pathField.ForeColor = theme.Text;
        theme.StyleButton(toggle, primary: true);
        grid.BackgroundColor = theme.Plot;
        grid.GridColor = theme.Border;
        grid.ColumnHeadersDefaultCellStyle.BackColor = theme.Background;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = theme.Text;
        grid.DefaultCellStyle.BackColor = theme.Surface;
        grid.DefaultCellStyle.ForeColor = theme.Text;
        grid.DefaultCellStyle.SelectionBackColor = theme.Accent;
        grid.DefaultCellStyle.SelectionForeColor = theme.OnAccent;
        grid.DefaultCellStyle.Font = new Font("Consolas", 9f);
        status.ForeColor = theme.StatusColor(tone);
        details.ForeColor = theme.Muted;
    }

    public void UpdateAction(bool canStart, CanCapture? capture)
    {
        bool busy = capture?.Busy == true;
        toggle.Text = busy ? "停止采集" : "开始采集";
        toggle.Enabled = busy
            ? capture!.Phase is not (CanCapturePhase.Stopping or CanCapturePhase.Saving)
            : canStart;
    }

    public void ShowStatus(CanCapture? capture, string idle, long now)
    {
        var current = capture?.Progress;
        bool newSession = capture != displayed;
        if (newSession)
        {
            displayed = capture;
            lastReceived = lastUpdate = 0;
        }

        bool listening = capture is { Busy: true, Phase: CanCapturePhase.Listening };
        if (!listening || newSession)
            counters[0].Text = "—";
        else if (now > lastUpdate && lastUpdate != 0)
            counters[0].Text = ((current!.Received - lastReceived) * 1000.0 / (now - lastUpdate)).ToString("N0");
        lastReceived = current?.Received ?? 0;
        lastUpdate = now;
        counters[1].Text = current?.Received.ToString("N0") ?? "—";
        counters[2].Text = current?.Saved.ToString("N0") ?? "—";

        long? missing = current?.MissingAtEnd(capture?.End);
        string? missingText = missing?.ToString("N0");
        if (capture is { Busy: false, End.Total: null })
            missingText = missing > 0 ? $"≥{missingText}" : "未知";
        CanBoardStats? stats = capture?.Stats;
        details.Text = $"板端丢帧 {stats?.Missed.ToString("N0") ?? "—"}    " +
            $"FIFO 溢出 {stats?.Overrun.ToString("N0") ?? "—"}    " +
            $"电脑缺帧 {missingText ?? "—"}    总线错误 {stats?.BusErrors.ToString("N0") ?? "—"}";
        if (current?.Invalid > 0)
            details.Text += $"    异常数据包 {current.Invalid:N0}";

        (string message, StatusTone color) = (capture, current) switch
        {
            (_, { Error: not null }) => ("保存失败，详细原因见串口日志。", StatusTone.Error),
            ({ Busy: false, End.Reason: "STOP" }, not null) when current.Complete(capture.End) =>
                (current.Received == 0 ? "采集完成 · 未收到帧" : "采集完成 · 已全部保存", StatusTone.Success),
            ({ Busy: false, End.Reason: "STOP" }, _) => ("采集结束 · 有缺帧或数据包异常", StatusTone.Error),
            ({ Busy: false, End.Reason: "CANCELED" }, _) => ("采集已取消", StatusTone.Neutral),
            ({ Busy: false, End.Reason: "BUSY" }, _) => ("板端仍有采集在结束，请稍后重试。", StatusTone.Error),
            ({ Busy: false, End.Reason: "CAN_INSTALL" or "CAN_START" or "USB_BUFFER" or "START_TIMEOUT" }, _) =>
                ("采集未能启动，详细原因见串口日志。", StatusTone.Error),
            ({ Busy: false }, _) => ("采集中断 · 已保存的帧可在 SQLite 中查看", StatusTone.Error),
            ({ Phase: CanCapturePhase.Preparing }, _) => ("正在打开帧数据库…", StatusTone.Neutral),
            ({ Phase: CanCapturePhase.Starting }, _) => ("正在启动 CAN B 监听…", StatusTone.Neutral),
            ({ Phase: CanCapturePhase.Stopping or CanCapturePhase.Saving }, _) =>
                ("正在停止采集并写入剩余数据…", StatusTone.Neutral),
            (_, _) when listening && (missing > 0 || current?.Invalid > 0 || stats is { Missed: > 0 } or { Overrun: > 0 }) =>
                ("监听中 · 已检测到缺帧或数据包异常", StatusTone.Error),
            (_, { Received: 0 }) when listening => ("监听中 · 尚未收到帧", StatusTone.Neutral),
            (_, _) when listening && counters[0].Text == "0" => ("监听中 · 暂无新帧", StatusTone.Neutral),
            (_, _) when listening => ("正在接收 CAN B 帧", StatusTone.Success),
            _ => (idle, StatusTone.Neutral),
        };
        tone = color;
        status.Text = message;
        status.ForeColor = theme.StatusColor(tone);

        CanFrame[] rows = current?.Preview ?? [];
        if (!ReferenceEquals(rows, preview))
        {
            preview = rows;
            grid.RowCount = rows.Length;
            grid.Invalidate();
        }
    }

    private void AddColumn(string title, float weight, int minimumWidth) =>
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = title, FillWeight = weight, MinimumWidth = minimumWidth,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });

    private void ShowFrame(object? sender, DataGridViewCellValueEventArgs args)
    {
        if (args.RowIndex >= preview.Length)
            return;
        CanFrame frame = preview[preview.Length - 1 - args.RowIndex];
        args.Value = args.ColumnIndex switch
        {
            0 => frame.TimeUs.ToString(),
            1 => frame.Id.ToString(frame.Extended ? "X8" : "X3"),
            2 => (frame.Extended ? "扩展" : "标准") + (frame.Remote ? " RTR" : " 数据"),
            3 => frame.Dlc.ToString(),
            4 => frame.Data.Length == 0 ? "—" : string.Join(' ', frame.Data.Select(b => b.ToString("X2"))),
            _ => null,
        };
    }
}
