namespace MultiSerialTool.Services.Production;

/// <summary>
/// 测试页与串口工作区之间的命令中继。测试页只发命令名称，
/// 由已持有真实串口工作区的 SerialViewModel 执行，避免重复打开端口。
/// </summary>
public sealed class TestCommandRelay
{
    public static TestCommandRelay Current { get; } = new();

    /// <summary>命令执行请求，参数为命令名称。</summary>
    public event Action<string>? CommandRequested;

    private TestCommandRelay()
    {
    }

    public void Send(string commandName) => CommandRequested?.Invoke(commandName);
}
