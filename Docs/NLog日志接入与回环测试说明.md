# NLog 日志接入与回环测试说明

> 适用程序：MultiSerialTool（WPF / .NET 9，`net9.0-windows`）
> 关联文件：`Services/LogService.cs`、`Services/DebugLogEntry.cs`、`Services/LogLevel.cs`、`App.xaml.cs`
> 目的：固定"门面 API 不变、文件输出切到 NLog、双通道落盘"的口径，并给出可复算的收发样例、预期解析结果与回环测试步骤。

---

## 1. 改造范围与架构

### 1.1 改造前 → 改造后

| 项 | 改造前（自研 LogService） | 改造后（NLog 接入） |
|---|---|---|
| 文件写入 | 每条日志 `FileStream` 追加 + 即时 `Flush`，写盘在调用线程 | Debug/Info/Warn 走 `AsyncWrapper` 后台队列；Error 走同步直写 |
| 调用线程阻塞 | 每条都有磁盘 I/O 阻塞（约 0.1–2ms） | 常规日志入队约 50–200ns，磁盘 I/O 移到后台线程 |
| 内存集合 | `Entries`（上限 1000 条，UI 绑定） | **保留不变** |
| 对外 API | `Debug/Info/Warn/Error(source, operation, detail, ex)` | **保留不变**，52 处调用零修改 |
| 目录切换 / 启动读回 | 自研 | **保留不变**，仅落盘后端替换 |
| 退出冲刷 | 即时 Flush 天然不丢 | `OnExit` 显式 `LogManager.Flush()` + `Shutdown()` |

### 1.2 链路分层

```
业务代码（17 个文件，52 处调用）
        │  LogService.Current.Info/Warn/Error(...)
        ▼
LogService（门面单例）
   ├─ 内存：Entries（ObservableCollection，UI 绑定，1000 条上限，Dispatcher 封送）
   ├─ 级别过滤：MinLevel；UI 页级别过滤 ICollectionView
   └─ 文件：WriteToFile → NLog Logger
              ├─ Debug/Info/Warn → AsyncWrapper（queueLimit=10000, Block 反压）→ File
              └─ Error          → File（同步直写，不经队列）
```

### 1.3 双通道设计

| 通道 | 级别 | 目标 | 溢出策略 | 用途 |
|---|---|---|---|---|
| `asyncFile` | Debug / Info / Warn | 异步后台队列写文件 | `overflowAction="Block"`：队列满时反压调用方，**不丢弃日志** | 高频收发、常规运行信息 |
| `errorFile` | Error | 同步直写文件 | 无队列，逐条写盘 + `autoFlush` | 致命/异常错误，崩溃或强杀瞬间也尽量落盘 |

> 两条通道写同一个 `log_yyyyMMdd.txt`，行格式一致。

---

## 2. 配置说明（代码内编程式构建）

> NLog 配置由 `LogService.BuildNLogConfig` 在代码内编程式创建，不依赖外部 `nlog.config` 文件；
> 这样日志目录可直接传入，避免配置变量未初始化导致 File 目标路径解析失败，测试宿主（无配置文件）也能正常初始化。

### 2.1 关键参数

| 参数 | 取值 | 理由 |
|---|---|---|
| `queueLimit` | 10000 | 串口突发收发够用；单条约 200B，满队列约 2MB |
| `overflowAction` | `Block` | 测试记录不丢日志；队列满时调用方短暂阻塞等待，不丢弃 |
| `batchSize` | 200 | 后台线程每批写 200 条，减少 I/O 次数 |
| `timeToSleepBetweenBatches` | `0` | 吞吐优先，后台线程不空睡 |
| `keepFileOpen` | `true` | 保持句柄常开，避免每次写 open/close，吞吐约翻倍 |
| `archiveAboveSize` | 104857600（100MB） | 单文件过大滚动归档；按天仍由文件名 `log_yyyyMMdd` 承担 |
| `maxArchiveFiles` | 10 | 归档上限，避免磁盘无限增长 |
| `encoding` | `utf-8` | 中文可读 |
| `autoFlush` | `true` | 后台批次落盘后即时刷盘 |

### 2.2 行格式（与改造前完全一致）

```
HH:mm:ss.fff [LEVEL/Source] Operation: Message
```

- 时间戳由 `LogService` 在代码层格式化为 `HH:mm:ss.fff`（3 位毫秒），经事件属性 `Stamp` 传入，不依赖 NLog 时间渲染器格式差异。
- 操作名为空时，消息体只留 `Message`，不出现空冒号前缀。
- 异常文本已在 `Error()` 内拼进 `Message`（`详情 | 异常类型: 消息`），NLog 事件 `Exception` 置空，避免堆栈重复输出。

### 2.3 动态日志目录

- 日志目录不写死在配置里，由 `LogService` 启动时从 `AppConfig.LogDirectory`（缺省 `%LocalAppData%\MultiSerialTool\Logs`）读取，直接传给 `BuildNLogConfig` 构造 File 目标路径。
- 切换目录（`SetLogDirectory`）成功后以新目录重建整份 NLog 配置并赋给 `LogManager.Configuration`，File 目标随即指向新目录。

---

## 3. 实际串口收发样例（HEX 字节 / 应答 / 预期日志）

### 3.1 Modbus RTU 读保持寄存器（SCx 控制器）

**下行请求（TX，主机 → 设备，COM 9600 8N1）**

```
01 03 00 00 00 02 C4 0B
```

| 字节 | 含义 |
|---|---|
| `01` | 从机地址 |
| `03` | 功能码（读保持寄存器） |
| `00 00` | 起始寄存器地址 |
| `00 02` | 读取寄存器数量 2 |
| `C4 0B` | CRC16 校验 |

**上行应答（RX，设备 → 主机）**

```
01 03 04 00 00 00 00 FA 33
```

| 字节 | 含义 |
|---|---|
| `01` | 从机地址 |
| `03` | 功能码 |
| `04` | 数据字节数 4 |
| `00 00` | 寄存器 1 值（如电池电压高字） |
| `00 00` | 寄存器 2 值 |
| `FA 33` | CRC16 校验 |

**预期全局调试日志行（写入 `log_yyyyMMdd.txt`）**

```
12:44:13.782 [INFO/Serial] SendData: 01 03 00 00 00 02 C4 0B
12:44:13.905 [INFO/Serial] OnEngineFrameReady: 收到应答 01 03 04 00 00 00 00 FA 33
```

> 业务收发内容显示在设备日志面板（`LogEntry`）；以上是全局调试日志（`DebugLogEntry`）落盘行，两者不要混用。

### 3.2 错误样例（端口被占用，Error 同步通道）

**预期日志行**

```
12:45:02.118 [ERROR/Serial] OpenPort: 打开失败 | IOException: 端口被占用
```

- 该行走 `errorFile` 同步通道，不经过异步队列。
- 预期解析结果（启动读回正则）：级别 `ERROR`、来源 `Serial`、操作 `OpenPort`、详情 `打开失败 | IOException: 端口被占用`。

### 3.3 纯消息式日志（无操作名）

**调用**

```csharp
LogService.Current.Warn("Serial", "接收缓冲区接近上限");
```

**预期日志行（无前导空冒号）**

```
12:45:10.537 [WARN/Serial] 接收缓冲区接近上限
```

### 3.4 启动 / 退出生命周期样例

```
12:44:13.377 [INFO/App] Startup: 程序启动，版本 1.0.7.0，CLR 9.0.22
12:44:13.757 [DEBUG/Rule] 已加载规则：电压采集器，字段 1 个，自动应答 0 条，覆盖层：True
12:46:01.204 [INFO/App] Shutdown: 程序退出，退出码 0
```

---

## 4. 回环测试步骤

### 4.1 编译与产物核对

```powershell
cd G:\05_QT_Project\MultiSerialTool
dotnet build -c Debug -v minimal
```

预期：**0 错误**。输出目录 `bin\Debug\net9.0-windows\` 应包含：

- `NLog.dll`
- `NLog.dll`（NuGet 引用，随主程序集输出）

### 4.2 基础落盘与格式核对

1. 启动程序，连接一个可用 COM 口（或虚拟串口对）。
2. 发送 3.1 的 HEX 请求帧 `01 03 00 00 00 02 C4 0B`。
3. 打开 `%LocalAppData%\MultiSerialTool\Logs\log_yyyyMMdd.txt`，核对：
   - 时间为 `HH:mm:ss.fff`（3 位毫秒）；
   - 级别大写、来源在 `[]` 内；
   - TX/RX 行齐全，HEX 字节与发送一致；
   - 中文无乱码；纯消息式行无空冒号前缀。

### 4.3 启动读回（内存回填）

1. 退出程序后再次启动。
2. 打开 LOG 页面，当天历史应回填（最近 1000 条）。
3. 级别过滤逐一切换：全部 / DEBUG / INFO / WARN / ERROR，条目随级别正确过滤。
4. 用 3.2、3.3 的格式人工核对读回解析：级别、来源、操作、详情四段拆分正确。

### 4.4 目录切换

1. LOG 页点击「选择目录」，切到另一个可写目录。
2. 产生几条新日志，确认写入新目录；旧目录文件不再增长。
3. 点击「恢复默认」，回到 `%LocalAppData%\MultiSerialTool\Logs`。
4. 选择一个不可写目录（如受保护系统目录），应弹提示且保持原目录不变，不静默失败。

### 4.5 异步队列压测（不丢日志）

前置：VSPD 或物理交叉线建立成对端口；或用 `Tests/device_simulator.py` 对端模拟上报。

```powershell
# 对端高频上报（示例 COM8 @ 9600）
python Tests\device_simulator.py COM8 9600
```

1. 应用侧连接对端端口，让其持续上报 1–2 分钟。
2. 观察：UI 日志面板不卡顿；接收线程不被写盘阻塞。
3. 正常退出程序（触发 `Flush` + `Shutdown`），统计落盘条数与上报条数一致，队列尾部无缺条。
4. 复核日志文件尾部最后一批已落盘，验证退出冲刷生效。

### 4.6 Error 同步通道验证

1. 手动触发一次端口占用（占用同一 COM 口后再连接），或在测试路径构造一次 `Error` 调用。
2. 确认 `[ERROR/...]` 行即时出现在日志文件（不依赖异步批次刷出）。
3. 任务管理器强杀进程后，复查已写入的 Error 行完整保留。

### 4.7 归档验证

1. 临时把 `LogService.BuildNLogConfig` 里的 `ArchiveAboveSize` 调小（如 `102400`，100KB）。
2. 持续写日志使单文件超限。
3. 确认生成滚动归档文件，旧文件不被覆盖；验证后改回 100MB。

### 4.8 离线单测回归

```powershell
dotnet build Tests\EdgeCaseTests\EdgeCaseTests.csproj -v q --nologo
& "Tests\EdgeCaseTests\bin\Debug\net9.0-windows\EdgeCaseTests.exe"

dotnet build Tests\EngineTests\EngineTests.csproj -v q --nologo
& "Tests\EngineTests\bin\Debug\net9.0-windows\EngineTests.exe"
```

日志后端替换不改变成帧/归组/解析行为，上述单测应保持原有通过数。

---

## 5. 风险与取舍

| 风险 | 应对 |
|---|---|
| 异步队列退出丢尾部日志 | `OnExit` 显式 `Flush()` + `Shutdown()`；Error 级别另有同步直写通道 |
| 强杀/断电丢失队列尾部少量常规日志 | 异步方案固有取舍；致命错误已由 Error 同步通道兜底 |
| 目录切换后仍写旧路径 | 切换时更新 `${var:logDirectory}` 并 `ReconfigExistingLoggers()` |
| 中文乱码 | 配置 `encoding="utf-8"`；4.2 专门核对 |
| NLog 自身异常 | `internalLogLevel="Warn"`，内部异常记录到 `%LocalAppData%\MultiSerialTool\NLog-internal.log` |

## 6. 回退方案

门面 `LogService` 对外 API、`Entries`、目录切换、读回逻辑均未改动；若需回退到自研同步写盘，只需把 `WriteToFile` 内部实现换回原 `FileStream + Flush` 逻辑，调用方无需改动。
