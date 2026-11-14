# 示波器调试（触发/限值/按钮）测试 SOP

适用窗口：`Protocols/Instruments/ScopeDemoWindow.xaml(.cs)`（主窗口 **仪器 → 示波器** 打开）
通信方式：LAN TCP 裸连接 SCPI，窗口顶部可填 IP/端口，默认 `192.168.2.100:4000`。
数据来源：全部为仪器真实采集（`CURV?` 二进制块 + `WFMPre` 前导头），未连接时不出波形，没有模拟数据。

---

## 1. 测量表格默认显示

| 项 | 预期 |
|---|---|
| 默认可见行 | Frequency、Average、Max（最大值）、Peak-peak（峰峰值）、Δt（时间）共 5 行 |
| 默认隐藏行 | Period、Rise/Fall time、Pos/Neg width、Duty +/−、High、Low、Amplitude、Min、RMS、Cycle mean/RMS、Pos/Neg overshoot、Area、Cursor a/b time、1/Δt、Cursor a/b volt、ΔV 共 23 行 |
| 弹窗同步 | 点 `Measures ▾`，弹窗勾选状态与表格一致；勾选项即时显示，取消即时隐藏；本地统计不中断，仪器测量只轮询可见行，勾选后 1 秒内更新 |

代码位置：`DefaultHiddenMeasures` + `ApplyTypeFilter()`；XAML 弹窗 `IsChecked` 初始值与该清单逐项对应。

---

## 2. 限值触发：达到即停 + 命中点居中

### 2.1 验证步骤（连机）

1. 启动程序（`MultiSerialTool-hfl.exe`），**仪器 → 示波器** 打开窗口。
2. 顶部填 IP `192.168.2.100`、端口 `4000`，点 **连接**；状态栏应显示 `TEKTRONIX MDO3024 | … Sa/s | … 点 | LAN 已连接`。
3. 点 **开始**，波形随仪器采集实时刷新；仪器前面板改时基/垂直档位，界面“时间/电压”输入框 1 秒内同步回显。
4. 右侧勾选 **限值**，目标选 `Δt`，上限/下限设成当前 Δt 之外的区间。
5. 预期：
   - 若干帧内采集自动停止，波形冻结；
   - 状态文字：`硬件触发: 限值停止 · Δt=…`，LED 亮；
   - 仪器同步停止采集（`*ACQuire:STATE?` 返回 0）。
6. 复位：点 **开始** 重新武装；点 **清空** 清缓冲与统计。
7. ΔV 路径同理：目标选 `ΔV`，上下限设为比当前 ΔV 更窄的区间。

### 2.2 时间轴与触发点（可复算）

- 横轴时间不是本地生成的：`time[i] = (i − PT_OFF) × XINCR + XZERO`，每帧都从 `:WFMPre?` 回读；
- 绿色触发线位置 = `XZERO`（触发时刻），Auto 模式隐藏；
- 时间/格 = `(time[last] − time[0]) / 10`，并回显到“时间”输入框；
- 界面上改“时间/格”会下发 `:HORizontal:MAIN:SCALe`，由下一帧回读的新时基生效。

### 2.3 真机联动

PC 判定限值停止后自动下发 `:ACQuire:STATE OFF`，MDO3024 同步停止采集（等同前面板 Run/Stop）。

---

## 3. 触发模式与硬件状态回读

| 模式 | 行为 |
|---|---|
| Auto | 自由运行，显示最新 n 点；边沿命中只闪状态灯，不对齐 |
| Normal | 命中边沿（源通道 + 上升/下降 + 电平）后构建居中帧，持续重武装 |
| Single | 命中一次即停，构建居中帧后停止 |

- 触发源 = 右侧 Tab 选中的通道（CH1~CH4），每个通道独立配置边沿与电平；连接时以仪器当前配置回填 Tab 与电平。
- 连机后每秒 `:TRIGger:STATE?` 回读：`ARMED`→等待触发；`TRIG'D`→已触发（LED 亮绿）；`READY`/`SAVE`/`AUTO` 均有中文映射。
- 连接时同时回读 `:TRIGger:A:TYPe?`；若仪器停在脉冲/电流等非边沿类型，连接后自动下发 `EDGe` 并把类型改为边沿。
- 运行中切换 Auto/Normal/Single 会重新下发 `:ACQuire:STOPAfter` 并重新布防；单次采集结束后切回 Auto/Normal 会自动重新开始采集。
- `应用触发` 按钮可强制重新下发当前触发类型/源/斜率/电平/模式；仪器前面板被改成其他触发类型后用它切回边沿。

### SCPI 配置下发（设置变更时）

```
:TRIGger:A:TYPe EDGe
:TRIGger:A:EDGE:SOUrce CH1
:TRIGger:A:EDGE:SLOpe RISE
:TRIGger:A:LEVel:CH1 1.500
:TRIGger:A:MODe NORMal
```

- 触发命令必须带 `:A:` 节点；省略 `A`（`:TRIGger:EDGE:*`、`:TRIGger:MODe`）会被 MDO3024 v1.20 判为命令错误。
- 触发电平必须写到通道节点 `:TRIGger:A:LEVel:CH<x>`，`:TRIGger:A:EDGE:LEVel` 同样会报错。
- 每次下发后应用读取 `*ESR?`；非零时界面显示“触发设置被仪器拒绝（ESR=...）”，不会静默失败。
- 仪器会把电平量化到当前垂直档位可用的步进；应用每秒回读 `:TRIGger:A:LEVel:CHn?` 并回显实际值（实测请求 1.0 V 回读 0.8 V），电平输入框显示 4 位小数。
- 连接成功时先回读 `:TRIGger:A:EDGE:SOUrce?`、`:TRIGger:A:EDGE:SLOpe?`、`:TRIGger:A:LEVel:CH1..4?`、`:TRIGger:A:MODe?` 回填界面，不再用默认值覆盖前面板设置。
- 单次采集额外下发 `:ACQuire:STOPAfter SEQuence` 再 `:ACQuire:STATE ON` 布防；
  连续采集用 `:ACQuire:STOPAfter RUNSTop`。`:TRIGger:STATE?` 仍可正常回读。

### 波形取帧失败（Source waveform is not active）

- 现象：连接正常，点“开始”后状态栏出现 `取帧失败(n)：Unable to read data from the transport connection ...`，波形区只有光标线。
- 根因：`:WFMPre:XINCR?` 等前导头查询会校验当前 `:DATA:SOUrce`；若它指向仪器上未显示的通道，仪器记录 `2244,"Source waveform is not active"` / `420,"Query UNTERMINATED"` 且不应答，应用 5 s 后超时。
- 修复：`ReadFrameCore` 在查询前导头前先把 `:DATA:SOUrce` 切到第一个勾选通道。
- 连续/自动模式不再每帧 `:ACQuire:STATE OFF/ON`，直接 `CURV?`（MDO3024 v1.20 实测运行中可读，10000 点约 60 ms）；只有开始、停止、限值停止和单次采集才控制 `:ACQuire:STATE`。
- 通道勾选通过 `:SELect:CHn ON/OFF` 下发到仪器，连接时同步一次；实测不会打断采集。
- 同一台仪器不要同时连接多个 SCPI 客户端（如 ncat）。MDO3024 的回复可能串到另一个连接，表现为查询超时或整组数据错位。
- 限制：应用只能读取仪器上已显示的通道；勾选一个在仪器上关闭的通道仍会导致取帧失败。

### 真机验证记录（MDO3024 / 固件 v1.20 / 2026-11-12）
| 下发命令 | `*ESR?` | 结果 |
|---|---|---|
| `:TRIGger:TYPe EDGE` | 32 | 拒绝（命令错误） |
| `:TRIGger:EDGE:SOUrce CH1` | 32 | 拒绝 |
| `:TRIGger:EDGE:SLOpe RISe` | 32 | 拒绝 |
| `:TRIGger:EDGE:LEVel 0.5` | 32 | 拒绝 |
| `:TRIGger:MODe NORMal` | 32 | 拒绝 |
| `:TRIGger:A:TYPe EDGe` | 0 | 接受 |
| `:TRIGger:A:EDGE:SOUrce CH1` | 0 | 接受，回读 `CH1` |
| `:TRIGger:A:EDGE:SLOpe FALL` | 0 | 接受，回读 `FALL` |
| `:TRIGger:A:EDGE:LEVel 0.5` | 32 | 拒绝（缺少通道节点） |
| `:TRIGger:A:LEVel:CH1 0.5` | 0 | 接受，回读 `500.0000E-3` |
| `:TRIGger:A:MODe NORMal` | 0 | 接受，回读 `NORMAL` |
| `:ACQuire:STOPAfter SEQuence` / `RUNSTop` | 0 | 接受 |
| `:WFMPre:*?` + `CURV?` | 0 | 数据与 `WFMOutpre` 一致（`PT_OFF=0`） |

每轮验证后均恢复仪器测试前的触发设置，ESR=0。

---

### 测量表格行与 SCPI 测量类型对应

| 表格行 | `TYPe` 参数 | 取值命令 | 说明 |
|---|---|---|---|
| Frequency | `FREQuency` | `:MEASUrement:IMMed:VALue?` | 先 `:MEASUrement:IMMed:SOUrce1 CHn` |
| Period | `PERIod` | 同上 | |
| Rise time | `RISe` | 同上 | |
| Fall time | `FALL` | 同上 | |
| Pos width | `PWIdth` | 同上 | |
| Neg width | `NWIdth` | 同上 | |
| Duty + | `PDUty` | 同上 | |
| Duty - | `NDUty` | 同上 | |
| High | `HIGH` | 同上 | |
| Low | `LOW` | 同上 | |
| Amplitude | `AMPlitude` | 同上 | |
| Average | `MEAN` | 同上 | 与示波器屏幕测量框同源 |
| Max | `MAXimum` | 同上 | |
| Min | `MINImum` | 同上 | |
| Peak-peak | `PK2Pk` | 同上 | |
| RMS | `RMS` | 同上 | 仪器无值时回退本机 `sqrt(avg(v²))` |
| Cycle mean | `CMEan` | 同上 | |
| Cycle RMS | `CRMs` | 同上 | |
| Pos overshoot | `POVershoot` | 同上 | |
| Neg overshoot | `NOVershoot` | 同上 | |
| Area | `AREa` | 同上 | |
| 光标 Δt / ΔV / 频率等 | —（本机计算） | — | 光标位置与波形数组直接计算 |

- 优先显示仪器 MEAS 原始结果；Frequency/Average/Max/Min/Peak-peak/RMS 在仪器无值时回退本机计算，`Source` 列显示 `Local calc`；
- 只轮询表格中可见的仪器测量类型，隐藏行不占用 SCPI 查询；勾选后下一次轮询（约 1 秒）更新；
- 每种测量类型单独容错，某个类型不被固件支持时不影响其他类型；
- 仪器测量每秒轮询一次；连续两次失败自动停用，避免固件不支持时每轮等超时、拖慢波形刷新；
- 每次测量查询使用 1.5 s 短超时，不长时间占用 SCPI 连接。

## 4. 按钮逻辑落地清单（含触发停止共 9 个）

| 按钮 | 落地逻辑 |
|---|---|
| 连接 | Toggle：按顶部 IP/端口异步连接（8 s 超时）+ `*IDN?`/采样率/记录长度/时基查询；断开时释放连接并停止采集 |
| 开始 | 下发触发配置 → `:ACQuire:STOPAfter` → `*OPC?` → `:ACQuire:STATE ON`；启动 50 ms 采集定时器，自身禁用、暂停启用 |
| 暂停 | Toggle：`_paused` 切换，tick 短路、画面保持；继续后恢复状态文字 |
| 清空 | 清缓冲/统计行/触发与限值标志，重新初始化刻度 |
| 导出 | 保存对话框，导出全部 28 个测量行（含隐藏行）CSV，UTF-8 BOM |
| 截图 | 2× 渲染图表+数据表为 PNG 位图写入剪贴板 |
| 触发停止 | 立即停采、画面保持，仪器同步 `:ACQuire:STATE OFF` |
| 强制触发 | 立即下发 `:TRIGger FORCe`，无信号或单次等待超时时可手动促成一次采集 |
| 应用触发 | 强制重新下发当前触发类型/源/斜率/电平/模式；仪器前面板改成其他触发类型后用它切回边沿 |

---

## 5. SCPI 收发 HEX 样例（ASCII + `\n`=0x0A）

| 方向 | 文本 | HEX 字节 |
|---|---|---|
| TX | `*IDN?` | `2A 49 44 4E 3F 0A` |
| RX | `TEKTRONIX,MDO3014,C012345,FW:1.26` | `54 45 4B 54 52 4F 4E 49 58 2C 4D 44 4F 33 30 31 34 2C 43 30 31 32 33 34 35 2C 46 57 3A 31 2E 32 36 0A` |
| TX | `:TRIGger:STATE?` | `3A 54 52 49 47 67 65 72 3A 53 54 41 54 45 3F 0A` |
| RX | `ARMED` | `41 52 4D 45 44 0A` |
| RX | `TRIG'D` | `54 52 49 47 27 44 0A` |
| TX | `:ACQuire:STATE OFF` | `3A 41 43 51 75 69 72 65 3A 53 54 41 54 45 20 4F 46 46 0A` |

二进制波形查询 `CURVE?` 的响应为 IEEE488.2 定长块 `#<N><len><data>`，由 `ScpiClient.QueryBinary` 解析，块尾 `\n` 自动消费保持流对齐。

---

## 6. 回环/替代验证方法

- **无真机**：窗口不再生成模拟波形，未连接时只显示空坐标轴，只能做编译与界面冒烟验证。
- **假示波器（Python SCPI 模拟）**：在 `127.0.0.1:4000` 起监听，窗口 IP 填 `127.0.0.1`，按以下应答即可跑通全流程：
  - `*IDN?` → `TEKTRONIX,MDO3024,C026598,CF:91.1CT FV:v1.20`
  - `:HORIZONTAL:MAIN:SAMPLERATE?` → `1000000`；`:HORIZONTAL:RECORDLENGTH?` → `10000`；`:HORIZONTAL:MAIN:SCALE?` → `0.001`
  - `:CHn:SCALE?` → `1.0`；`:CHn:OFFSET?` → `0`
  - `:ACQuire:STATE?` → `1`（单次模式下触发后改回 `0`）
  - `:TRIGger:STATE?` → `ARMED`；`:TRIGger:A:EDGE:SOUrce?` → `CH1`；
    `:TRIGger:A:EDGE:SLOpe?` → `RISE`；`:TRIGger:A:MODe?` → `AUTO`；
    `:TRIGger:A:LEVel:CH1?` ~ `:TRIGger:A:LEVel:CH4?` → `0.0`
  - `:WFMPre:XINCR?` → `1.0E-6`；`:WFMPre:XZERO?` → `0`；`:WFMPre:PT_OFF?` → `5000`；`:WFMPre:YMULT?` → `4.0E-3`；`:WFMPre:YOFF?` → `127.0`
  - `CURV?` → `#410000` + 10000 字节波形 + `\n`
  - `:MEASUrement:IMMed:VALue?` → 任意数值；`*OPC?` → `1`；`*ESR?` → `0`
  - 其余查询返回一行数字即可；所有设置命令无应答。用它可在无仪器时验证连接、取帧、绘图与按钮流程。
- **编译验证**：`dotnet build MultiSerialTool.csproj` 应 0 错误。
- **冒烟验证**：启动 exe → 仪器 → 示波器，窗口无异常。
