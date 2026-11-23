using System.Text;

namespace VwEcuReader;

internal sealed class DsgPanel : UserControl
{
    public const string Capability = "READER CAPABILITIES dsg-id";
    private static readonly (string Did, string Label)[] Fields =
    [
        ("F187", "软件件号"),
        ("F189", "软件版本"),
        ("F19E", "ODX 数据集"),
    ];

    private readonly Button readButton;
    private readonly TextBox[] values = new TextBox[Fields.Length];
    private readonly Label status = new()
    {
        AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Palette.Muted,
        Margin = new Padding(0, 8, 0, 0),
    };
    private long deadline;
    private int received;
    private string? result;
    private bool failed;

    public bool Pending => deadline != 0;

    public DsgPanel(Button readButton)
    {
        this.readButton = readButton;
        Dock = DockStyle.Fill;
        BackColor = Palette.White;
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 6,
            Padding = new Padding(18, 12, 18, 16),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 106));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.Controls.Add(readButton, 0, 0);
        layout.SetColumnSpan(readButton, 2);

        for (int index = 0; index < Fields.Length; index++)
        {
            var label = new Label
            {
                Text = Fields[index].Label, AutoSize = true, Anchor = AnchorStyles.Left,
                Margin = Padding.Empty,
            };
            var value = new TextBox
            {
                AccessibleName = Fields[index].Label, ReadOnly = true, Text = "—",
                Anchor = AnchorStyles.Left | AnchorStyles.Right, BackColor = Palette.Background,
                Font = new Font("Consolas", 10f),
            };
            values[index] = value;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            layout.Controls.Add(label, 0, index + 1);
            layout.Controls.Add(value, 1, index + 1);
        }

        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(status, 0, 4);
        layout.SetColumnSpan(status, 2);
        Controls.Add(layout);
    }

    public void UpdateAvailability(bool ready, string idleStatus)
    {
        readButton.Enabled = ready && !Pending;
        status.Text = Pending ? "正在识别变速箱控制器…" : result ?? idleStatus;
        status.ForeColor = failed ? Palette.Error : Palette.Muted;
    }

    public void Reset()
    {
        deadline = 0;
        received = 0;
        result = null;
        failed = false;
        foreach (var value in values)
            value.Text = "—";
    }

    public void Begin(long now)
    {
        Reset();
        deadline = now + 7000;
    }

    public bool Advance(long now) =>
        Pending && now >= deadline && Finish("串口等待超时。请查看日志。", isError: true);

    public bool AcceptLine(string line)
    {
        if (!Pending || !line.StartsWith("DSG ", StringComparison.Ordinal))
            return false;
        if (line == "DSG END")
            return Finish($"识别完成，收到 {received}/{Fields.Length} 项。", isError: received == 0);

        string[] parts = line.Split(' ', 4, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
            return false;
        if (parts[1] == "ERROR")
            return Finish(DescribeError(parts[2]), isError: true);

        int index = Array.FindIndex(Fields, field => field.Did == parts[1]);
        if (index < 0 || values[index].Text != "—")
            return false;
        if (parts.Length == 4 && parts[2] == "OK" && DecodeIdentity(parts[3]) is string identity)
        {
            values[index].Text = identity;
            received++;
        }
        else
        {
            values[index].Text = parts.Length == 4 && parts[2] == "NRC"
                ? $"TCU 拒绝读取 (NRC {parts[3]})"
                : DescribeError(parts[2]);
        }
        return false;
    }

    private static string? DecodeIdentity(string hex)
    {
        if (hex.Length == 0 || hex.Length > 186)
            return null;
        Span<byte> bytes = stackalloc byte[93];
        if (Convert.FromHexString(hex, bytes, out _, out int count) != System.Buffers.OperationStatus.Done)
            return null;

        while (count > 0 && bytes[count - 1] == 0)
            count--;
        if (count == 0 || bytes[..count].IndexOfAnyExceptInRange((byte)0x20, (byte)0x7E) >= 0)
            return hex;
        string text = Encoding.ASCII.GetString(bytes[..count]).Trim();
        return text.Length == 0 ? hex : text;
    }

    private bool Finish(string message, bool isError)
    {
        deadline = 0;
        result = message;
        failed = isError;
        return true;
    }

    private static string DescribeError(string code) => code switch
    {
        "TIMEOUT" => "TCU 响应超时",
        "CAN_ERROR" => "CAN 传输失败",
        "INVALID_FRAME" => "ISO-TP 响应格式错误",
        "COOLDOWN" => "请等两秒再读取。",
        "CAN_INSTALL" or "CAN_START" => "CAN B 启动失败。请查看串口日志。",
        "CAN_CLEANUP" => "CAN B 关闭失败。请重新启动板子。",
        _ => "识别响应格式错误",
    };
}
