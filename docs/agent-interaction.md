# Agent 提问与沙箱传输

`ask_user` 一次只询问一个问题。`choices=[]` 表示自由输入，终端直接显示输入框；不要把“是否需要通知”和“项目目录在哪”混成多选选项。`multiple=true` 时 `data.answer` 是字符串数组，AI 不需要再解析一层 JSON。单选和自由输入仍返回字符串，保留现有宿主回调接口。

只有主代理注册 `ask_user`。子代理没有用户提问回调和提问工具，缺信息、权限或环境条件时向主代理返回阻塞原因。单任务、并行与串行派发见 [代理运行设计](agent-runtime.zh-CN.md)；停止会等待实际宿主操作及子代理收尾。

选择界面的“其他”是可直接编辑的输入行。移到该行即可输入、粘贴和退格编辑；Enter 一次提交。多选时可同时提交勾选选项和自定义文字，不需要第二次提问或另一个 API。

沙箱单次 Python 源码限制为 32,000 字符。工具参数由可信前置代码序列化并以 1,024 字符分片传给宿主，不设置参数总量的固定上限，再通过工具调度、校验和审批执行；工具返回值和 stdout 也采用分片。file_read 按行分页并支持图片附件，file_edit 要求唯一匹配，file_write 不设置固定字符上限；以当前工具 schema 为准。分片不提供跨执行的 Python 状态持久化。

`file_edit` 只接受 `path` 和 `edits` 数组，例如 `call_tool("file_edit", path="src/File.cs", edits=[{"old_string":"unique old text", "new_string":"replacement"}])`。不支持旧的顶层 `old_string/new_string` 参数。每个条目基于同一原文件匹配，反向应用替换；任一条目失败、歧义、重叠或整批无变化都不写入。精确匹配优先，随后支持行尾空白和有限 Unicode 归一化；未涉及的行保留原文本，保留 UTF-8 BOM 与原换行风格。同文件的 file_edit/file_write 在进程内排队。输出包含 replacements、fuzzyMatch 和有界 diff。文件大小、批量编辑数量和检查点快照大小不设置固定上限，输出差异仍按预算展示。零匹配应重新读取当前目标区域；多匹配应增加唯一上下文，不能重复原请求或改用整文件覆盖绕过。

`file_read` 的 offset 是从 1 开始的行号，limit 是行数。每页最多 2,000 行或 20,000 UTF-8 字节，并同时限制 JSON 转义后的大小，返回 totalLines、nextOffset、outputLines、outputBytes 和 notice。普通分页保留完整行；超长单行返回 nextColumnOffset，保持同一 offset 并设置 column_offset 继续读取。文本文件不设置固定大小上限。PNG/JPEG/GIF/WebP 按内容签名识别，通过已有模型图片通道传递，单图仍限 4 MB，不把 base64 塞进工具 JSON。详见 [宿主文件与命令工具](host-tools.zh-CN.md)。

`list_directory` 默认隐藏常见构建、依赖及代理缓存目录（包括 .lanagent）；`include_ignored=true` 可以查看全部。先过滤、按名称排序，再按条目数和序列化字节预算分页；此工具仍不递归。

宿主已停止 Python AST 白名单审核。普通 Python 语法、关键字解包、推导式、元组解包和 lambda 由实际 guest 执行；模块可用性由隔离运行时决定。运行错误返回真实源码位置和异常诊断，宿主工具失败通过 ok/error 返回。guest 未配置宿主文件挂载或出站域名，宿主 I/O 通过工具完成。

Shell 设置 PlainText、NO_COLOR、TERM=dumb，宿主同时用流式解析清理 CSI/OSC 控制序列，支持跨输出块的序列；前台结果、后台实时输出和最终输出均使用清理后的文本计量容量。普通模式下 pwsh 仍需要原有审批，不能仅因命令中含 Get-Content 等字样就认定任意脚本只读。

初始模型上下文包含已验证的工作区、Python、PowerShell 路径、操作系统和容量限制。未实现全盘软件/仓库发现，也未引入跨执行 Python 状态；这些需要另外设计发现范围和会话状态生命周期。
