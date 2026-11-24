using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace VwEcuReader;

internal sealed record Metric(string Key, string Label, string Unit, string Format)
{
    public static readonly Metric[] All =
    [
        new("rpm", "发动机转速", "rpm", "0.##"),
        new("coolant", "冷却液温度", "°C", "0"),
        new("speed", "车速", "km/h", "0"),
        new("load", "发动机负荷", "%", "0.0"),
        new("throttle", "节气门开度", "%", "0.0"),
        new("intake", "进气温度", "°C", "0"),
    ];
}

internal sealed record Sample(DateTimeOffset Time, double?[] Values, string? Error);

internal sealed class Telemetry
{
    private static readonly Regex MetricLine = new(
        @"^(Engine RPM|rpm|coolant|speed|load|throttle|intake):\s*(-?\d+(?:\.\d+)?)\s*(?:rpm)?$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private readonly StringBuilder lineBuffer = new();
    private bool discardingLine;
    private double?[]? values;
    private string? error;
    private long deadline;
    private long lastSampleLine;

    public bool Pending => values is not null;
    public Func<string, bool>? ConsumeLine { get; set; }
    public event Action<string>? LineReceived;
    public event Action? ValuesChanged;
    public event Action<Sample>? Completed;

    public void Begin(long now)
    {
        values = new double?[Metric.All.Length];
        error = null;
        deadline = now + 3000;
        lastSampleLine = 0;
        ValuesChanged?.Invoke();
    }

    public double? GetValue(int index) => values?[index];

    public void Feed(string chunk, long now)
    {
        foreach (char character in chunk)
        {
            if (character is '\r' or '\n')
            {
                if (lineBuffer.Length > 0 && !discardingLine)
                    AcceptLine(lineBuffer.ToString().Trim(), now);
                lineBuffer.Clear();
                discardingLine = false;
            }
            else if (!discardingLine)
            {
                if (lineBuffer.Length < 4096)
                    lineBuffer.Append(character);
                else
                {
                    lineBuffer.Clear();
                    discardingLine = true;
                }
            }
        }
    }

    public void Advance(long now)
    {
        // Older firmware has no dedicated end marker when CSV storage is unavailable.
        // The longest gap between its output lines during a read is about 400 ms.
        if (Pending && (now >= deadline || (lastSampleLine != 0 && now - lastSampleLine >= 700)))
            Finish();
    }

    public void Reset()
    {
        values = null;
        error = null;
        lineBuffer.Clear();
        discardingLine = false;
    }

    public static bool IsReaderReply(string line) =>
        line == "Only 'read' is supported" ||
        line == "Commands: 'read', 'version'" ||
        (line.StartsWith("VW ECU Reader firmware:", StringComparison.Ordinal) &&
         (line.Contains("(obd2_read,", StringComparison.Ordinal) ||
          line.Contains("(obd2_read_diag,", StringComparison.Ordinal)));

    private void AcceptLine(string line, long now)
    {
        if (line.Length == 0 || ConsumeLine?.Invoke(line) == true)
            return;
        LineReceived?.Invoke(line);

        var match = MetricLine.Match(line);
        if (match.Success && double.TryParse(match.Groups[2].Value, NumberStyles.Float,
                CultureInfo.InvariantCulture, out double value) && Math.Abs(value) <= 16383.75)
        {
            string key = match.Groups[1].Value;
            if (key.Equals("Engine RPM", StringComparison.OrdinalIgnoreCase))
                key = "rpm";
            int index = Array.FindIndex(Metric.All, metric => metric.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (index == 0 && values?[0] is not null)
                Finish();
            if (!Pending)
                Begin(now);
            values![index] = value;
            lastSampleLine = now;
            ValuesChanged?.Invoke();
        }
        else if (IsReadError(line))
        {
            if (!Pending && !line.StartsWith("Wait 2 seconds", StringComparison.Ordinal))
                Begin(now);
            if (Pending)
            {
                error = line;
                lastSampleLine = now;
            }
        }
        else if (Pending && (line == "CSV saved" || line.StartsWith("CSV write failed", StringComparison.Ordinal)
                 || line.StartsWith("CSV limit reached", StringComparison.Ordinal)))
        {
            Finish();
        }
    }

    private static bool IsReadError(string line) =>
        line.StartsWith("Wait 2 seconds", StringComparison.Ordinal) ||
        (line.StartsWith("PID ", StringComparison.Ordinal) &&
         (line.Contains("cannot queue request", StringComparison.Ordinal) ||
          line.Contains("CAN transmission failed", StringComparison.Ordinal) ||
          line.Contains("no valid reply", StringComparison.Ordinal))) ||
        line is "CAN B: driver installation failed" or "CAN B: start failed" ||
        line.StartsWith("CAN B: cleanup failed", StringComparison.Ordinal);

    private void Finish()
    {
        var sample = new Sample(DateTimeOffset.Now, values!,
            error ?? (values!.All(value => value is null) ? "读取超时，未收到数据" : null));
        values = null;
        error = null;
        Completed?.Invoke(sample);
    }
}
