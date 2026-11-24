using System.Globalization;

namespace VwEcuReader;

internal readonly record struct CanFrame(long Sequence, long TimeUs, int Id,
    bool Extended, bool Remote, byte Dlc, byte[] Data);

internal readonly record struct CanBoardStats(uint Missed, uint Overrun, uint BusErrors);

internal sealed record CanCaptureEnd(string Reason, long? Total = null, CanBoardStats? Stats = null);

internal sealed record CanCaptureProgress(long Received, long Saved, long Missing, long Invalid,
    long LastSequence, CanFrame[] Preview, string? Error)
{
    public long MissingAtEnd(CanCaptureEnd? end) =>
        Math.Max(Missing, (end?.Total ?? 0) - Received);

    public bool Complete(CanCaptureEnd? end) =>
        end is { Reason: "STOP", Total: long total, Stats: { Missed: 0, Overrun: 0 } } &&
        total == Received && total == LastSequence && Saved == Received &&
        Missing == 0 && Invalid == 0 && Error is null;
}

internal enum CanCapturePhase { Preparing, Starting, Listening, Stopping, Saving }

internal sealed class CanCapture
{
    public const string Capability = "READER CAPABILITIES can-capture-v1";
    private readonly string token = Random.Shared.NextInt64(1, 1L + uint.MaxValue).ToString("X8");
    private readonly string dataPrefix;
    private readonly CanCaptureLog log;
    private bool started;
    private long deadline;
    private long nextKeepalive;
    private string? stopReason;
    private readonly TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public CanCapture(string path, string portName, string? firmware)
    {
        dataPrefix = $"CAN1 DATA {token} ";
        log = new CanCaptureLog(path, portName, firmware);
        Completion = Task.WhenAll(log.Completion, finished.Task);
    }

    public CanCapturePhase Phase { get; private set; }
    public Task Completion { get; }
    public bool Busy => !Completion.IsCompleted;
    public CanCaptureProgress Progress => log.Progress;
    public CanBoardStats? Stats { get; private set; }
    public CanCaptureEnd? End { get; private set; }

    public string? Advance(long now)
    {
        if (Phase == CanCapturePhase.Saving)
            return null;
        if (Phase == CanCapturePhase.Stopping)
        {
            if (now >= deadline)
                Interrupt(stopReason ?? "STOP_TIMEOUT");
            return null;
        }
        if (Progress.Error is not null)
            return Stop(now, "STORAGE_ERROR");
        if (stopReason == "QUEUE_FULL")
            return Stop(now, stopReason);

        if (Phase == CanCapturePhase.Preparing && log.Ready)
        {
            Phase = CanCapturePhase.Starting;
            deadline = now + 3500;
            return $"canopen {token}";
        }
        if ((Phase is CanCapturePhase.Starting or CanCapturePhase.Listening) && now >= deadline)
            return Stop(now, Phase == CanCapturePhase.Starting ? "START_TIMEOUT" : "STREAM_TIMEOUT");
        if (Phase == CanCapturePhase.Listening && now >= nextKeepalive)
        {
            nextKeepalive = now + 1000;
            return $"canping {token}";
        }
        return null;
    }

    public string? Stop(long now, string? reason = null)
    {
        if (Phase == CanCapturePhase.Preparing)
        {
            Interrupt(reason ?? "CANCELED");
            return null;
        }
        if (Phase is CanCapturePhase.Starting or CanCapturePhase.Listening)
        {
            stopReason ??= reason;
            Phase = CanCapturePhase.Stopping;
            // Allow the board's five-second lease and two-second drain to expire on USB failure.
            deadline = now + 8000;
            return $"canstop {token}";
        }
        return null;
    }

    public void Interrupt(string reason) => Finish(new(reason, Stats: Stats));

    // Returns a control line for the serial log. DATA packets go straight to the recorder.
    public string? Accept(string line, long now)
    {
        if (Phase is CanCapturePhase.Preparing or CanCapturePhase.Saving)
            return null;
        if (line.StartsWith(dataPrefix, StringComparison.Ordinal))
        {
            if (started)
            {
                if (Progress.Error is null && stopReason != "QUEUE_FULL" && !log.Enqueue(line, dataPrefix.Length))
                    stopReason = "QUEUE_FULL";
                if (Phase == CanCapturePhase.Listening)
                    deadline = now + 3500;
            }
            return null;
        }
        if (line.StartsWith("CAN1 DATA ", StringComparison.Ordinal))
            return null;

        string[] parts = line.Split(' ');
        if (parts.Length < 3 || parts[2] != token)
            return null;
        switch (parts[1])
        {
            case "START" when parts.Length == 3 && !started:
                started = true;
                if (Phase == CanCapturePhase.Starting)
                {
                    Phase = CanCapturePhase.Listening;
                    nextKeepalive = now + 1000;
                    deadline = now + 3500;
                }
                return line;
            case "STATS" when parts.Length == 6 && started && TryStats(parts.AsSpan(3), out var stats):
                Stats = stats;
                if (Phase == CanCapturePhase.Listening)
                    deadline = now + 3500;
                return null;
            case "END" when parts.Length == 8 && started &&
                long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out long total) &&
                TryStats(parts.AsSpan(4, 3), out var final):
                Stats = final;
                Finish(new(stopReason ?? parts[7], total, final));
                return line;
            case "ERROR" when parts.Length == 4:
                Interrupt(parts[3]);
                return line;
            default:
                return null;
        }
    }

    private void Finish(CanCaptureEnd end)
    {
        if (Phase == CanCapturePhase.Saving)
            return;
        End = end;
        Phase = CanCapturePhase.Saving;
        log.Finish(end);
        finished.SetResult();
    }

    private static bool TryStats(ReadOnlySpan<string> parts, out CanBoardStats stats)
    {
        stats = default;
        if (!uint.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out uint missed) ||
            !uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out uint overrun) ||
            !uint.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out uint errors))
            return false;

        stats = new(missed, overrun, errors);
        return true;
    }
}
