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

## 2026-11-23 CAN B 间歇读取复测

实车点火 ON、发动机未启动时，Windows Reader 通过 `COM3 / 115200` 完成握手。首条转速请求发送失败，读到 0/6 项，`tx_error=8`、`rx_error=0`、`bus_off=0`。此前同样工况下曾读齐六项。

诊断版 `obd2_read_diag` 新增 USB 串口命令 `listen`。它在 500 kbit/s 下以 TWAI 只听模式统计三秒内接收到的帧、接收丢帧和总线错误，随后返回待机。监听期间控制器不发送请求或 ACK。收到帧可以确认这一时段的接收链路有效。收到零帧时，诊断网关在该时段没有主动发帧也是可能原因。

普通版和诊断版均编译通过。拔掉车端 OBD 后，先备份并校验 `app0` 内将被覆盖的 786,432 字节，再刷入诊断版 `v0.1.0-20261123T090010.634760Z`。离车通过串口执行两次 `listen`，每次三秒后均返回 0 帧、0 丢帧和 0 总线错误，之后仍能正常查询版本。

接回 OBD 并打开点火后，同一诊断版三秒内收到 9,316 帧，接收队列丢帧、硬件 FIFO 溢出和总线错误计数均为 0。随后通过串口触发一次 `read`，首条 PID `0C` 请求发送失败，ECC 为 `0xD9`（ACK 槽），`tx_error=8`、`rx_error=0`、`bus_off=0`。实车接收在本次测试中正常，发送方向的 ACK 缺失原因仍待定位。

重新接好 OBD 后，Reader 于 17:25:08 保存了完整样本。发动机转速 0 rpm，冷却液 35°C，车速 0 km/h，发动机负荷 0.0%，节气门开度 16.9%，进气温度 22°C。之后数次发动机读取均在首条请求失败，变速箱身份查询也在 `F187` 报 `CAN_ERROR`。两类查询的串口日志均显示 ECC `0xD9`。

故障期间只听三秒仍收到 9,374 帧，丢帧和总线错误均为 0。保持车端接线和点火状态，通过 USB 重启 ESP32 并核对启动提示，首轮仍报 `0xD9`。随后保持板子和 Reader 连接，车端点火关闭再切到 ON，17:42:43 单次读取仍报 `0xD9`，收到 0/6 项。

Reader 重连测试中，SQLite 记录了 17:53:30、17:54:52、17:56:11 三个完整样本，期间 17:54:41 有一次首条转速请求发送失败。用户报告手动刷新过端口，Reader 日志记录了串口开关及读请求。三次成功读取都在打开 COM3 后约 2 至 4 秒发起。

随后用与 Reader 相同的 .NET 串口设置，在打开 COM3 后约三秒各触发一次 `read`。第一组枚举一次端口，第二组额外枚举一次以模拟手动刷新，两组均报 `0xD9`。Reader 的断开操作本身就会重新枚举端口。这次对照中，额外刷新未恢复 CAN 应答。

SQLite 中发动机运转时的完整样本包括 18:17:41、18:20:51 和 18:22:54，车速均为 0 km/h。这段记录中的失败仍发生在首条转速请求。

为对照 CAN 位时序，诊断版 `v0.1.0-20261123T110852.891297Z` 增加 `rpm80` 和 `rpm85`。两条命令均在 500 kbit/s 下发送一次 PID `0C`，分别使用 80% 和 85% 的采样点，结束后恢复待机。普通版和诊断版均编译通过。只连 USB 刷入诊断版后，两个命令都输出了预期采样点并报告发送失败，随后 `listen` 正常结束，串口仍能查询版本。上一版诊断镜像已备份。

19:35 接车后，按停车、点火 ON 工况，通过 COM3 先执行一次 `listen`。CAN B 在三秒内收到 9,315 帧，接收丢帧、FIFO 溢出和总线错误计数均为 0。随后用同一诊断版以 `80、85、85、80` 的顺序执行两组单次转速查询，每次间隔约 2.5 秒。80% 和 85% 各 4 次均报 `0xD9`，没有转速回包。各次发送错误计数均为 8，未触发 Bus-Off。本轮将采样点后移至 85% 未改善 ACK 缺失。

## 2026-11-23 Reader 原始帧验证

普通版和诊断版均编译通过。模拟串口分段输入以约 3,105 帧/秒写入 31,050 帧，SQLite 行数与发送数一致。测试覆盖标准帧、扩展帧、远程帧、传输缺口、断线和数据库打不开的情况。

诊断版 `v0.1.0-20261123T115342.868558Z` 在只连 USB 时刷入 `app0`，刷写前核对并备份了原镜像和分区信息。离车测试中，板子能开始和停止采集，停止接收电脑保活后也能退出监听。

20:08 在迈腾点火 ON、发动机未启动时，从 Reader 对 CAN B 只听采集约 29 秒。板端从队列取出 89,429 帧，另记录 922 帧未进入接收队列、0 次 FIFO 溢出和 0 次总线错误。Reader 与 SQLite 均核对为 88,826 帧，传输序号缺 603 帧，有 12 个异常数据包，采集结果标记为不完整。

已保存的帧全为 `0x17F00010` 扩展数据帧，DLC 为 8，数据字节没有变化。未见 `0x7E8` 或 `0x7E9` 诊断回包，当前记录无法解码发动机或变速箱测量值。公开的途观实测也观察到同一 ID，并将它识别为大众网关广播。 [途观实测记录](https://github.com/unfrgivn/spia/pull/19)

## 2026-11-26 OBD 公母头和线束台架测试

离车使用新 OBD 母座代替车端插座。母座的 6、14、5 脚接 CAN A 的 `CANHA`、`CANLA`、`SGNDA`，原车用 OBD 公头仍接 CAN B。插合公母头后，CAN B 的请求和 CAN A 的模拟回包均经过这套插头和线束。

先校验并备份 `app0` 中将被覆盖的 282,624 字节，再临时运行 `obd2_bench` 版本 `v0.1.0-20261126T115629.761267Z`。通过 `COM3 / 115200` 连续触发六次 `t`，每次均读到模拟转速 800.00 rpm、水温 90°C，并报告 `OBD-II BENCH PASS`。台架结果验证了本次插合状态下的双向通信，迈腾上间歇缺 ACK 的原因仍待定位。

测试后写回原应用并通过哈希校验，从 `COM3` 查询确认已恢复诊断固件 `v0.1.0-20261123T115342.868558Z`。两次刷写范围均为 `app0` 的应用区域。

同一套公母头和线束随后用桌面 Reader 复测。在临时诊断构建 `v0.1.0-20261126T123439.812158Z` 中，CAN A 应答发动机转速和水温请求，CAN B 沿用实车版的查询和解析逻辑。Reader 经 `COM3` 点「读取一次」后显示 800 rpm、90°C，临时 SQLite 核对为一条有效记录，其余指标为空。测试结束后写回并通过串口确认原诊断固件。

## 后续排错记录

后续在本页按日期追加。每轮保留固件版本、车辆和硬件状态、测试动作、串口结果、改动和恢复情况，以及还需要验证的问题。
