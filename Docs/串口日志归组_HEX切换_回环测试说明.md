# 串口日志成帧归组、HEX 通篇切换与回环测试说明

> 适用程序：MultiSerialTool（WPF / .NET 9，`net9.0-windows`）
> 关联规则：`Assets/DeviceRules/SCx太阳能控制器.json`、`4G透传模组.json`
> 目的：固定“日志逐条显示、解析按次归组、HEX 通篇切换、切页静默补连”的口径，并给出可复算的收发样例与回环测试步骤。

---

## 1. 本次改造范围

| 项 | 改造前问题 | 改造后口径 |
|---|---|---|
| 日志显示 | 30ms 空闲间隔把到达字节合并成一条 LogEntry，多行 ASCII 整块塞进一条，文本内带 `\r\n`，TextBlock 对 `\r` 处理异常，残留空格/排版错位 | **日志层按成帧单元逐条显示，不跨单元合并**；收到一帧/一行显示一条 |
| 图表解析 | 假定“一条 Entry = 一条记录行”，误并/误拆导致字段串组 | **解析/图表层把近乎同时到达、同属一次上报的多行归为一组**，一组一条记录行 |
| HEX 显示 | RX 条目存了 Text+HexText，TX 条目只存 Text、HexText=null、无原始字节；HEX 模式下 TX 回退 ASCII；开关只对新流量生效 | RX/TX 条目都回填原始字节与 HexText，**历史 RX/TX 通篇跟随开关切换** |
| 切页连接 | — | 切回串口页时对所有在线但未连接/报错的端口**静默补连**；已连接/连接中保留，不主动断开 |

---

## 2. 链路分层（四层，职责分离）

| 层 | 类 | 职责 |
|---|---|---|
| 连接层 | `SerialConnection` | `DataReceived` 按块读入原始字节，封装 `SerialRawChunk`，封送到 UI 线程 |
| 成帧层 | `Protocols/Serial/Framing/StreamFramer.cs` | 把字节流拆成可独立显示的 RX 单元：二进制定长帧 / ASCII 行 / 空闲兜底 Raw 块 |
| 归组层 | `Protocols/Serial/Rules/ReportGrouper.cs` | 把同一次上报的若干单元聚合成一个 `ReportGroup`；二进制帧独立成组 |
| 解析/图表层 | `RuleExecutionEngine`、`Views/ChartView.xaml.cs` | 归组结果喂引擎解析回填与自动应答；图表按组取数，一组一条记录行 |

### 2.1 两个集合，不要混用

- `SerialDeviceInfo.LogEntries`：逐条日志单元列表，**日志面板绑定它**，一条单元一条 `LogEntry`。
- `SerialDeviceInfo.LogGroups`：归组历史列表，**图表绑定它**，一组一条 `ReportGroup`。

### 2.2 成帧规则（StreamFramer）

- 二进制帧头：`53 57 49 4F 54 54`（ASCII `SWIOTT`）；offset 6 的 Length = 整帧总字节数；最小帧 8 字节。
  - 长度非法：丢掉首字节重新同步，避免死锁在伪帧头。
  - 缓冲不足整帧：等待后续字节，不解析残帧。
  - 数据区即使含 `0x0A` / `0x0D` 也不会被当换行拆断。
- 非帧头数据：按 `\r\n` / `\n` / `\r` 切 ASCII 行，行内不含终止符。
- 兜底：既无帧头又无行尾，空闲超时把残留缓冲作为 Raw 块封出；超过 64KB 强制封帧。

### 2.3 归组规则（ReportGrouper）

- 锚点行（正则 `^\s*-{5,}\s*\d{4}-\d{2}-\d{2}`，如 `----- 2026-11-13 12:35:1 -----`）开启新组。
- 普通行追加到当前组；组内空闲超过阈值封组。
- 二进制帧独立成组：先结束当前 ASCII 组，再单独封出本帧，不与后续 ASCII 行合并。
- `ReportGroup` 聚合视图：`Units`（按序单元）、`CombinedText`（LF 拼接）、`CombinedHex`、`PrimaryBytes`（二进制取该帧；纯文本取首行字节）、`IsBinary`。

---

## 3. 关键配置（`AppConfig`）

| 配置键 | 默认值 | 含义 |
|---|---|---|
| `MessageIdleGapMs` | 30 ms | 成帧兜底阈值：间隔超过该值仍未闭合行/帧，把残留缓冲封成 Raw 块 |
| `MessageGroupGapMs` | 300 ms | 归组空闲阈值：组内最后一行之后超过该值仍无新行即封组 |

---

## 4. HEX 通篇切换规则

- `LogEntry` 同时保留 `Text`（ASCII 视图）、`HexText`（HEX 视图）、`Bytes`（原始字节）。
- RX：原始字节回填 `Bytes` / `HexText`。
- TX：两条写出链路在记录日志前先算字节与 HEX，回填到条目：
  - 引擎自动应答 / 初始查询：`OnEngineFrameReady`；
  - 工作区手动快捷发送：`OnTxSubmitted`。
  - HEX 命令先归一化 HEX 再取字节；文本载荷按 UTF-8 取字节转 HEX。
- 开关 `IsHexDisplay` 绑定到日志列表，`DataTrigger` 对集合内**全部历史行**重新求值；复制（Ctrl+C）与查找同样按当前视图取 HexText / Text；Test1 测试日志列表复用同一偏好通篇跟随。
- 条目无 `HexText`（无原始字节）时回退显示 ASCII。

> 线程收口：延时自动应答在后台线程到期回调 `FrameReady`，`OnEngineFrameReady` 顶部先经 `Dispatcher` 回到 UI 线程再写日志集合，避免与 UI 线程 RX `Add` 竞争造成穿插/重复。

### 4.1 右键复制与成功反馈

- 日志列表右键菜单：**复制选中**（多选选区）、**复制全部**（当前设备全部日志）；Ctrl+C 仍复制选中行。
- 复制取值与界面视图一致：HEX 模式取 `HexText`，否则取 `Text`，拼接为 `[时间] TX/RX 内容`。
- 复制成功后底部状态区临时显示「已复制 N 条」，1.5 秒后自动清除；剪贴板被占用等失败时显示「复制失败：原因」。
- 右键按下先选中命中条目，避免右键复制沿用旧选区。

---

## 5. 切页静默补连行为

- 触发入口：协议下拉切到串口页 → `SerialViewModel.OnPageActivated()`（`MainViewModel.SwitchPage` 串口分支调用）。
- 动作：
  1. `RefreshPorts()` 重新扫描端口、差量更新设备列表；
  2. 对所有在线但状态为 `Disconnected` / `Error` 的端口 `StartConnect(silent: true)`；
  3. `Connected` / `Connecting` 的设备保留不动，**不主动断开**。
- 静默语义：连接失败仅行内错误状态 + 日志，不弹窗打断；已登记自动重连的设备沿用既有重连键（USB 优先 VID/PID，否则端口名）。
- **不触发补连**的路径：
  - 应用启动：构造函数直接赋值串口 ViewModel，不经过 `SwitchPage`；
  - 切到 TCP / MQTT：不断开串口、保留连接；
  - 设置 / LOG 覆盖层返回：只清覆盖层，不经过 `SwitchPage`。

---

## 6. 收发样例与预期解析

### 6.1 二进制 HEX 样例（SCx）

- TX 电池保护参数查询：
  ```
  53 57 49 4F 54 54 13 4D 0B 00 00 00 00 00 00 00 00 58 AD
  ```
- RX 光伏数据（D8，小端）：
  ```
  53 57 49 4F 54 54 11 D8 09 B6 00 00 32 04 06 D3 A1
  ```
- 预期解析：
  - 光伏电压 = `0x00B6` × 0.1 = **18.2 V**
  - 充电电流 = `0x3200` = **12800 mA**
  - 充电功率 = `0x0604` × 0.1 = **154.0 W**
- 归组口径：该二进制帧独立成组，`PrimaryBytes` = 整帧，offset 规则按帧解析。

### 6.2 ASCII 多行上报样例（同一次上报，归为一组）

到达字节（行间 `\r\n`，块末 `\r\n`）：
```
----- 2026-11-13 12:35:1 -----
Solar voltage: 38.72, current: 3.22, power: 124.59, current offset: 0.254, pwm: 0.78
Battery voltage: 29.21, current: 4.29, capacity: 100, mode: 3
LED voltage: 47.03, current: 2.63, power: 123.61, energy: 0.00
```

预期表现：
- **日志面板**：拆成 3 条 RX 单元逐条显示（锚点行、Solar 行、Battery 行、LED 行各一条，共 4 条文本行单元），单行内不再带 `\r\n`。
- **归组器**：锚点行开启新组，Solar/Battery/LED 三行追加进同一组；空闲 300ms 内无新行即封组。
- **图表**：该组只建一条记录行，Solar/Battery/LED 各字段落在同一行，不串组；`CombinedText` 以 LF 拼接四行供 regex 匹配。

### 6.3 AT 问答样例（单行，各自独立成组）

```
Send AT: AT+SMSTATE?
Recv AT: AT+SMSTATE?+SMSTATE: 2OK
Send AT: AT+CGREG?
Recv AT: AT+CGREG?+CGREG: 0,5OK
Send AT: AT+CSQ
Recv AT: AT+CSQ+CSQ: 14,99OK
2026-11-13 12:34:52  Network RSSI = -86dBm
```

- 每条问答按行独立成组，不与下一段 ASCII 上报块合并；4G 规则走 ASCII / regex 解析。

---

## 7. 测试步骤

### 7.1 离线单元测试（无需串口，可直接在本机跑）

先构建主工程（测试 dll 引用主程序集输出，改主项目后必须先重建主项目）：

```powershell
cd G:\05_QT_Project\MultiSerialTool
dotnet build -v q --nologo
```

- 成帧/归组边界测试：
  ```powershell
  dotnet build Tests\EdgeCaseTests\EdgeCaseTests.csproj -v q --nologo
  & "Tests\EdgeCaseTests\bin\Debug\net9.0-windows\EdgeCaseTests.exe"
  ```
  覆盖：ASCII 切两行且不含终止符；二进制整帧取出；残留行兜底 Raw；空数据不产生单元；三行锚点归一组；二进制独立成组。
  当前结果：**49 通过 / 0 失败**。

- 规则引擎端到端：
  ```powershell
  dotnet build Tests\EngineTests\EngineTests.csproj -v q --nologo
  & "Tests\EngineTests\bin\Debug\net9.0-windows\EngineTests.exe"
  ```
  覆盖：字段回填、初始查询、自动应答、手动刷新、数值断言、ASCII/HEX 判别、规则迁移。
  当前结果：**16 通过 / 0 失败**。

### 7.2 VSPD 回环测试（`Tests/Program.cs`，LoopbackTest）

前置：用 VSPD（或物理交叉线）建立成对端口，两端均未被占用；默认 `COM9`（应用侧）/ `COM8`（对端模拟设备）。

```powershell
dotnet build Tests\LoopbackTest.csproj -v q --nologo
& "Tests\LoopbackTest\bin\Debug\net9.0-windows\LoopbackTest.exe" COM9 COM8
```

用例清单：

| 编号 | 场景 | 预期 |
|---|---|---|
| 1 | RX Modbus 二进制帧 | 整块收到，HEX/ASCII 占位正确 |
| 2 | RX 文本帧 `AT+CSQ\r\n` | 文本完整保留终止符 |
| 3 | TX 应用发送，对端接收 | 字节逐字节一致 |
| 4 | 中文/多字节 UTF-8 透传 | 对端解码与原文一致 |
| 5 | 多行 ASCII 上报块整块到达 | 原始字节完整透传（成帧拆行在 VM 内完成，原始层不损坏字节） |
| 6 | 二进制帧数据区含 `0x0A` / `0x0D` | 整帧透传，不被原始层拆断 |

> 连接层只负责原始字节透传；成帧、归组、图表落行在 VM 内完成，对应行为由 7.1 离线单测确定性覆盖，7.3 在实机上复核。

### 7.3 WPF 界面手工验证清单（实跑，本机尚未执行）

1. **并发顺序**：连续点 5 条快捷发送，配合 2 条延时自动应答；日志顺序与发送/应答顺序一致，无重复条目、无穿插、无跨线程异常。
2. **多行上报归组**：模拟设备连续推 6.2 的四行块；日志面板为 4 条独立单元，图表记录 1 行，Solar/Battery/LED 字段不串到相邻记录。
3. **HEX 通篇切换**：先收发若干 RX/TX，再来回切换 HEX 开关；历史 RX 与 TX 都应通篇在 HEX / ASCII 间切换，TX 不再回退 ASCII；Ctrl+C 复制与查找结果跟随当前视图。
4. **坏帧隔离**：发送帧头错误 / 长度不足 / 校验错误帧；坏帧进入日志，但字段值不被刷新、连接不断。
5. **断开重连残留**：断开前留有半行/半帧；重连后新会话不携带旧残帧，日志不出现半截乱码单元。
6. **切页补连**：
   - 串口 → TCP → 串口：TCP 侧不断开串口；回到串口时，Disconnected/Error 的在线端口静默补连，已连接端口保持连接；
   - 打开设置页 / LOG 页再返回工作区：不触发补连；
   - 启动首次进入串口页：不自动补连。
7. **清空日志**：清空当前设备日志后，图表记录行同步清空，不留旧曲线。
8. **右键复制**：右键日志行弹「复制选中 / 复制全部」；复制后底部显示「已复制 N 条」并在 1.5 秒后消失；HEX 模式复制 HEX 文本，ASCII 模式复制原文；多选 Ctrl+C 行为不变。

---

## 8. 已处理项与剩余限制

1. **测试日志页（Test1）已跟随 HEX**：测试日志列表复用同一 `WorkspaceState.IsHexDisplay` 偏好，HEX/ASCII 双块随开关通篇切换；无 HEX 内容时回退文本。原入队定格的 `DisplayText` 属性已移除。
2. **`ChartLogEntries` 遗留属性已删除**：图表统一走 `ChartLogGroups`，主工程无其它绑定引用。
3. **旧组包器已移除**：`MessageAssembler.cs` 已删除，主工程与测试工程无残留引用；成帧由 `StreamFramer` 替代。
4. **等宽字体已设置**：串口日志列表与 Test1 测试日志列表统一 `Consolas`，ASCII 列对齐空格不再因比例字体错位。
5. **运行时未实跑**：本次主工程编译 0 错误，离线单测全绿（EdgeCase 49/0、Engine 16/0）；WPF 界面的曲线回放、归组落行、切页重连、右键复制反馈等运行时行为以 7.3 手工清单复核为准。

---

## 9. 连接失败后设备进入烧录模式（排查与缓解）

### 9.1 现象与链路核对

- 现象：点连接失败后，设备复位进入烧录模式（原 COM 口消失/换口枚举）。
- 已排除：失败路径**不下发**业务帧或复位帧；初始查询仅在 `State=Connected` 后由 `RunInitialQueries()` 发出；波特率探测阶段只读不写。
- 配置侧：`DtrEnable`/`RtsEnable` 默认 `false`，本机 `config.json` 落盘也是 `false`。
- 可疑动作：自动探测会对同一端口按 8 个候选速率反复 `Open → 监听 600ms → Close/Dispose`；部分 CH340/CP210x/FTDI 驱动在 `Open` 瞬间先把控制线按默认态上线，若板子是自动下载电路（RTS→EN/RESET、DTR→BOOT0/IO0），复位采样可能进入下载模式，bootloader 换口枚举后旧口打开报“设备不存在”。

### 9.2 本次第一步缓解（已落地）

1. **直连优先**：探测候选把配置/上次成功波特率排第一，高置信度命中（评分 ≥85）即提前结束，减少在错误速率上反复 Open/Close。
2. **控制线显式复位**：真实端口 `Open` 之后、`Close` 之前，以及探测口 `Open` 之后，都按配置重新落一次 `DtrEnable/RtsEnable`；驱动默认高电平脉冲被覆盖回禁用态（用户在设置页主动开启的 RS485 方向脚除外）。
3. **静默重连退避**：连接失败/运行异常后 3 秒内，热插拔、切页补连、强制重连等**静默路径**不再自动 Open/Close；手动连接不受退避限制；连接成功后清除该端口退避记录。

### 9.3 未落地（第二步，待确认后再做）

- 设置页“自动波特率探测”总开关，可彻底关闭探测、固定用配置波特率直连。
- 按设备记忆上次成功波特率，下次直连该速率。

### 9.4 仍需对照实验定位根因

1. 工具完全关闭后只插适配器/板子，设备管理器里的口是否仍变（排除硬件/适配器枚举原因）。
2. 进烧录的具体表现：换 VID/PID 的口、枚举成 U 盘/RPI-RP2/DFU，还是日志里收到 bootloader 报文。
3. 用第三方助手固定波特率、不做自动探测直开同一端口，是否也复位。
4. 主控型号、串口芯片（CH340/CP210x/FTDI），DTR/RTS 是否接到 EN/BOOT。
