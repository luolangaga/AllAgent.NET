# 文件搜索与工具输出

工具调用以紧凑的终端行展示：紫色 READ / EDIT / WRITE / SEARCH / SHELL 标签（历史会话里的 EVERYTHING 标签仍然保留显示），文件修改附带行号和红绿差异。终端点击操作或按 Ctrl+E 查看完整详情；桌面聊天点击标题或“展开详情”。长输出默认预览三行。

## 按文件名搜索（`names_only=true`）

`file_search` 的 `names_only=true` 走 ripgrep 的 `--files`，只列文件、不读内容：`file_pattern` 是文件名或相对路径通配符（如 `*.tsx`、`src/**/*.cs`），`pattern` 变成可选的名字过滤（默认普通文本，`regex=true` 时是正则），留空即列出全部匹配文件。名字模式同样遵守 `.gitignore`、排除构建/依赖目录（`include_ignored=true` 可放开），只列文件不列目录——查目录用 `list_directory`。结果返回工作区相对路径、`hasMore` 与 `nextOffset`，与内容模式共用同一套分页。

```python
# 按名称找文件（包含子目录，遵守 .gitignore）
print(call_tool('file_search', names_only=True, file_pattern='*合同*.docx'))
# 名字里再过滤一层；limit/offset 与其他文件工具一致
print(call_tool('file_search', names_only=True, file_pattern='*.pdf', pattern='合同', limit=50))
```

随包仍然携带 Everything 便携索引（`everything/`）与 `EverythingSearch` / `EverythingIndexer` 实现，供需要 Windows 索引加速的宿主恢复 `[AgentTool("everything_search")]` 后重新启用；默认不再注册为模型工具，`%LOCALAPPDATA%/AllAgent.NET/Everything/` 的索引缓存也不再随调用增长。

`file_search` 由 C# 宿主直接运行已安装的 ripgrep，无 Shell 拼接，尊重 `.gitignore`（包括子目录规则和否定规则）：

```python
# 搜索代码内容，返回行号和上下文
hits = call_tool('file_search', pattern='QuickMode|FastMode', regex=True,
                 path='src', file_pattern='*.cs', context_lines=2)
print(hits)
```

`file_search` 的 `file_pattern` 中 `*` 匹配单个路径段，`?` 匹配一个字符；`*.ts` 仅匹配搜索目录下的文件，`**/*.ts` 递归匹配。默认按不区分大小写的普通文本搜索，设置 `regex=true` 启用正则，`case_sensitive=true` 区分大小写。

`file_search` 支持非负 `offset`、任意正数 `limit` 分页，返回 `nextOffset`。默认尊重忽略规则，并跳过 `.git`、`bin`、`obj`、`node_modules`、`.venv`、`__pycache__`、`.mimosa`、`dist`、`.allagent`；`include_ignored=true` 绕过忽略规则。跳过链接；搜索跳过二进制文件，不按文本文件大小跳过。

扫描不设置固定文件数量上限或超时。`matchLimitReached` 或 `outputTruncated` 表示仍可继续分页，使用 `nextOffset` 继续；`linesTruncated` 或 `contextTruncated` 表示正文或上下文被裁剪，应使用 `file_read` 按对应行号读取。外部路径沿用目录授权机制。读取、图片、命令日志和全部字段见 [宿主工具文档](host-tools.zh-CN.md)。

思考与调度代码各保留单行预览和行数；思考使用 `Ctrl+O` 展开完整内容，代码使用 `Ctrl+E`，也可点击对应预览行。模型返回的思考在完成或进入工具调用后保留到聊天记录，并在重载会话时恢复。`/thinking` 控制思考显示。没有返回思考内容的模型只显示代码预览，不生成思考内容。

Python 源码直接交给 Hyperlight microVM，不经过宿主 AST 函数/方法白名单审核。运行时返回不支持的函数或语法错误；宿主工具仍检查目录权限、工具参数和审批。代码大小、超时及输出预算继续作为资源上限。长错误默认折叠，完整错误在代码详情中查看。
