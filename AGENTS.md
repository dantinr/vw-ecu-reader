# AGENTS.md

## 项目结构

- `desktop/` 是 Windows WinForms 桌面端，目标框架为 `net10.0-windows`。`Dashboard.cs` 管理串口和采样状态，`Telemetry.cs` 解析发动机读数，`DsgPanel.cs` 显示 TCU 识别结果，`SampleStore.cs` 写入 SQLite 或 Memurai。
- `examples/obd2_read/` 是 LILYGO T-2CAN V1.0 普通版的实车固件，使用 ESP32-S3 的 TWAI（CAN B）。`dsg_response.h` 负责重组 TCU 的 ISO-TP 回包。
- `examples/obd2_bench/` 用于离车 A/B 互测。实车操作见 [README](README.md)，带日期的验证和排错结果记在 [开发与排错记录](docs/DEVELOPMENT.md)。

## 构建与验证

从仓库根目录运行。桌面端构建和运行使用 Windows 与 .NET 10 SDK。

```powershell
dotnet build desktop/VwEcuReader.csproj
dotnet run --project desktop/VwEcuReader.csproj
```

按改动范围编译对应的 PlatformIO 环境。

```sh
pio run -c platformio-obd2-read.ini -e obd2_read -j 1
pio run -c platformio-obd2-read.ini -e obd2_read_diag -j 1
pio run -c platformio-obd2-bench.ini -e obd2_bench -j 1
```

修改串口或解析时，用离车台架或模拟串口输出核对正常响应、分段输入和超时。涉及 TCU ISO-TP 时，覆盖单帧、多帧和拒绝响应。记录验证用的固件环境，标清结果来自模拟、台架还是实车。

## 串口与数据兼容

- USB 串口速率为 `115200`。`version` 查询固件身份和能力；`read` 发起一轮发动机读取；`dsg-id` 发起 DQ381 身份读取。
- `READER CAPABILITIES dsg-id` 用于识别支持 TCU 查询的固件。更改串口命令、能力或响应格式时，同步检查固件与桌面端解析，保留旧只读固件的发动机读取兼容性。
- 保留已保存的 SQLite 和板载 CSV 数据。新增指标时，使用明确的字段名和单位，并区分有效的零值与缺失值。
- TCU 身份查询使用 `F187`、`F189`、`F19E`。新增变速箱实时测量项前，核对目标 TCU 的软件版本、ODX 数据集、数据标识符和换算单位。

## 硬件与本地配置

- 实车联调使用实车只读固件和 CAN B。刷写、台架互测和 `original_test` 均在与车辆 OBD 断开的情况下进行。接车和终端电阻按 [README 的接线说明](README.md#首次接车与读取) 核对。
- 保持只读查询行为。连接串口、查询版本或打开板载网页时，CAN 保持空闲。连续读取需要用户显式开启，通信错误后暂停。
- 热点密码放在本地的 `examples/obd2_read/wifi.local.h`。提交日志或测试样本前移除 VIN、密码和其它车辆或用户标识。
