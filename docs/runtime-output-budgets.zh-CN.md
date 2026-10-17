# 执行预算与重试

宿主可通过 `AgentOptions` 配置读取及传输预算，默认值保持兼容：

| 配置 | 默认值 | 含义 |
| --- | ---: | --- |
| FileReadMaxBytes | 20000 | 每页文件内容的 UTF-8 / JSON 转义字节上限 |
| FileReadMaxLines | 2000 | 每页文件行数上限 |
| MaxCodeCharacters | 32000 | execute_code 源码的 UTF-16 字符数上限，最高 128000 |
| MaxToolResultCharacters | 64000 | 注册工具结果的 JSON 字符数上限，最高 4000000 |
| MaxToolStreamCharacters | 20000 | 注册工具结果中每个 stdout/stderr 字段的字符上限 |
| RetryAfterModelOutput | false | 是否允许对已经返回输出或用量的请求自动重试 |

例如库宿主希望单次读取约 100 KB：

```csharp
var options = new AgentOptions
{
    WorkspaceRoot = workspace,
    FileReadMaxBytes = 110_000,
    MaxToolResultCharacters = 150_000,
    MaxCodeCharacters = 100_000
};
```

`file_read` 新增可选 `max_bytes`，0 使用宿主上限，正数至少 256 且不得超过该上限。`limit` 仍控制请求行数。读取结果包含 `byteLimit`、`lineLimit`、`remainingLines` 和原有续读坐标。预算由宿主决定，聊天中的自然语言不会自行修改宿主配置。

文件页与总结果预算要一起调整，为 JSON 字段及转义留足空间。沙箱最终 stdout 仍有独立的 20000 字符上限；大文件可在沙箱内处理后打印所需片段或摘要。runtimeContext 明示这一限制。

`powershell_script` 在 worker 侧先限制 records（512 条或约 160,000 字符）、text（约 160,000 字符）和每类输出流（32 条、单条 4000 字符，progress 8 条），再进入上面的 `MaxToolResultCharacters` 预算统一裁剪；consoleOutput 另限 20,000 字符。

注册工具结果超限时优先缩短字符串，保留对象、数组及数值字段；极端情况下连空字符串结构也放不下时，保留集合前缀。不会再用 2000 字符的 JSON 字符串替换整个 data。外层 `outputTruncation` 记录原 JSON 长度、裁剪后 JSON 长度及差值（不包含新增诊断字段与 truncated 标记变化）。这里统计 UTF-16 字符数，不代表 token 数或原文件字节数。被裁剪内容需要重新读取；该诊断不是完整结果存档。

重试仍受 `MaxModelRetries` 与瞬时错误分类控制。默认只有尚未收到文本、思考、工具调用、原生状态或正数用量的失败可以自动重试。显式设置 `RetryAfterModelOutput = true` 可恢复此前的响应重放行为，可能产生额外费用。即便客户端未收到输出，服务端也可能已经开始计算，因此此策略不能保证失败请求完全不计费；预先探测请求也不能提供这种保证。

`hostCallSummary.failures` 记录最多 20 条工具名和错误（每条最多 1000 字符）。非法 parallel 批次也计入汇总，避免客体代码未打印返回值时错误原因丢失。整批先校验，再启动子调用。

Python 环境分工：活动 Sandbox runtime 说明决定 guest 语法和模块；pip.md 只列 AllAgent.NET 管理的 package venv 中的第三方包，并给出 sys.executable、sys.prefix、sys.base_prefix。不要据此推断全局 Python 或 Monty 私有环境中安装了相同包。
