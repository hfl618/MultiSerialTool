# MultiSerialTool 代码框架与逻辑说明

> 本文档说明项目的目录结构、分层架构、运行流程与各模块的实现逻辑。
> 技术栈：C# / .NET 9 / WPF / MVVM / DevExpress WPF 24.2（Ribbon、LayoutControl、Charts、TabControl）
> 目标框架：`net9.0-windows`，输出为单文件可执行程序 `MultiSerialTool-<版本>-hfl.exe`。

---

## 1. 项目概览

一款面向仪器调试场景的多协议串口工具，当前以**串口（Serial）**为主，TCP / MQTT 为预留协议页。

核心能力：

- 本机串口枚举（WMI 补全友好名、厂商、VID/PID）
- 自动波特率探测（按数据质量评分选择真实波特率）
- 热插拔监听 + 500ms 去抖 + 自动重连（USB 设备按 VID/PID 匹配）
- 每台设备独立收发日志、独立发送草稿与历史
- 日志 HEX / ASCII 双视图、多选复制、Ctrl+F 查找/过滤
- 设备解析规则（外部 JSON 文件，可替换）、图表实时曲线绘制
- 全局调试日志（内存 + 按天落盘文件）
- 中英文运行时切换（源字符串即键）

---

## 2. 目录结构

```
MultiSerialTool/
├─ App.xaml / App.xaml.cs          # 应用入口、全局异常挂钩
├─ MultiSerialTool.csproj          # 版本号、资源、输出文件配置
│
├─ Models/                         # 数据模型
│  ├─ AppInfo.cs                   # 应用名/版本号统一入口
│  ├─ ReleaseInfo.cs               # changelog 中的一条版本记录
│  └─ Localization/                # 本地化引擎
│     ├─ LocalizationService.cs    # 单例，语言包加载与翻译
│     ├─ Loc.cs                    # C# 侧翻译入口 Loc.T()
│     ├─ TranslateExtension.cs     # XAML 标记扩展 {loc:Translate 文本}
│     └─ LocConverter.cs           # 绑定值翻译转换器
│
├─ Commands/                       # MVVM 命令
│  ├─ RelayCommand.cs              # 无参命令
│  └─ RelayCommandGeneric.cs       # 带参命令 RelayCommand<T>
│
├─ Services/                       # 基础设施服务
│  ├─ LogService.cs                # 全局调试日志单例
│  ├─ DebugLogEntry.cs / LogLevel.cs
│  ├─ AppConfig.cs                 # %LocalAppData%\MultiSerialTool\config.json
│  ├─ SerialDeviceWatcher.cs       # WMI 热插拔监听
│  └─ SerialPortInfoProvider.cs    # WMI 查询串口完整信息（VID/PID 等）
│
├─ Shared/                         # 跨协议共享的模型与工具
│  ├─ DeviceState.cs               # 连接状态枚举
│  ├─ LogEntry.cs                  # 一条收发日志（TX/RX）
│  └─ Find/                        # 查找/过滤匹配引擎
│     ├─ FindMatcher.cs
│     └─ FindMatchMode.cs
│
├─ ViewModels/                     # 顶层 ViewModel
│  ├─ ViewModelBase.cs             # INotifyPropertyChanged 基类
│  ├─ ProtocolViewModelBase.cs     # 协议 VM 基类（Title / Workspace）
│  ├─ MainViewModel.cs             # 主窗口 VM：协议切换 + 覆盖层
│  ├─ LogViewModel.cs              # LOG 页
│  ├─ SettingsViewModel.cs         # 设置页（占位）
│  ├─ FindDialogViewModel.cs       # 查找弹窗
│  └─ AboutViewModel.cs            # 版本页（位于 Views 命名空间）
│
├─ Views/                          # 顶层视图
│  ├─ MainWindow.xaml(.cs)         # 主窗口：菜单栏 / 状态栏 / 左右分栏
│  ├─ LogView.xaml(.cs)            # 全局日志页
│  ├─ SettingsView.xaml(.cs)       # 设置页
│  ├─ AboutWindow.xaml(.cs)        # 版本窗口
│  ├─ FindDialog.xaml(.cs)         # 公共查找弹窗
│  └─ ChartView.xaml(.cs)          # 图表页（文本日志 → 曲线）
│
├─ Protocols/                      # 协议模块（每协议一对 VM/View）
│  ├─ Serial/
│  │  ├─ SerialViewModel.cs        # 设备集合、连接管理、热插拔、自动重连
│  │  ├─ SerialConnection.cs       # 单个串口：波特率探测、收/发、故障上报
│  │  ├─ SerialDeviceInfo.cs       # 表格行数据 + 独立日志/草稿/版本令牌
│  │  ├─ SerialComposer.cs         # 单设备发送草稿与历史
│  │  ├─ SerialRawChunk.cs         # 原始数据块（字节/HEX/文本三视图）
│  │  ├─ SerialWorkspaceViewModel.cs
│  │  ├─ SerialWorkspaceView.xaml(.cs)
│  │  ├─ SerialPortView.xaml(.cs)  # 左侧设备表格
│  │  ├─ RuleModels.cs             # 设备规则 JSON 模型
│  │  ├─ RuleEditorViewModel.cs    # 规则页逻辑
│  │  ├─ RuleEditorView.xaml(.cs)
│  │  ├─ DeviceFieldModels.cs      # 设备参数字段模型（预留）
│  │  └─ DeviceParametersView.xaml # 设备参数容器（预留）
│  ├─ Tcp/                         # TCP 页（仅 VM + 空表格 XAML）
│  └─ Mqtt/                        # MQTT 页（仅 VM + 空表格 XAML）
│
├─ Assets/
│  ├─ Languages/zh-CN.json         # 嵌入资源语言包
│  ├─ Languages/en-US.json
│  ├─ Icons/*.svg                  # 嵌入资源 SVG 图标
│  ├─ DeviceRules/*.json           # 设备解析规则（复制到输出目录 DeviceRules）
│  └─ Changelog/*.json             # 版本历史（复制到输出目录 Changelog）
│
└─ Tests/                          # 独立控制台测试项目（不参与主项目编译）
   ├─ Program.cs                   # 串口回环测试 LoopbackTest
   ├─ LoopbackTest.csproj
   └─ FindMatcherTests/            # 查找引擎单元测试
```

> `.csproj` 中用 `<Compile Remove="Tests\**\*.cs" />` 把测试源码排除出主项目，避免重复的 AssemblyInfo 特性（CS0579）。

---

## 3. 分层架构

```
        Views (XAML)  ──DataBinding/Command──►  ViewModels
             │                                    │
             │ DataTemplate 自动选择视图            │ 调用
             ▼                                    ▼
        Protocols/Serial/*.xaml            Services / Shared / Models
                                          (LogService, WMI, FindMatcher,
                                           Localization, AppConfig)
                                                 │
                                                 ▼
                                          SerialConnection
                                          (System.IO.Ports)
```

约定：

- **View 不含业务逻辑**，仅做布局、绑定与少量 UI 交互（快捷键、滚动定位）。
- **ViewModel 不直接持有控件**，通过属性、命令、事件通信。
- **服务层为单例或静态类**，不依赖 UI 类型（个别位置为封送 UI 线程而访问 `Application.Current.Dispatcher`）。
- **所有 ViewModel 继承 `ViewModelBase`**，统一实现 `INotifyPropertyChanged`。

---

## 4. 应用启动与主窗口

### 4.1 启动流程

1. `App.xaml` 指定 `StartupUri="Views/MainWindow.xaml"`。
2. `App.OnStartup`（`App.xaml.cs:16`）依次执行：
   - `LocalizationService.Instance.Initialize()`：读取上次语言偏好（`%LocalAppData%\MultiSerialTool\current.txt`），加载语言包。
   - 注册三层全局异常钩子：UI 线程（`DispatcherUnhandledException`，吞掉并记录）、非 UI 线程（`AppDomain.UnhandledException`）、未观察的 Task（`TaskScheduler.UnobservedTaskException`）。
   - 写一条启动日志。
3. `MainWindow` 构造时：
   - 设置标题为 `AppInfo.FullName`（如 `MultiSerialTool-1.0.0-hfl`）。
   - 启动 1 秒周期的状态栏时钟定时器（与日志同源，均用 `DateTime.Now`）。
   - 窗口关闭时 `Stop` 定时器，并对 `DataContext` 调用 `Dispose()`（释放串口页的热插拔资源）。

### 4.2 MainViewModel —— 页面路由中枢

`MainViewModel`（`ViewModels/MainViewModel.cs`）持有：

| 成员 | 作用 |
|---|---|
| `Protocols` | 协议下拉项 `串口 / TCP / MQTT` |
| `CurrentProtocol` | 当前协议；写入时忽略 null（避免初始化中间态误触发切换） |
| `CurrentViewModel` | 左侧设备区当前协议 VM |
| `RightContent` | 右侧内容：`_overlay ?? 当前协议的 Workspace` |
| `_overlay` | 设置页 / LOG 页覆盖层，置空即返回设备工作区 |

关键设计：

- **协议 VM 单例缓存**：`_serialViewModel / _tcpViewModel / _mqttViewModel` 首次创建后常驻，切换协议只换引用，不重建。这样串口连接、收发历史、发送草稿全部保留，且不会产生第二个串口句柄。
- **设置/LOG 是覆盖层**：打开时只设置 `_overlay` 并通知 `RightContent`，不销毁协议实例，因此后台串口仍在收数据。
- 视图选择由 `MainWindow.xaml` 中的 `DataTemplate` 完成（见 4.3）。
- `Dispose()` 只释放串口页的热插拔监听，已打开的串口连接随进程退出由系统回收。

### 4.3 DataTemplate 视图映射

`Views/MainWindow.xaml` 资源中按 VM 类型映射视图：

| ViewModel | View |
|---|---|
| `SerialViewModel` | `SerialPortView`（左侧设备表格） |
| `TcpViewModel` / `MqttViewModel` | `TcpView` / `MqttView` |
| `SerialWorkspaceViewModel` | `SerialWorkspaceView`（右侧工作区） |
| `SettingsViewModel` | `SettingsView` |
| `LogViewModel` | `LogView` |

`ContentControl` 绑定某 VM 时 WPF 自动选用对应模板，无需手写切换逻辑。

### 4.4 主窗口布局

`MainWindow.xaml` 使用 DevExpress `BarManager`：

```
┌──────────────────────── 菜单栏（文件 / 仪器 / 帮助→设置|LOG|版本|语言） ────────────────────────┐
├──────────────┬─┬─────────────────────────────────────────────────────────────────────────────┤
│ 左侧 160px   │▌│ 右侧：RightContent                                                          │
│ 协议下拉      │ │  - 协议工作区（SerialWorkspaceView 等）                                      │
│ 设备列表      │ │  - 或覆盖层（SettingsView / LogView）                                        │
├──────────────┴─┴─────────────────────────────────────────────────────────────────────────────┤
│ 状态栏：就绪文本                                    测试记录时间（每秒刷新的时钟）             │
└──────────────────────────────────────────────────────────────────────────────────────────────┘
```

菜单中已绑定命令：`ShowSettingsCommand`、`ShowLogCommand`、`ShowAboutCommand`、`SwitchLanguageCommand`（参数为 culture code）。

---

## 5. MVVM 基础设施

### 5.1 ViewModelBase（`ViewModels/ViewModelBase.cs`）

- `SetProperty<T>(ref T field, T value, [CallerMemberName])`：值变化时触发 `PropertyChanged` 并返回 `true`，否则返回 `false`。
- `RaisePropertyChanged([CallerMemberName])`：手动通知，用于计算属性。

### 5.2 RelayCommand / RelayCommand&lt;T&gt;

- `RelayCommand`：包装 `Action` + 可选 `Func<bool>`。
- `RelayCommand<T>`：包装 `Action<T?>` + 可选 `Predicate<T?>`，参数做了类型转换兜底（失败返回 `default`）。
- 两者的 `CanExecuteChanged` 都挂到 `CommandManager.RequerySuggested`，由 WPF 自动重新求值，无需手动触发。

---

## 6. 串口模块（核心）

### 6.1 对象关系

```
SerialViewModel (协议 VM, 单例)
 ├─ Devices: ObservableCollection<SerialDeviceInfo>   → SerialPortView.DataGrid
 ├─ _connections: Dictionary<portName, SerialConnection>   ← 真实端口
 ├─ _connectTasks: Dictionary<portName, Task>              ← 在途连接任务
 ├─ _reconnectKeys: HashSet<key>                           ← 自动重连登记
 ├─ _deviceWatcher: SerialDeviceWatcher                    ← WMI 热插拔
 ├─ _debounceTimer: DispatcherTimer(500ms)                 ← 事件去抖
 └─ _workspace: SerialWorkspaceViewModel                   → SerialWorkspaceView

SerialDeviceInfo (一行设备)
 ├─ 基本信息：Name / FullName / Description / Manufacturer / HardwareId / Vid / Pid
 ├─ 状态：State(DeviceState) / BaudRate / ErrorMessage / StatusText
 ├─ ConnectVersion: int              ← 异步结果版本令牌
 ├─ LogEntries: ObservableCollection<LogEntry>   ← 设备独立收发日志
 └─ Composer: SerialComposer                     ← 设备独立草稿与历史
```

**一个设备的全部状态都挂在 `SerialDeviceInfo` 上**，切换选中行只是把工作区代理到另一台设备的数据源，不搬运数据。

### 6.2 串口枚举与设备行创建

- 首次：`LoadPorts()` 调 `SerialPort.GetPortNames()` 拿到端口名列表，再用 `SerialPortInfoProvider.QueryAll()` 经 WMI 补充完整名、描述、厂商、硬件 ID、VID/PID。
- `CreateDevice()` 生成 `SerialDeviceInfo`，默认 `BaudRate=9600`、`State=Disconnected`。
- WMI 失败时 `QueryAll` 返回空字典并记警告，`DisplayName` 回退为短名（如 `COM5`）。

### 6.3 连接流程与版本令牌

`ConnectAsync(device, silent)`（`SerialViewModel.cs:283`）：

1. 若状态为 `Connecting/Connected` 直接返回。
2. `version = ++device.ConnectVersion`，状态置 `Connecting`，清空错误。
3. 后台 `Task.Run(() => connection.Open())`（内含波特率探测，避免阻塞 UI）。
4. 完成后比对 `version != device.ConnectVersion`：
   - 不相等 → 结果已过期（用户取消/重连），`connection.Dispose()` 后丢弃。
   - 相等 → 登记连接、回填实际波特率、置 `Connected`、把重连 key 加入 `_reconnectKeys`。
5. 异常路径同样比对版本；未过期则置 `Error` 并记录。非静默模式且是当前选中设备时才弹窗。

**所有异步回调（收数据、故障、连接完成）都携带 version 校验**，确保旧流程的结果不会污染新流程。

### 6.4 自动波特率探测（`SerialConnection.DetectBaudRate`）

1. 候选速率按频率排序：`115200, 57600, 38400, 19200, 9600, 4800, 2400, 1200`。
2. 逐个尝试：打开端口 → 监听 600ms（每 50ms 轮询 `BytesToRead`）→ 统计帧错误（Frame/RXParity/Overrun）。
3. `ScoreData()` 评分：
   - 样本 < 8 字节：0 分。
   - 有帧错误：`max(0, 20 - 帧错误数*20)`，基本淘汰。
   - 无帧错误：基础 55 分，按可打印字符比例减垃圾字节（0x00/0xFF）比例 ×40 调整；含 CR/LF 额外 +10。
4. 取最高分速率；若全部为 0（设备不主动上报），保留调用方给的 `preferredBaudRate`（默认 9600），不盲目回退。
5. 打开失败处理：`UnauthorizedAccessException`（端口被占用）与 `IOException`（设备拔出/驱动异常）会被包装成中文提示后抛出，由上层捕获。
6. 评分 ≥ 85 提前结束探测。

端口参数固定：`8 数据位 / 无校验 / 1 停止位`，`DtrEnable` 与 `RtsEnable` 均为 true，读写超时 500ms。

### 6.5 接收数据链路

```
SerialPort.DataReceived（后台线程）
  └─ SerialConnection.OnDataReceived
       ├─ 读取 BytesToRead 字节
       ├─ AppendRawBytes → ConcurrentQueue<byte> 环形缓冲（上限 1MB，超出丢最旧）
       └─ DispatchOnUi: ReceivedChunks.Add + ChunkReceived 事件
            └─ SerialViewModel.OnChunkReceived(device, version, chunk)
                 ├─ version 校验
                 ├─ SplitByLineEnding() 按 CR/LF 切分
                 └─ 每段生成 LogEntry { Text, HexText } 写入 device.LogEntries
```

- 日志归属设备，未选中的设备也在后台持续记录。
- `SplitByLineEnding`：连续 CR/LF（含 CRLF）不产生空条目；无换行时整块算一条，**除换行符外不丢字节**。
- `SerialRawChunk` 同时提供 `Bytes / Hex（"41 54 0D"）/ Text（不可打印以 '.' 占位）` 三种视图。

### 6.6 发送链路

`SerialWorkspaceView` 的发送按钮 → `SerialWorkspaceViewModel.Send()`：

1. 从当前设备 `Composer.Commit()` 取出文本（同时入历史、清空草稿）。
2. 触发 `TxSubmitted` 事件。
3. `SerialViewModel.OnTxSubmitted`：
   - 先把 TX 记入该设备 `LogEntries`（`IsTx=true`）。
   - 查找 `_connections`；未连接 → 警告日志 + 提示框，不发送。
   - `connection.Write(text)`（UTF-8 编码）→ 异常记录并提示。

### 6.7 断开、取消与故障

| 操作 | 方法 | 行为 |
|---|---|---|
| 手动断开 | `Disconnect` | `ConnectVersion++` 使在途结果过期；释放并移除连接；状态置 Disconnected；**从 `_reconnectKeys` 移除**（再次插入不再自动连接） |
| 取消连接中 | `CancelConnecting` | `ConnectVersion++`；立即回退 Disconnected |
| 运行期故障 | `OnConnectionFaulted` | 版本匹配才处理；释放连接、置 Error；若端口仍在系统枚举中才弹窗（物理拔线不弹，等热插拔移除行） |

故障来源：`OnErrorReceived`（帧错误/奇偶错误 → 线路或波特率问题）、`OnDataReceived` 中的异常。`SerialConnection` 内部**所有后台路径都不向外抛异常**，统一通过 `Faulted` 事件上报。

### 6.8 热插拔与自动重连

```
WMI Win32_DeviceChangeEvent (EventType=2 到达 / 3 移除)
  └─ SerialDeviceWatcher.DeviceChanged（后台线程）
       └─ SerialViewModel.OnDeviceChanged → 封送 UI 线程 → ArmDebounce
            ├─ 记录 _pendingRemoval（仅 EventType=3）
            └─ 重启 500ms DispatcherTimer（合并连续事件）
                 └─ OnDebounceTick → HandleDeviceChanged
```

`HandleDeviceChanged` 分三步：

1. `RefreshPorts()` 差量刷新：
   - 消失的口：使版本过期、退订事件、释放连接、移除行；若为选中行则改选首行。
   - 新出现的口：创建 `SerialDeviceInfo` 加入集合，返回“新增设备列表”。
   - 未变化的：保留原状态与连接。
2. 对新设备检查 `_reconnectKeys`，命中则 `StartConnect(silent: true)`（静默自动重连）。
3. **快速拔插边缘场景**：去抖窗口内“拔出又插回”导致端口名净变化为零时，`RefreshPorts` 看不出变化，但旧句柄已失效。此时对“本周期发生过移除 + 仍在线上 + 已登记重连”的设备调用 `ForceReconnect`：
   - `ConnectVersion++`
   - 释放旧连接
   - `await` 旧在途任务结束（其探测结果因版本已过期被丢弃）
   - 重新 `StartConnect`

**重连标识** `ReconnectKey`：USB 设备用 `USB:<VID>:<PID>`（重插后 COM 号变化也能匹配）；无 VID/PID 时用 `PORT:<端口名>` 兜底。

### 6.9 工作区 VM（`SerialWorkspaceViewModel`）

作为“当前选中设备”的视图代理：

- `Device` setter 负责订阅/退订设备的 `PropertyChanged`，并通知 `CurrentLogEntries / CurrentStatusText / SendText`。
- `CurrentLogEntries` 直接返回 `Device.LogEntries`。
- `SendText` 读写 `Device.Composer.Draft`，切换设备互不影响。
- `CurrentStatusText` 由 `Device.StatusText` 组合 `Loc.T("串口状态：{0}")`。
- `IsHexDisplay` 控制 RX 日志显示 HEX 还是 ASCII。
- `TxColor / RxColor` 控制日志方向色（可在设置中扩展）。
- `DeviceFieldGroups`、`RefreshFieldCommand`、`LoadDeviceViewConfig` 为设备参数界面的**预留框架**（当前仅一个空分组）。

### 6.10 工作区视图结构（`SerialWorkspaceView.xaml`）

`DXTabControl` 四个页签：

| 页签 | 内容 |
|---|---|
| 日志 | 工具栏（状态文本、HEX 开关、查找/清空/设置按钮）+ 发送区（多行 TextBox + 发送按钮）+ 日志 ListBox |
| 设备信息 | 端口信息固定分组（绑定 `Device.*`）+ `DeviceParametersView`（预留） |
| 参数配置 | 占位文本 |
| 规则 | `RuleEditorView`（设备规则查看/编辑） |
| 图表 | `ChartView`（实时曲线） |

日志 ListBox 的 UI 行为在 code-behind（`SerialWorkspaceView.xaml.cs`）：

- **Ctrl+F / 查找按钮**：打开置顶非模态 `FindDialog`，传入 `FindDialogViewModel`（数据源=ListBox 可见项，搜索文本按当前 HEX/ASCII 视图选择，定位回调=选中并滚动）。
- **Ctrl+C**：复制选中多行，格式 `[yyyy-MM-dd HH:mm:ss] TX/RX 内容`。
- **Ctrl+滚轮**：日志字体 10~24 缩放。
- **发送框 ↑/↓**：光标位于第一行时上翻历史，位于最后一行时下翻历史，否则正常移动光标。
- 设备切换（`CurrentLogEntries` 变化）时自动滚动到最新，并重绑查找过滤器。

日志行模板：时间 + `TX/RX` 前缀（颜色由 `TxColor/RxColor` 决定）+ HEX 文本（仅在 HEX 开启且存在 `HexText` 时可见）+ ASCII 文本（HEX 关闭或 TX 条目时可见）。

---

## 7. 设备规则与图表

### 7.1 规则模型（`RuleModels.cs`）

一个 JSON 文件 = 一种设备类型 = 一个 `DeviceRuleSet`：

```jsonc
{
  "deviceType": "电压采集器",
  "fields": [        // 设备参数字段：query 为查询帧，parse 为应答解析规则
    { "key": "addr", "label": "设备地址",
      "query": "01 03 00 00 00 01 84 0A",
      "parse": { "type": "offset", "offset": 3, "length": 2 },
      "refresh": true }
  ],
  "charts": [        // 图表数据项
    { "key": "voltage", "label": "电压",
      "parse": { "type": "offset", "offset": 3, "length": 2, "scale": 0.1, "unit": "V" } }
  ],
  "autoReplies": [   // 自动应答规则
    { "index": 1, "command": "...", "expected": "...",
      "operation": "自动回复", "actualReply": "..." }
  ],
  "sample": { "request": "...", "response": "..." }
}
```

`ParseRule` 支持三种类型：`regex`（正则）、`offset`（固定字节偏移）、`modbus`（寄存器，预留）。可选 `scale` 缩放与 `unit` 单位；`Summary` 生成界面摘要。

### 7.2 规则页（`RuleEditorViewModel` + `RuleEditorView`）

- 规则目录固定为 `AppContext.BaseDirectory/DeviceRules`（构建时由 csproj 从 `Assets/DeviceRules` 复制）。
- 构造时扫描 `*.json`，文件名（不含扩展名）作为“设备类型”下拉选项，默认选中第一个。
- 选中后反序列化（大小写不敏感、允许注释与尾逗号），`CurrentRule` 供界面分区绑定，自动应答同步到 `ObservableCollection<AutoReplyRule>` 并重排序号。
- 支持新增/删除自动应答行（同时写回 `CurrentRule.AutoReplies`）。
- 视图在**设计时**不创建 VM（`RuleEditorView.xaml.cs` 检查 `DesignerProperties.GetIsInDesignMode`），避免设计器宿主下扫描目录产生副作用。

### 7.3 图表页（`ChartView.xaml.cs`）

图表页把“粘贴的文本日志”转换成多页签实时曲线。注意：粘贴内容**只定义规则**，不提供数据点；数据来自串口实时接收的日志。

1. **规则语法**
   - 分段标记：`-----chart1` 或 `-----chart1 n="光伏板信息"`（横线 ≥2、序号必填、名称可选）。
   - 段内字段写法：`Solar voltage: {%.2f}/current: {%.2f}`，只提取冒号前的字段名。
2. **粘贴并解析**（`BtnParse_Click`）：
   - `SplitSegments` 切段；
   - 每段提取字段（归一化：小写、压缩空格、去重保序）；
   - 段内第一个字段作为“锚点”；
   - 为每个字段创建一条 `SplineSeries2D`（8 色调色板循环），建立空 `ChartControl` 与页签；
   - 建立 `DataTable`（列 = `页签名.字段名`，避免跨页签同名字段冲突）。
3. **实时数据**：订阅 `SerialWorkspaceViewModel.CurrentLogEntries` 的 `CollectionChanged`，对每条 RX 日志：
   - 用 `KeyValueRegex`（`字段: 数值`）提取值，按出现顺序记录行首键；
   - 只有锚点等于本行行首键的页签才处理（防止裸字段跨组错配）；
   - 命中字段追加 `SeriesPoint` 并写入记录表行。
4. 清空会清掉图表、页签、记录表与订阅，回到空状态。

---

## 8. 本地化系统

采用**“源字符串即键”**模式：源码/XAML 中直接写中文，由 `LocalizationService` 按当前语言查表翻译。

| 入口 | 用法 |
|---|---|
| C# | `Loc.T("串口连接失败")`；带参数 `Loc.T("已接收 {0} 字节", n)` |
| XAML 静态文本 | `{loc:Translate 设置}`（`TranslateExtension`，内部绑定服务索引器） |
| XAML 动态绑定值 | `{Binding Xxx, Converter={StaticResource LocConverter}}`（如下拉项、表格单元格） |

- 语言包：`Assets/Languages/zh-CN.json`、`en-US.json`，作为嵌入资源打包（`pack://application:,,,/Assets/Languages/xx.json`）。
- 切换语言：`Switch(culture)` 重新加载字典 → 写入偏好文件 `current.txt` → 以空属性名触发 `PropertyChanged`，令所有绑定自动刷新。
- 翻译缺失时原样返回源字符串，界面不会出现空白。
- `GetAvailableCultures()` 通过资源管理器枚举 `assets/languages/*.json`。

---

## 9. 日志系统

分两类，互不混用：

| 类型 | 模型 | 用途 |
|---|---|---|
| 业务收发日志 | `Shared/LogEntry` | 每台设备独立的 TX/RX 内容，显示在工作区 |
| 全局调试日志 | `Services/DebugLogEntry` | 运行诊断，显示在 LOG 页并落盘 |

### 9.1 LogService（单例）

- **内存**：`ObservableCollection<DebugLogEntry> Entries`，上限 1000 条，超出淘汰最旧。
- **文件**：`<日志目录>/log_yyyyMMdd.txt`，按天滚动，同一天多次运行追加；新文件写 UTF-8 BOM，每条即时 `Flush`（崩溃不丢）。
- **启动读回**：构造时读取当天文件并解析回填内存（只保留最近 1000 条）。
- **线程安全**：`_fileLock` 保护文件写入；UI 集合更新经 `Dispatcher.BeginInvoke` 封送到 UI 线程。
- **行格式**：`HH:mm:ss.fff [LEVEL/Source] Operation: Detail`。
- **级别**：`Debug < Info < Warn < Error`，`MinLevel` 以下不记录。
- **日志目录**：默认 `%LocalAppData%\MultiSerialTool\Logs`，可通过 `SetLogDirectory` 切换（写探测文件验证权限 → 持久化到 `AppConfig` → 清空内存并读回新目录当天文件 → 触发 `DirectoryChanged`）。
- **静态字段顺序**：`LineRegex` 与 `DefaultLogDirectory` 必须声明在 `Current` 之前（静态字段按声明顺序初始化，构造函数中即会使用）。

### 9.2 配置（AppConfig）

- 固定位置：`%LocalAppData%\MultiSerialTool\config.json`。
- 当前只有 `LogDirectory` 一项。
- 读取失败/文件损坏返回默认实例；保存失败静默（写 Debug 输出）。

### 9.3 LOG 页（`LogViewModel` + `LogView`）

- 使用 `ICollectionView` 对 `LogService.Entries` 做级别过滤（全部/DEBUG/INFO/WARN/ERROR）。
- 支持清空内存日志（不影响已落盘文件）、打开日志目录、选择新目录、恢复默认目录。
- 级别文本按 INFO/WARN/ERROR 着色。

---

## 10. 查找与过滤引擎（`Shared/Find/FindMatcher`）

四种匹配方式：`Contains`（默认）、`WholeWord`（全词）、`Exact`（精确）、`Regex`（正则）。

- **全词匹配**：对关键字转义后按首尾字符是否为单词字符施加 `(?<!\w)` / `(?!\w)` 边界。符号开头（如 `+CSQ`）不额外加左边界，避免 `AT+CSQ` 中匹配失败。
- **过滤表达式**：
  - `|` 表示“或”，`\` 表示“与”；
  - 先按 `|` 分组，组内按 `\` 拆分；任一组内全部条件命中即整体命中（DNF）；
  - 空表达式视为不过滤（放行全部）；连续分隔符产生的空条件被忽略；
  - 正则模式下非法表达式通过 `out error` 返回错误信息，不抛异常。
- `TryBuildRegex` 使用 `CultureInvariant`，可选忽略大小写。

### 10.1 查找弹窗（`FindDialogViewModel` + `FindDialog`）

- 与具体列表控件解耦，宿主通过构造委托注入：`itemsProvider`（数据源）、`textSelector`（每项可搜索文本）、`locateCallback`（命中定位动作）。
- 两种工作方式：
  - **查找模式**：只定位命中项，上一个/下一个循环跳转，不隐藏数据；
  - **过滤模式**：通过 `Matches` 谓词隐藏不命中项，同时支持跳转。
- 快捷键：回车=下一个，Shift+回车=上一个，Esc=关闭；打开即聚焦输入框。
- 宿主（串口工作区）把 `Matches` 挂到 ListBox 默认视图的 `Filter`，弹窗关闭时移除过滤器并恢复完整显示。

---

## 11. 其他视图

### 11.1 版本窗口（`AboutWindow` / `AboutViewModel`）

- 显示应用名、版本（`Models/AppInfo` 从程序集 `InformationalVersion` 读取，去掉 `+commitsha` 后缀）、GitHub 地址。
- 历史版本来自输出目录 `Changelog/changelog.<culture>.json`，语言切换时自动重载；按版本号倒序（可解析为 `System.Version` 时用版本比较，否则字符串比较）。
- 外部替换 JSON 即生效，无需重新编译。

### 11.2 设置页（`SettingsView` / `SettingsViewModel`）

当前为占位页，仅提供“返回设备区”按钮（通过 `RelativeSource` 找到主窗口 `MainViewModel.ShowWorkspaceCommand`）。

### 11.3 TCP / MQTT

目前仅 `ProtocolViewModelBase` 派生 VM + 空表格 XAML（连接名、角色、目标 IP、端口等列），作为后续协议的扩展点。

---

## 12. 资源与构建配置

`MultiSerialTool.csproj` 要点：

- `Version` 为**版本号单一来源**，`AssemblyName` / `Product` / `AssemblyTitle` 均由 `$(Version)` 派生，输出 `MultiSerialTool-<版本>-hfl.exe`。
- 语言包与 SVG 图标作为 `Resource` 嵌入程序集。
- `Changelog/*.json`、`DeviceRules/*.json` 以 `CopyToOutputDirectory=PreserveNewest` 复制到输出目录，支持外部替换。
- 依赖：`DevExpress.Images`、`devexpress.wpf.carousel`、`DevExpress.Wpf.Charts`、`DevExpress.Wpf.LayoutControl`、`devexpress.wpf.ribbon`、`System.IO.Ports`、`System.Management`。

---

## 13. 测试项目（`Tests/`）

两个独立控制台项目，均以 **dll 引用**（非 ProjectReference）主项目输出，规避 WPF 可执行项目的 CS0579 冲突。改主项目后需先构建主项目。

| 项目 | 内容 |
|---|---|
| `LoopbackTest` | 需要一对虚拟串口（VSPD）或交叉线，默认 `COM9`/`COM8`。验证：连接打开、无探测数据时保留首选波特率、Modbus 二进制帧 RX（HEX/ASCII）、CRLF 文本帧 RX、TX 字节、中文 UTF-8 透传。 |
| `FindMatcherTests` | 覆盖包含/全词/精确/正则匹配，以及 `|`/`\` 过滤表达式的组合、空表达式、非法正则错误回传。 |

运行示例：

```bash
dotnet run --project Tests/FindMatcherTests
```

```bash
dotnet run --project Tests/LoopbackTest COM9 COM8
```

---

## 14. 关键设计模式与注意事项

1. **版本令牌（ConnectVersion）**：所有异步连接/接收结果都带版本校验，解决“取消/重连后旧结果回写”的竞态。
2. **协议 VM 单例**：切换协议不重建实例，保护串口句柄与用户状态。
3. **覆盖层而非页面替换**：设置/LOG 不销毁工作区，后台串口持续运行。
4. **故障不逃逸**：`SerialConnection`、WMI 回调、日志写入等后台路径统一 catch，避免未观察异常终止进程；App 层另有兜底钩子。
5. **线程边界**：串口/WMI 事件在后台线程产生，所有 UI 集合更新均经 Dispatcher 封送。
6. **外部可替换数据**：语言包（嵌入）、规则文件与 changelog（输出目录），便于现场调整。
7. **预留扩展点**：
   - `DeviceParametersView` / `DeviceFieldModels` / `LoadDeviceViewConfig`：设备参数动态界面；
   - `TcpViewModel` / `MqttViewModel`：协议实现；
   - `ParseRule.Type = "modbus"`：十六进制寄存器解析；
   - `SerialWorkspaceViewModel.TxColor/RxColor`：日志配色设置。

---

## 15. 构建与运行

```bash
dotnet build MultiSerialTool.csproj
```

```bash
dotnet run --project MultiSerialTool.csproj
```

- 需要 .NET 9 SDK 与 Windows；DevExpress NuGet 包需可访问（当前部署含私有源时请确认）。
- 运行数据目录：`%LocalAppData%\MultiSerialTool\`（`config.json`、`current.txt`、`Logs\`）。
- 输出目录附带 `DeviceRules\` 与 `Changelog\`，可直接编辑。
