<!--
 * @Description: None
 * @Author: LILYGO_L
 * @Date: 2023-09-11 16:13:14
 * @LastEditTime: 2026-04-10 15:54:21
 * @License: GPL 3.0
-->
# VW ECU Reader

基于 LILYGO T-2CAN V1.0 的 OBD-II 读取器。[Windows 桌面版](#windows-串口仪表盘)通过 USB 串口查看发动机读数、记录趋势并识别 DQ381 变速箱。Mac、Windows 和手机也可以通过板载网页查看读数并下载 CSV。当前固件支持经典 CAN、`500 kbit/s` 和 11 位诊断 ID，车辆兼容性需实测。

离车台架连续六次通过。2020 年 9 月出厂的迈腾曾读到六项 ECU 数据，实车复测发现间歇性 CAN ACK 缺失。 [开发与排错记录](./docs/DEVELOPMENT.md) · [仓库维护说明](./AGENTS.md)

## 接车前注意事项

- **离车台架测试完成后，接车前拆除 B 路 120 Ω 贴片终端电阻。** 本项目用 LILYGO T-2CAN V1.0 普通版的 CAN B 接车，拆的是 [原理图](./project/T-2Can_V1.0.pdf) 上的 `RZ1`。拔掉 USB 电源和车端 OBD 后再拆焊，保留 A 路终端，空焊盘保持开路。
- **拆完先断电测量。** 拔掉 A/B 台架跳线和临时外接终端，用万用表测 `CANHB ↔ CANLB`。阻值应明显高于拆除前，可能显示 `OL`。若仍约 `120 Ω` 或接近 `0 Ω`，先排查再接车。详见 [买板后的调整](#买板后的调整)。
- **核对 OBD 接线和固件。** 只接 `6 → CANHB`、`14 → CANLB`、`5 → SGNDB`，`16` 号车端电源线单独绝缘，板子由 USB 供电。接车使用 `obd2_read` 固件，首次读取时停车挂 P 挡、拉手刹并打开点火。详见 [首次接车与读取](#首次接车与读取)。

## 硬件与购买

- [LILYGO 原产品 GitHub](https://github.com/Xinyuan-LilyGO/T-2Can) · [官方产品说明](https://wiki.lilygo.cc/products/industrial-series/t-2can/) · [LILYGO 商城](https://lilygo.cc/products/t-2can) · [LILYGO 官方淘宝店](https://shop140839766.taobao.com/)
- 本项目用 **T-2CAN V1.0 普通版**，选购时确认带 `MCP2515`，不要选 T-2CAN-FD。板上主控是 ESP32-S3，A 路使用 `MCP2515`，B 路使用 ESP32-S3 内置 `TWAI`。[V1.0 原理图](./project/T-2Can_V1.0.pdf) · [原板中文资料](./README_CN.md)
- 还需要 USB-C **数据线**、OBD-II 公头转散线、万用表。使用网页时装上随板或匹配的 `2.4 GHz` 外接天线；离开电脑后可以用 `5 V` USB 充电宝供电。

<img src="https://wiki.lilygo.cc/products/industrial-series/t-2can/index/image/t-2can-1.jpg" width="460" alt="LILYGO T-2CAN V1.0 板卡正面">

图片来源：[LILYGO 官方 Wiki](https://wiki.lilygo.cc/products/industrial-series/t-2can/)（[另一张实物图](https://wiki.lilygo.cc/products/industrial-series/t-2can/index/image/t-2can-2.jpg)）。淘宝链接为官方店铺入口，未核实独立商品页或库存。

## 买板后的调整

1. **先进行台架测试，不接车。** 保留出厂的两路终端电阻，断电后用三根短线连接 `CANHA ↔ CANHB`、`CANLA ↔ CANLB`、`SGNDA ↔ SGNDB`。刷入 `obd2_bench`，打开 `115200` 串口并按 `t`。看到 `OBD-II BENCH PASS` 即通过；`800 rpm` 和 `90 C` 是模拟值。
2. **为接车处理 B 路终端。** 拔掉 USB、车端 OBD 和三根台架跳线。由熟悉贴片焊接的人按[原理图](./project/T-2Can_V1.0.pdf)核对并移除 **B 路 `RZ1`（120 Ω）**，不要短接空焊盘，也不拆 A 路的终端。不要凭照片左右或电阻上 `121` 的标记判断是哪一路。[外部诊断设备的终端要求](https://law.resource.org/pub/us/cfr/ibr/inc/iso/iso.15765-4.2005.pdf#page=25)
3. **断电复测。** A 路 `CANHA ↔ CANLA` 应仍约 `120 Ω`；B 路 `CANHB ↔ CANLB` 应明显高于拆除前，万用表可能显示 `OL`，不是 `0 Ω`。若 B 路仍约 `120 Ω` 或接近 `0 Ω`，不要接车。电阻复测不能证明 CAN 收发正常；建议在台架复测拆焊后的 B 路，若用了临时外接终端，接车前也要拔掉。
4. **刷回 `obd2_read`，然后接车。** `original_test` 和 `obd2_bench` 都仅用于台架。刷写时板子只连电脑 USB，OBD 必须与车辆断开。

## 编译与刷写

安装 VS Code 的 [PlatformIO IDE](https://docs.platformio.org/en/latest/integration/ide/vscode.html) 扩展。在工程根目录打开 PlatformIO 终端，`pio device list` 可查串口。以下以 Windows 的 `COM3` 为例；Mac 请换成实际的 `/dev/cu.usbmodem...` 设备。刷写前关闭已占用端口的监视器。

```sh
# 台架固件，仅在 A/B 互测时使用
pio run -c platformio-obd2-bench.ini -j 1 -t upload --upload-port COM3
pio device monitor --port COM3 --baud 115200
```

实车版默认不启动热点。需要网页时，在本地 `examples/obd2_read/wifi.local.h` 定义自己的 8 到 63 字符密码；该文件已被 Git 忽略，不要提交。**不给密码也可以串口使用。**

```cpp
#pragma once
#define OBD2_AP_PASSWORD "replace-with-your-own-password"
```

```sh
# 实车只读版，可先执行第一条命令确认能够编译
pio run -c platformio-obd2-read.ini -j 1
pio run -c platformio-obd2-read.ini -j 1 -t upload --upload-port COM3
pio device monitor --port COM3 --baud 115200 --echo
```

`OBD-II READ ONLY | CAN B 500 kbit/s | idle` 表示实车版已启动。实车版、诊断版、台架版和单板自检版每次通过 PlatformIO 构建或刷写时，都会把带 UTC 构建时间的版本号写进固件，并在启动时通过串口显示版本号、构建环境和 Git 提交号。Git 有本地改动时显示 `+dirty`。实车版和诊断版可在 `115200` 串口输入 `version` 并回车查询，台架版和单板自检版按 `v` 查询。版本查询不会触发 CAN 请求。用旧的 `.bin` 恢复时，恢复的是该文件原有的版本。

## Windows 串口仪表盘

桌面版使用 Windows 和 .NET 10 SDK。通过 USB 串口连接 T-2CAN，适合实车读数、趋势观察和排查通信问题。板载网页仍可用于手机或 Mac。

| 功能 | 使用方式 |
| --- | --- |
| 发动机读数 | 「发动机」页显示转速、冷却液温度、车速、计算负荷、节气门开度和进气温度。发动机未启动时，`0 rpm` 是有效读数，`—` 表示该项没有数据。 |
| 单次和连续读取 | 「读取一次」发起一轮查询。开启连续读取后，每轮完成约 3 秒后再读。遇到发送失败、回包超时等错误时，程序会暂停连续读取。 |
| 趋势与独立窗口 | 六项读数各有趋势图。点击指标卡片上的「独立窗口」，可单独查看并调整窗口大小。 |
| 本地保存 | 每轮发动机读数和失败原因会自动写入当前选中的 SQLite 或 Memurai。 |
| 串口日志 | 右侧显示带时间的串口命令和响应。「保存日志」导出为 `.log` 或 `.txt`，也会包含变速箱识别的串口响应。 |
| 主题 | 可切换「经典」「F1」「WRC」，下次启动时恢复上次选择。 |

### 启动与连接

1. 使用 [编译与刷写](#编译与刷写) 中的 `obd2_read` 实车只读固件。从仓库根目录启动桌面程序。

   ```powershell
   dotnet run --project desktop/VwEcuReader.csproj
   ```

2. 实车测试时，按 [首次接车与读取](#首次接车与读取) 核对接线。用 USB-C 数据线连接板子和电脑，给板子供电后再打开点火。
3. 关闭占用同一串口的 PlatformIO 监视器，在桌面版选择实际端口（例如 `COM3`）并点「连接」。程序使用 `115200` 波特率。显示「只读固件已就绪」后，先点「读取一次」检查结果。

仪表盘兼容原版只读固件和带版本号的只读、诊断固件。变速箱识别需要支持 `dsg-id` 的版本。

### 识别 DQ381 变速箱

停车挂 P 挡、拉手刹并打开点火。连接板子、暂停连续读取，在「变速箱」页点「读取 TCU 信息」。

| 识别项 | UDS 数据标识符 |
| --- | --- |
| 软件件号 | `F187` |
| 软件版本 | `F189` |
| ODX 数据集 | `F19E` |

固件在 CAN B 上向 `0x7E1` 发出只读查询，等待 `0x7E9` 响应，这组地址仍需在目标车辆上确认。需要排查时，用「保存日志」导出实际响应。当前页面提供 TCU 身份字段，油温、实际挡位和 K1/K2 滑差仍待核对对应的软件版本和测量定义。

### 采集 CAN 原始帧

使用支持 `can-capture-v1` 的固件连接 Reader，暂停连续读取，在「CAN 帧」页点「开始采集」。CAN B 以 500 kbit/s 只听模式接收 OBD 口上可见的标准帧、扩展帧和远程帧。采集期间暂停发动机、TCU 和板载网页查询。

此页显示每秒收到的帧数、本次接收与保存总数、板端丢帧、FIFO 溢出、电脑缺帧、总线错误，以及最近 20 帧。停止时 Reader 会等板端发送剩余帧，然后写完数据库。串口中断时保留已经入库的数据，界面会提示采集中断。

每次采集单独编号。Reader 将成功解析的原始帧写入 `%LOCALAPPDATA%\VwEcuReader\can-frames.sqlite3` 的 `can_frames` 表。页面底部可复制路径。每行包含采集编号、帧序号、板端从上电开始计的微秒时间、CAN ID、帧格式、DLC 和原始数据。采集结果和丢帧计数保存在 `can_capture_sessions` 表。

### 数据保存

- **SQLite** 是默认选项。首次保存时创建 `%LOCALAPPDATA%\VwEcuReader\readings.sqlite3`，并按采样时间建立索引。
- **Memurai** 连接本机 `127.0.0.1:6379`，读数写入 `vw-ecu-reader:readings` Stream。重启后可恢复的范围取决于 Memurai 的快照或 AOF 设置。
- 切换「存储」后，新完成的读取写入所选后端。脱离电脑使用时，可通过板载网页下载 CSV。

## 首次接车与读取

布线时拔掉 USB，OBD 公头也不要插车。按插头针脚编号用万用表核线，不按导线颜色猜测。A 路和两位直流供电端子都留空，不接台架跳线或额外终端电阻。[J1962 针脚参考](https://www.ni.com/en/support/documentation/supplemental/18/specifications-for-the-can-obd-ii-cable.html#sae-j1962-connector-wiring)

| OBD-II 针脚 | T-2CAN V1.0 |
| --- | --- |
| `6` / CAN High | `CANHB` |
| `14` / CAN Low | `CANLB` |
| `5` / 信号地 | `SGNDB` |
| `16` / 车端电源正极 | **不接，单独绝缘** |

1. 拧紧端子，把所有未用散线分别绝缘。车辆停稳、挂 P 挡并拉手刹，点火关闭时插入 OBD。然后用 USB 给板子供电，把车辆点火切到 `ON`，先不启动发动机。[诊断工具接入顺序参考](https://www.obdlink.com/windows_qsg_ex/)
2. 配置了热点密码时，Mac 或手机连接 `B85380`，在浏览器打开 [http://192.168.4.1/](http://192.168.4.1/)，点一次**读取一次**；也可以在 USB 串口输入 `read` 并回车。不需要安装 App。发动机未启动时 `0 rpm` 是有效读数，`—` 则表示没有该项数据。没有结果时先检查点火状态和接线，不要连续重试。
3. 查询结果追加到板载 `obd2-v2.csv`，可从网页下载；旧版 `obd2.csv` 保留。时间字段是启动编号和开机后的毫秒数，不是实时时钟。曲线随新的手动读数更新。

**只读查询会发送 CAN 请求。** 待机时 CAN B 控制器停用，读取时进入正常模式发送查询，结束后停用。板载网页由用户手动触发读取。桌面端在用户开启「连续读取」后按轮查询。连接热点、打开或刷新网页、下载 CSV 均不会触发 CAN 查询。固件仅执行读取操作。[ESP32-S3 TWAI 模式说明](https://docs.espressif.com/projects/esp-idf/en/v4.4.4/esp32s3/api-reference/peripherals/twai.html#operating-modes)
