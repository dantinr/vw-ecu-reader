using System.IO.Ports;
using Microsoft.Data.Sqlite;
using StackExchange.Redis;
using Timer = System.Windows.Forms.Timer;

namespace VwEcuReader;

internal sealed class Dashboard : Form
{
    private const int PollIntervalMs = 3000;
    private readonly ReaderComboBox ports = new() { Width = 91, AccessibleName = "串口" };
    private readonly Button refresh = ActionButton("刷新端口");
    private readonly Button connect = ActionButton("连接");
    private readonly Button read = ActionButton("读取一次");
    private readonly Button poll = ActionButton("开启连续读取");
    private readonly Button save = ActionButton("保存日志");
    private readonly Label connection = new() { AutoSize = true };
    private readonly Label activity = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
    private readonly ReaderHeader header = new() { Dock = DockStyle.Fill, Margin = Padding.Empty };
    private readonly Label subtitle = new()
    {
        Dock = DockStyle.Top, Height = 24, Text = "OBD-II  实时仪表盘", BackColor = Color.Transparent,
    };
    private readonly ReaderComboBox themeSelector = new()
    {
        Width = 90, AccessibleName = "主题",
    };
    private readonly FlowLayoutPanel actions = new()
    {
        Dock = DockStyle.Fill, Padding = new Padding(12, 4, 8, 4),
        Margin = new Padding(0, 0, 0, 8), WrapContents = false,
    };
    private readonly ReaderTabs tabs = new() { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 8, 0) };
    private readonly TabPage engine = new("发动机") { Padding = new Padding(0, 6, 0, 0) };
    private readonly TabPage transmission = new("变速箱");
    private readonly TableLayoutPanel logPanel = new()
    {
        Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2,
        Padding = new Padding(14, 8, 14, 10), Margin = new Padding(8, 0, 0, 0),
    };
    private readonly ReaderComboBox storage = new()
    {
        Width = 93, AccessibleName = "存储方式",
    };
    private readonly Label databaseStatus = new()
    {
        AutoSize = true, Anchor = AnchorStyles.Right, Text = "SQLite · 等待读取",
    };
    private readonly RichTextBox log = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None,
        Font = new Font("Consolas", 9.25f), WordWrap = false, DetectUrls = false, TabStop = false,
    };
    private readonly MetricView[] metrics = new MetricView[Metric.All.Length];
    private readonly Dictionary<int, MetricWindow> metricWindows = new();
    private readonly Queue<Sample> history = new();
    private readonly Telemetry telemetry = new();
    private readonly Button readDsg = ActionButton("读取 TCU 信息");
    private readonly DsgPanel dsgPanel;
    private readonly SampleStore sampleStore;
    private readonly Timer timer = new() { Interval = 40 };
    private SerialPort? port;
    private bool isReader;
    private bool supportsDsg;
    private bool polling;
    private long probeAt;
    private long probeDeadline;
    private long readyAt;
    private long nextPoll;
    private bool closing;
    private bool activityError;
    private StatusTone storageTone;
    private ReaderTheme theme = ReaderTheme.Classic;
    private string ThemePath => Path.Combine(Path.GetDirectoryName(sampleStore.DatabasePath)!, "theme.txt");
    private StorageBackend SelectedBackend => (StorageBackend)storage.SelectedItem!;

    public Dashboard(SampleStore sampleStore)
    {
        this.sampleStore = sampleStore;
        dsgPanel = new DsgPanel(readDsg);
        Text = "VW ECU Reader";
        Font = new Font("Microsoft YaHei UI", 9f);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(980, 660);
        ClientSize = new Size(1150, 730);
        storage.Items.AddRange([StorageBackend.SQLite, StorageBackend.Memurai]);
        storage.SelectedItem = StorageBackend.SQLite;
        themeSelector.Items.AddRange(ReaderTheme.All);
        Controls.Add(BuildLayout());
        LoadTheme();
        themeSelector.SelectedIndexChanged += (_, _) => ChangeTheme();

        refresh.Click += (_, _) => RefreshPorts();
        connect.Click += (_, _) => { if (port is null) OpenPort(); else ClosePort(); };
        ports.SelectedIndexChanged += (_, _) => UpdateActions();
        read.Click += (_, _) => RequestRead(Environment.TickCount64);
        readDsg.Click += (_, _) => RequestDsgIdentity();
        poll.Click += (_, _) => TogglePolling();
        save.Click += (_, _) => SaveLog();
        save.Enabled = false;
        try { storage.SelectedItem = sampleStore.LoadBackend(); }
        catch (Exception error) when (IsStorageError(error))
        {
            AppendLog("读取存储设置失败  " + error.Message);
        }
        storage.SelectedIndexChanged += (_, _) =>
        {
            try { sampleStore.SaveBackend(SelectedBackend); }
            catch (Exception error) when (IsStorageError(error))
            {
                AppendLog("保存存储设置失败  " + error.Message);
            }
            _ = ShowStorageReadyAsync(SelectedBackend);
        };
        Shown += (_, _) => _ = ShowStorageReadyAsync(SelectedBackend);
        telemetry.LineReceived += OnSerialLine;
        telemetry.ValuesChanged += ShowValues;
        telemetry.Completed += OnSample;
        timer.Tick += (_, _) => Tick();
        FormClosing += WaitForDatabase;
        FormClosed += (_, _) =>
        {
            timer.Dispose();
            port?.Dispose();
            sampleStore.Dispose();
            foreach (var window in metricWindows.Values.ToArray())
                window.Close();
        };

        RefreshPorts();
        SetActivity("连接板子后开始接收串口数据。");
        UpdateActions();
        timer.Start();
    }

    private Control BuildLayout()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(24, 16, 24, 14),
            ColumnCount = 1, RowCount = 4,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 57));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));

        var title = new Label
        {
            Dock = DockStyle.Top, Height = 43, Text = "VW ECU Reader",
            Font = ReaderTheme.TitleFont, BackColor = Color.Transparent,
        };
        var themeControls = new FlowLayoutPanel
        {
            Dock = DockStyle.Right, Width = 142, BackColor = Color.Transparent,
            Padding = new Padding(4, 16, 0, 0), WrapContents = false,
        };
        themeControls.Controls.Add(new Label { Text = "主题", AutoSize = true, Margin = new Padding(0, 6, 6, 0) });
        themeControls.Controls.Add(themeSelector);
        header.Controls.Add(subtitle);
        header.Controls.Add(title);
        header.Controls.Add(themeControls);
        layout.Controls.Add(header, 0, 0);

        actions.Controls.Add(new Label { Text = "串口", AutoSize = true, Margin = new Padding(1, 10, 9, 0) });
        ports.Margin = new Padding(0, 7, 8, 0);
        actions.Controls.Add(ports);
        actions.Controls.AddRange([refresh, connect, read, poll]);
        connection.Margin = new Padding(12, 10, 0, 0);
        actions.Controls.Add(connection);
        actions.Controls.Add(new Label { Text = "存储", AutoSize = true, Margin = new Padding(18, 10, 8, 0) });
        storage.Margin = new Padding(0, 7, 0, 0);
        actions.Controls.Add(storage);
        layout.Controls.Add(actions, 0, 1);

        var metricGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2, Margin = new Padding(0, 0, 8, 0),
        };
        for (int column = 0; column < 3; column++)
            metricGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
        metricGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        metricGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        for (int index = 0; index < metrics.Length; index++)
        {
            int metricIndex = index;
            int column = index % 3;
            int row = index / 3;
            var metric = new MetricView(metricIndex, () => OpenMetricWindow(metricIndex))
            {
                Margin = new Padding(0, 0, column < 2 ? 8 : 0, row == 0 ? 8 : 0),
            };
            metrics[index] = metric;
            metricGrid.Controls.Add(metric, column, row);
        }

        var lower = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        lower.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 72));
        lower.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
        engine.Controls.Add(metricGrid);
        transmission.Controls.Add(dsgPanel);
        tabs.TabPages.AddRange([engine, transmission]);
        lower.Controls.Add(tabs, 0, 0);
        logPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        logPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        logPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        logPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        logPanel.Controls.Add(new Label
        {
            Text = "串口日志", AutoSize = true, Anchor = AnchorStyles.Left, Font = ReaderTheme.SectionFont,
        }, 0, 0);
        save.Anchor = AnchorStyles.Right;
        logPanel.Controls.Add(save, 1, 0);
        logPanel.Controls.Add(log, 0, 1);
        logPanel.SetColumnSpan(log, 2);
        lower.Controls.Add(logPanel, 1, 0);
        layout.Controls.Add(lower, 0, 2);

        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        activity.Margin = new Padding(3, 3, 8, 0);
        databaseStatus.Margin = new Padding(0, 3, 2, 0);
        footer.Controls.Add(activity, 0, 0);
        footer.Controls.Add(databaseStatus, 1, 0);
        layout.Controls.Add(footer, 0, 3);
        return layout;
    }

    private void LoadTheme()
    {
        try
        {
            if (File.Exists(ThemePath))
            {
                string name = File.ReadAllText(ThemePath).Trim();
                theme = ReaderTheme.All.FirstOrDefault(option => option.Mode.ToString() == name) ?? ReaderTheme.Classic;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AppendLog("读取主题设置失败  " + error.Message);
        }
        themeSelector.SelectedItem = theme;
        ApplyTheme();
    }

    private void ChangeTheme()
    {
        if (themeSelector.SelectedItem is not ReaderTheme selected)
            return;
        theme = selected;
        ApplyTheme();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ThemePath)!);
            File.WriteAllText(ThemePath, theme.Mode.ToString());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AppendLog("保存主题设置失败  " + error.Message);
        }
    }

    private void ApplyTheme()
    {
        BackColor = theme.Background;
        ForeColor = theme.Text;
        header.ApplyTheme(theme);
        subtitle.ForeColor = theme.Muted;
        actions.BackColor = theme.Surface;
        engine.BackColor = theme.Background;
        transmission.BackColor = theme.Surface;
        tabs.ApplyTheme(theme);
        logPanel.BackColor = log.BackColor = theme.Surface;
        log.ForeColor = theme.Muted;
        foreach (var selector in new[] { ports, storage, themeSelector })
            selector.ApplyTheme(theme);
        foreach (var button in new[] { refresh, read, poll, save })
            theme.StyleButton(button);
        theme.StyleButton(connect, primary: true);
        foreach (var metric in metrics)
            metric.ApplyTheme(theme);
        foreach (var window in metricWindows.Values)
            window.ApplyTheme(theme);
        dsgPanel.ApplyTheme(theme);
        activity.ForeColor = activityError ? theme.Error : theme.Muted;
        databaseStatus.ForeColor = theme.StatusColor(storageTone);
        connection.ForeColor = isReader ? theme.Success : theme.Muted;
    }

    private static Button ActionButton(string text) => new ReaderButton
    {
        Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        MinimumSize = new Size(64, 32), Padding = new Padding(8, 0, 8, 0),
        Margin = new Padding(0, 3, 8, 0), FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false,
    };

    private void RefreshPorts()
    {
        var lastPort = ports.SelectedItem as string;
        string[] available;
        try { available = SerialPort.GetPortNames().Order(StringComparer.OrdinalIgnoreCase).ToArray(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            SetActivity("无法枚举串口  " + error.Message, isError: true);
            return;
        }
        ports.Items.Clear();
        ports.Items.AddRange(available);
        int knownPort = Array.IndexOf(available, lastPort ?? "COM3");
        ports.SelectedIndex = knownPort >= 0 ? knownPort : available.Length == 1 ? 0 : -1;
        UpdateActions();
    }

    private void OpenPort()
    {
        if (ports.SelectedItem is not string name)
            return;
        var candidate = new SerialPort(name, 115200)
        {
            DtrEnable = true, RtsEnable = false, Handshake = Handshake.None, WriteTimeout = 250,
        };
        try { candidate.Open(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            candidate.Dispose();
            AppendLog("打开串口失败  " + error.Message);
            SetActivity($"无法打开 {name}。请检查 USB 连接及串口是否被占用。", isError: true);
            return;
        }

        port = candidate;
        isReader = false;
        supportsDsg = false;
        dsgPanel.Reset();
        telemetry.Reset();
        history.Clear();
        ShowValues();
        UpdateCharts();
        long now = Environment.TickCount64;
        probeAt = now + 350;
        probeDeadline = now + 3000;
        readyAt = 0;
        AppendLog($"已打开 {name} / 115200");
        SetActivity("正在识别板子固件…");
        UpdateActions();
    }

    private void ClosePort(string? reason = null)
    {
        var closing = port;
        port = null;
        try { closing?.Dispose(); }
        catch (IOException) { }
        polling = false;
        isReader = false;
        supportsDsg = false;
        dsgPanel.Reset();
        telemetry.Reset();
        ShowValues();
        AppendLog(reason ?? "串口已断开");
        SetActivity(reason ?? "串口已断开", isError: reason is not null);
        RefreshPorts();
    }

    private void Tick()
    {
        if (port is null)
            return;
        long now = Environment.TickCount64;
        try
        {
            telemetry.Feed(port.ReadExisting(), now);
            telemetry.Advance(now);
            if (dsgPanel.Advance(now))
                readyAt = now + 2100;
            if (!isReader && probeAt != 0 && now >= probeAt)
            {
                probeAt = 0;
                port.Write("version\n");
            }
            if (!isReader && probeDeadline != 0 && now >= probeDeadline)
            {
                probeDeadline = 0;
                SetActivity("未识别到 OBD-II 只读固件。请检查串口和板子固件。", isError: true);
            }
            if (polling && now >= nextPoll)
                RequestRead(now);
            UpdateActions();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException)
        {
            ClosePort("串口通信中断  " + error.Message);
        }
    }

    private void OnSerialLine(string line)
    {
        AppendLog(line);
        if (isReader && line.StartsWith("OBD-II READ ONLY |", StringComparison.Ordinal))
        {
            // A board reset invalidates the current request and pauses periodic reads.
            isReader = false;
            supportsDsg = false;
            polling = false;
            dsgPanel.Reset();
            telemetry.Reset();
            long now = Environment.TickCount64;
            probeAt = now + 350;
            probeDeadline = now + 3000;
            ShowValues();
            SetActivity("板子已重启，正在重新识别。");
        }
        if (!isReader && Telemetry.IsReaderReply(line))
        {
            isReader = true;
            probeAt = probeDeadline = 0;
            SetActivity("只读固件已就绪。");
        }
        if (line == DsgPanel.Capability)
            supportsDsg = true;
        if (dsgPanel.AcceptLine(line))
            readyAt = Environment.TickCount64 + 2100;
    }

    private void RequestRead(long now)
    {
        if (port is null || !isReader || telemetry.Pending || dsgPanel.Pending || now < readyAt)
            return;
        try { port.Write("read\n"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException)
        {
            ClosePort("读取指令发送失败  " + error.Message);
            return;
        }
        telemetry.Begin(now);
        AppendLog("> read");
        SetActivity("正在读取车辆数据…");
        UpdateActions();
    }

    private void RequestDsgIdentity()
    {
        if (!readDsg.Enabled || port is null)
            return;
        try { port.Write("dsg-id\n"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException)
        {
            ClosePort("TCU 识别指令发送失败  " + error.Message);
            return;
        }

        dsgPanel.Begin(Environment.TickCount64);
        AppendLog("> dsg-id");
        UpdateActions();
    }

    private void TogglePolling()
    {
        polling = !polling;
        if (polling)
        {
            long now = Environment.TickCount64;
            nextPoll = now;
            SetActivity("连续读取已开启，每轮完成后等约 3 秒。");
            RequestRead(now);
        }
        else
            SetActivity("连续读取已暂停。");
        UpdateActions();
    }

    private void OnSample(Sample sample)
    {
        if (port is not null)
            _ = SaveSampleAsync(sample, port.PortName);

        history.Enqueue(sample);
        while (history.Count > 75)
            history.Dequeue();
        long now = Environment.TickCount64;
        readyAt = now + 2100;
        nextPoll = now + PollIntervalMs;
        int received = sample.Values.Count(value => value.HasValue);
        string message = $"{sample.Time:HH:mm:ss}  收到 {received}/6 项";
        if (sample.Error is not null)
        {
            if (polling)
                message += "  ·  连续读取已暂停";
            polling = false;
            message += "  ·  " + sample.Error;
        }
        SetActivity(message, isError: sample.Error is not null);
        ShowValues();
        UpdateCharts();
        UpdateActions();
    }

    private async Task ShowStorageReadyAsync(StorageBackend backend)
    {
        if (backend == StorageBackend.SQLite)
        {
            ShowStorageStatus(backend, "等待读取");
            return;
        }

        ShowStorageStatus(backend, "正在连接");
        try
        {
            await sampleStore.CheckMemuraiAsync();
            ShowStorageStatus(backend, "已连接", StatusTone.Success);
        }
        catch (Exception error) when (IsStorageError(error))
        {
            ShowStorageStatus(backend, "连接失败", StatusTone.Error);
            AppendLog($"Memurai 连接失败 ({SampleStore.MemuraiAddress})  {error.Message}");
        }
    }

    private async Task SaveSampleAsync(Sample sample, string portName)
    {
        StorageBackend backend = SelectedBackend;
        try
        {
            ShowStorageStatus(backend, "正在保存");
            await sampleStore.AppendAsync(sample, portName, backend);
            ShowStorageStatus(backend, "已保存", StatusTone.Success);
        }
        catch (Exception error) when (IsStorageError(error))
        {
            ShowStorageStatus(backend, "保存失败", StatusTone.Error);
            string target = backend == StorageBackend.Memurai ? SampleStore.MemuraiAddress : sampleStore.DatabasePath;
            AppendLog($"{backend} 保存失败 ({target})  {error.Message}");
        }
    }

    private void ShowStorageStatus(StorageBackend backend, string status, StatusTone tone = StatusTone.Neutral)
    {
        if (SelectedBackend != backend)
            return;
        databaseStatus.Text = $"{backend} · {status}";
        storageTone = tone;
        databaseStatus.ForeColor = theme.StatusColor(tone);
    }

    private static bool IsStorageError(Exception error) =>
        error is SqliteException or RedisException or TimeoutException or IOException or UnauthorizedAccessException;

    private async void WaitForDatabase(object? sender, FormClosingEventArgs args)
    {
        if (closing)
        {
            args.Cancel = true;
            return;
        }
        Task pending = sampleStore.Completion;
        if (pending.IsCompleted)
            return;
        args.Cancel = true;
        closing = true;
        timer.Stop();
        Enabled = false;
        try { await pending; }
        catch (Exception error) when (IsStorageError(error))
        {
            // The pending operation reports its own error.
        }
        closing = false;
        Close();
    }

    private void ShowValues()
    {
        var previous = history.LastOrDefault();
        for (int index = 0; index < metrics.Length; index++)
        {
            double? value = telemetry.Pending ? telemetry.GetValue(index) : previous?.Values[index];
            metrics[index].ShowValue(value);
            if (metricWindows.TryGetValue(index, out var window))
                window.View.ShowValue(value);
        }
    }

    private void OpenMetricWindow(int index)
    {
        if (metricWindows.TryGetValue(index, out var existing))
        {
            if (existing.WindowState == FormWindowState.Minimized)
                existing.WindowState = FormWindowState.Normal;
            existing.Activate();
            return;
        }

        var window = new MetricWindow(index, Font);
        window.ApplyTheme(theme);
        var workArea = Screen.FromControl(this).WorkingArea;
        int offset = 22 * metricWindows.Count;
        window.Location = new Point(
            Math.Clamp(Left + 36 + offset, workArea.Left, Math.Max(workArea.Left, workArea.Right - window.Width)),
            Math.Clamp(Top + 72 + offset, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - window.Height)));
        window.FormClosed += (_, _) => metricWindows.Remove(index);
        metricWindows.Add(index, window);
        window.View.ShowTrend(history.ToArray());
        ShowValues();
        window.Show();
    }

    private void UpdateCharts()
    {
        var samples = history.ToArray();
        for (int index = 0; index < metrics.Length; index++)
        {
            metrics[index].ShowTrend(samples);
            if (metricWindows.TryGetValue(index, out var window))
                window.View.ShowTrend(samples);
        }
    }

    private void UpdateActions()
    {
        bool attached = port is not null;
        bool mayRead = isReader && !telemetry.Pending && !dsgPanel.Pending && Environment.TickCount64 >= readyAt;
        ports.Enabled = refresh.Enabled = !attached;
        connect.Enabled = attached || ports.SelectedIndex >= 0;
        connect.Text = attached ? "断开" : "连接";
        read.Enabled = mayRead && !polling;
        poll.Enabled = isReader && !dsgPanel.Pending;
        string dsgStatus = !isReader ? "连接板子后可识别 TCU。"
            : !supportsDsg ? "请刷入支持 TCU 识别的固件。"
            : polling ? "暂停连续读取后可识别 TCU。"
            : "等待读取 TCU 信息。";
        dsgPanel.UpdateAvailability(mayRead && supportsDsg && !polling, dsgStatus);
        poll.Text = polling ? "暂停连续读取" : "开启连续读取";
        connection.Text = !attached ? "未连接" : $"{port!.PortName}  /  115200  ·  {(isReader ? "已连接" : "串口已打开")}";
        connection.ForeColor = isReader ? theme.Success : theme.Muted;
    }

    private void SetActivity(string text, bool isError = false)
    {
        activityError = isError;
        activity.Text = text;
        activity.ForeColor = isError ? theme.Error : theme.Muted;
    }

    private void AppendLog(string line)
    {
        if (log.TextLength > 250_000)
        {
            int firstLineToKeep = log.GetLineFromCharIndex(25_000) + 1;
            log.Select(0, log.GetFirstCharIndexFromLine(firstLineToKeep));
            log.SelectedText = "";
        }
        log.AppendText($"{DateTime.Now:HH:mm:ss.fff}  {line}{Environment.NewLine}");
        save.Enabled = true;
        log.SelectionStart = log.TextLength;
        log.ScrollToCaret();
    }

    private void SaveLog()
    {
        using var dialog = new SaveFileDialog
        {
            Filter = "日志文件 (*.log)|*.log|文本文件 (*.txt)|*.txt",
            FileName = $"vw-ecu-{DateTime.Now:yyyyMMdd-HHmmss}.log",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        try
        {
            File.WriteAllText(dialog.FileName, log.Text);
            SetActivity("日志已保存  " + dialog.FileName);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            SetActivity("无法保存日志  " + error.Message, isError: true);
        }
    }
}
