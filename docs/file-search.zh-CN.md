# 文件搜索与工具输出

工具调用以紧凑的终端行展示：紫色 READ / EDIT / WRITE / SEARCH / EVERYTHING / SHELL 标签，文件修改附带行号和红绿差异。终端点击操作或按 Ctrl+E 查看完整详情；桌面聊天点击标题或“展开详情”。长输出默认预览三行。

## 内置 Everything 文件名搜索（Windows）

`everything_search` 随应用携带 Everything 1.4.1.1032 便携索引引擎和 ES 1.1.0.38 查询端，支持 Windows x64、x86、ARM64。无需另行安装、配置、启动 Everything，也不要求管理员权限或系统服务。首次调用自动在后台索引获准目录，后续调用复用索引并监控文件变化。首次扫描大型目录仍需时间；它不提供 NTFS 服务模式的首次全盘建库速度。

```python
# 在工作区按名称快速找文件（包含子目录）
print(call_tool('everything_search', query='*合同*.docx'))
# 明确指定更大的目录；工作区外沿用目录授权
print(call_tool('everything_search', query='*.pdf', path='C:/Users/me/Documents', limit=50))
```

`query` 是普通文件名文本或 `*` / `?` 通配符，不接受路径；Everything 查询操作符按普通名称字符处理，不提供内容搜索。返回绝对路径 `paths`、`hasMore` 和 `nextOffset`；`hasMore=true` 时用相同参数和返回的 `nextOffset` 翻页，过滤失效索引项时本页可能为空。它包含隐藏文件和忽略文件，不读取 `.gitignore`；全文与忽略规则搜索继续用 `file_search`。

索引缓存位于 `%LOCALAPPDATA%/AllAgent.NET/Everything/`，只对本次获准目录建库；不安装开机启动项，不影响用户自己的 Everything。每个宿主最多运行一个索引进程，切换目录时重启自己的索引器；启动时刷新目录，避免遗漏停机期间新增的文件，之后复用当前索引，宿主正常退出时关闭自己的后台进程。并发宿主使用独立数据库避免写冲突。索引反映最近监控到的状态，文件刚修改时可能短暂滞后；返回前重新检查路径边界、存在性和链接。

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
