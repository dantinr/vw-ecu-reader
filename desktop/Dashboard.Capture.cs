namespace VwEcuReader;

internal sealed partial class Dashboard
{
    private readonly TabPage canFrames = new("CAN 帧");
    private readonly Button captureButton = ActionButton("开始采集");
    private readonly CanCapturePanel capturePanel;
    private CanCapture? capture;
    private bool supportsCapture;
    private bool disconnectAfterCapture;
    private bool captureResultShown;
    private string? firmwareIdentity;
    private long nextCaptureDisplay;

    private bool CaptureBusy => capture?.Busy == true;
    private string CapturePath => Path.Combine(Path.GetDirectoryName(sampleStore.DatabasePath)!, CanCaptureLog.FileName);
    private bool CanStartCapture => port is not null && isReader && supportsCapture && !CaptureBusy &&
        !polling && !telemetry.Pending && !dsgPanel.Pending && Environment.TickCount64 >= readyAt;

    private void ToggleCapture()
    {
        if (CaptureBusy)
            SendCaptureCommand(capture!.Stop(Environment.TickCount64));
        else if (CanStartCapture)
        {
            capture = new CanCapture(CapturePath, port!.PortName, firmwareIdentity);
            captureResultShown = false;
            SetActivity("正在启动 CAN 帧采集…");
        }
        nextCaptureDisplay = 0;
        UpdateActions();
    }

    private void Disconnect()
    {
        if (!CaptureBusy)
        {
            ClosePort();
            return;
        }
        disconnectAfterCapture = true;
        SendCaptureCommand(capture!.Stop(Environment.TickCount64));
        UpdateActions();
    }

    private bool ConsumeCaptureLine(string line)
    {
        if (!line.StartsWith("CAN1 ", StringComparison.Ordinal))
            return false;
        if (capture?.Accept(line, Environment.TickCount64) is string control)
        {
            AppendLog(control);
            nextCaptureDisplay = 0;
        }
        return true;
    }

    private void AdvanceCapture(long now)
    {
        if (capture is not null)
        {
            SendCaptureCommand(capture.Advance(now));
            if (!capture.Busy && !captureResultShown)
            {
                captureResultShown = true;
                nextCaptureDisplay = 0;
                var progress = capture.Progress;
                bool complete = progress.Complete(capture.End);
                string summary = $"CAN 帧采集结束，收到 {progress.Received:N0} 帧，已保存 {progress.Saved:N0} 帧";
                AppendLog($"{summary}  ·  {progress.Error ?? capture.End?.Reason}");
                SetActivity(summary, isError: !complete);
            }
        }
        if (disconnectAfterCapture && !CaptureBusy)
            ClosePort();

        if (now >= nextCaptureDisplay)
        {
            nextCaptureDisplay = now + 250;
            string idle = !isReader ? "连接板子后可开始采集。"
                : !supportsCapture ? "请升级板子固件后采集 CAN 帧。"
                : polling ? "暂停连续读取后可开始采集。"
                : "等待开始采集。";
            capturePanel.ShowStatus(capture, idle, now);
        }
    }

    private void SendCaptureCommand(string? command)
    {
        if (command is null || port is null)
            return;
        try
        {
            port.Write(command + "\n");
            if (!command.StartsWith("canping ", StringComparison.Ordinal))
                AppendLog("> " + command);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException)
        {
            ClosePort("CAN 帧采集串口中断  " + error.Message);
        }
    }
}
