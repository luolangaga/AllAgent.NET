# 文件搜索与工具输出

工具调用以紧凑的终端行展示：紫色 READ / EDIT / WRITE / SEARCH / GLOB / SHELL 标签，文件修改附带行号和红绿差异。终端点击操作或按 Ctrl+E 查看完整详情；桌面聊天点击标题或“展开详情”。长输出默认预览三行。

两个只读工具由 C# 宿主直接运行已安装的 ripgrep，无 Shell 拼接，尊重 `.gitignore`（包括子目录规则和否定规则）：

```python
# 查找所有 C# 文件（** 可匹配零层或多层目录）
files = call_tool('file_glob', pattern='**/*.cs', path='src')
# 搜索代码内容，返回行号和上下文
hits = call_tool('file_search', pattern='QuickMode|FastMode', regex=True,
                 path='src', file_pattern='*.cs', context_lines=2)
print(hits)
```

`file_glob` 的 `*` 匹配单个路径段，`?` 匹配一个字符；`*.ts` 仅匹配搜索目录下的文件，`**/*.ts` 递归匹配。`file_search` 默认按不区分大小写的普通文本搜索，设置 `regex=true` 启用正则，`case_sensitive=true` 区分大小写。

两者支持 非负 `offset`、任意正数 `limit` 分页，返回 `nextOffset`。默认尊重忽略规则，并跳过 `.git`、`bin`、`obj`、`node_modules`、`.venv`、`__pycache__`、`.mimosa`、`dist`、`.lanagent`；`include_ignored=true` 绕过忽略规则。跳过链接；搜索跳过二进制文件，不按文本文件大小跳过。

路径查找不设置固定扫描数量上限，分页未完成时 `totalMatches=null`。`outputTruncated` 表示输出预算已用完，使用 `nextOffset` 继续；`linesTruncated` 表示正文或上下文被裁剪，应使用 `file_read` 按对应行号读取。扫描不设置固定超时，外部路径沿用目录授权机制。读取、图片、命令日志和全部字段见 [宿主工具文档](host-tools.zh-CN.md)。

思考与调度代码各保留单行预览和行数；思考使用 `Ctrl+O` 展开完整内容，代码使用 `Ctrl+E`，也可点击对应预览行。模型返回的思考在完成或进入工具调用后保留到聊天记录，并在重载会话时恢复。`/thinking` 控制思考显示。没有返回思考内容的模型只显示代码预览，不生成思考内容。

Python 源码直接交给 Hyperlight microVM，不经过宿主 AST 函数/方法白名单审核。运行时返回不支持的函数或语法错误；宿主工具仍检查目录权限、工具参数和审批。代码大小、超时及输出预算继续作为资源上限。长错误默认折叠，完整错误在代码详情中查看。
