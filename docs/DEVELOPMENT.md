# VW ECU Reader 开发与排错记录

## 2026-11-22 迈腾 OBD-II 读数间歇中断

测试车辆是一汽-大众迈腾，2020 年 9 月出厂。设备是 LILYGO T-2CAN V1.0，CAN B 使用 ESP32-S3 的 TWAI，CAN A 使用 MCP2515。实车复测定位到发送帧缺少 ACK，离车台架连续通过六次，板子已恢复原固件。实车缺 ACK 的具体原因待对照。

### 接车前核对 USB、线束和终端

- Mac 最初枚举出 `/dev/cu.usbmodem2101`。在 115200 波特率下捕获到 `OBD-II READ ONLY` 和 `B85380` 热点启动日志。不同 DTR、RTS 设置下试发 `?` 均未收到预期的命令提示。USB 命令收发随后用实际的 `read` 验证。
- 首测用 USB 供电。车端 OBD 6 脚接 `CANHB`，14 脚接 `CANLB`，5 脚接 `SGNDB`，16 脚车载电源留空并绝缘。原理图上的 B 路 `RZ1` 为跨接 H、L 的 120 Ω。开始复测后，用户确认 `RZ1` 已拆，B 路 H、L 间读数为 `OL`，并确认线束接法。A 路 `RZ2` 在后续台架测试时确认仍在。 [OBD 针脚定义](https://www.ni.com/en/support/documentation/supplemental/18/specifications-for-the-can-obd-ii-cable.html) · [LILYGO V1.0 原理图](../project/T-2Can_V1.0.pdf) · [官方端子图](https://wiki.lilygo.cc/products/industrial-series/t-2can/index/image/t-2can-pin.jpg)
- 原固件使用 CAN B、500 kbit/s、11 位 CAN ID。每次手动读取先向 `0x7DF` 发送 Mode `01` 的转速请求 PID `0C`，等待 `0x7E8` 回包。请求以单次发送模式发出，每轮读完后有两秒间隔。车型核对时参考了大众同期 Passat 的 500 kbit/s 诊断 CAN 资料。这辆迈腾随后用当前配置读到了有效数据。 [固件入口](../examples/obd2_read/main.cpp) · [大众 SSP 596](https://esperformance.net/ssp/vw/SSP_596_DE.pdf#page=6)

### 原固件实车复测

| 轮次 | 测试动作 | 结果 |
| --- | --- | --- |
| 首次接车 | 用户从仪表盘试读 | 无数据。当时电脑未枚举板子的 USB 串口，无法捕获失败日志。 |
| USB 接回后 | 通过串口发送一次 `read` | `PID 0C: CAN transmission failed; no retry`。失败发生在发送转速请求时。 |
| 确认车型和 B 路终端后 | 保留 USB 供电，重新接车并试读一次 | 仍报同一发送错误。 |
| 更换电脑 USB-C 口后 | 串口变为 `/dev/cu.usbmodem1101`，试读一次 | 命令收发正常，CAN 发送仍失败。 |
| 偶发成功 | 用户报告读到一次，随后又无数据 | 当时暂停了单板自检构建。该次成功读数未从串口采集。 |
| 三次间隔试读 | 用户确认车辆始终点火 ON，发动机未启动 | 第一次读齐六项，第二、三次均在 PID `0C` 发送时失败。 |

最后一轮的第一次实读为转速 0 rpm、水温 70°C、车速 0 km/h、发动机负荷 0.0%、节气门开度 16.9%、进气温度 39°C。仪表盘用最新样本刷新，新的失败样本缺少数值时显示 `—`。CSV 按次记录成功和失败的试读。 [仪表盘刷新逻辑](../examples/obd2_read/dashboard.h) · [CSV 写入逻辑](../examples/obd2_read/main.cpp)

### TWAI 告警、备份与首次诊断

1. 在发送失败和 Bus-Off 告警之外，启用发送成功、仲裁丢失和总线错误告警，并记录 TWAI 的发送、接收错误计数。编译通过后，在 OBD 已拔下的状态下进行临时刷写。首次读取备份时，VS Code 的 PlatformIO 串口监视器占用同一端口，串口读取中断。退出监视器后重读成功。
2. 从实际分区表和 OTA 数据确认活动应用为 `app0`，起始地址为 `0x10000`。先备份并校验临时刷写涉及的 430,080 字节，再写入应用镜像。车载 CSV 所在的 `spiffs` 数据分区在 `0xC90000`。当时本地未配置 `wifi.local.h`，临时版通过 USB 取日志。
3. 再接车，间隔试读三次。前两次在 PID `0C` 发送时失败，报告 `tx_ok=0`、`arbitration_lost=0`、`bus_error=1`、`bus_off=0`，发送错误计数为 8，接收错误计数为 0。第三次读齐六项，得到转速 0 rpm、水温 95°C、车速 0 km/h、发动机负荷 0.0%、节气门开度 16.9%、进气温度 15°C。 [TWAI 告警定义](https://docs.espressif.com/projects/esp-idf/en/v4.4/esp32s3/api-reference/peripherals/twai.html)

### 离车单板自检与错误位段采集

- 拔下 OBD 后运行单板自检。CAN B 的 TX 和 RX 高低电平回读各通过 200 次。B 路未装终端、两路尚未用跳线相连时，500 kbit/s 自收发测试输出 `tx=6 txerr=14 rx=0`，其中 `tx` 是 `twai_transmit` 返回成功的次数。随后用带终端的 A/B 台架复核实际收发。 [单板自检源码](../debug/examples/self_test/self_test.ino) · [LILYGO 参考板记录](https://github.com/Xinyuan-LilyGO/T-2Can/issues/13)
- 第一版位段诊断在主循环收到告警后读取 ECC。接车三次均发送失败，ECC 均读到 `0x00`。核对 SDK 后发现 `twai_hal_get_events` 在中断中先读取该寄存器。改为在诊断构建中包装 HAL 入口，在驱动读取前保存 ECC。离车用 `read` 验证抓到了有效值 `0xA2`，对应标识符前段的接收位填充错误。 [诊断构建配置](../platformio-obd2-read.ini) · [乐鑫 ESP32-S3 技术手册 §31.5.8](https://documentation.espressif.com/esp32-s3_technical_reference_manual_en.pdf)

### 实车 ACK 验证

这轮实车测试的 USB 串口枚举为 `/dev/cu.usbmodem101`，随后离车台架和固件恢复也使用了该端口。

| 工况 | 试读次数 | 实际结果 |
| --- | ---: | --- |
| 点火 ON，发动机未启动 | 3 | 3 次均在首个转速请求报 `0xD9`，发送错误计数为 8。 |
| 原地怠速 | 3 | 3 次均报相同的 `0xD9`。 |

`0xD9` 对应发送方向，出错位段 `0x19` 为 ACK 槽。板子在 CAN 帧的确认位未检测到应答，故障出现在等待 ECU 的 PID 回包之前。两组实车测试的仲裁丢失和 Bus-Off 均为 0。 [乐鑫 ESP32-S3 技术手册 §31.3.3、§31.5.8](https://documentation.espressif.com/esp32-s3_technical_reference_manual_en.pdf)

### 有终端的离车台架与固件恢复

1. 车端 OBD 拔下，板子断电后用三根短线连接 `CANHA ↔ CANHB`、`CANLA ↔ CANLB`、`SGNDA ↔ SGNDB`。两个 `DGND` 端子留空。A 路保留的 120 Ω `RZ2` 通过跳线接到两路共用的 CAN 总线上，B 路 `RZ1` 已拆。随后接回 USB。 [LILYGO 官方 V1.0 原理图](https://github.com/Xinyuan-LilyGO/T-2Can/blob/main/project/T-2Can_V1.0.pdf)
2. 刷入 `obd2_bench`，用 CAN A 模拟 ECU。通过 USB 串口连续触发六次 `t`，CAN B 在 500 kbit/s 下每次均读到模拟转速 **800.00 rpm** 和水温 **90°C**，六次都输出 `OBD-II BENCH PASS`。 [台架源码](../examples/obd2_bench/main.cpp)
3. 将备份的 430,080 字节写回 `app0` 原地址，刷写工具报告数据哈希校验通过。复位后从 USB 重新捕获到 `OBD-II READ ONLY` 和 `Wi-Fi AP: B85380 | http://192.168.4.1/`。诊断流程只刷写应用分区，试读记录照常追加到 CSV。

### 当前待验证

台架已验证 CAN B 在短线和已知应答节点下能完成 OBD-II 收发。实车缺 ACK 的具体原因仍待定位。同一迈腾的 OBD 口还没有用已知正常的读码器做对照。下次接车前拆掉 A/B 的三根台架跳线，实车只接 CAN B。

### 复现构建

```sh
pio run -c platformio-obd2-read.ini -e obd2_read
pio run -c platformio-obd2-read.ini -e obd2_read_diag
PLATFORMIO_BUILD_DIR=.pio/bench-build pio run -c platformio-obd2-bench.ini -e obd2_bench
PLATFORMIO_BUILD_DIR=.pio/self-test-build PLATFORMIO_SRC_DIR=debug/examples/self_test \
  pio run -c platformio.ini -e self_test
```

普通只读版和带 ECC 的诊断版都已编译通过。刷写前拔下车端 OBD。台架固件使用 `t` 触发，单板自检固件上电即运行，两者都用于离车测试。

## 后续排错记录

后续在本页按日期追加。每轮保留固件版本、车辆和硬件状态、测试动作、串口结果、改动和恢复情况，以及还需要验证的问题。
