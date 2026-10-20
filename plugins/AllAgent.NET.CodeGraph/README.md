# AllAgent.NET.CodeGraph

内置的**进程内**代码知识图插件：用 tree-sitter 解析工作区，Agent 用一次 `codegraph_explore`
就能拿到相关源码、调用链和影响范围，替代反复 grep + 逐文件读取。

不需要安装 Node、npm、PowerShell 脚本或任何外部 CLI。

## 功能

- `codegraph_explore`：只读工具，按自然语言问题或符号/文件集合返回源码、调用路径与 blast radius。
- `codegraph_status`：只读工具，报告索引位置、大小、文件/符号/引用数和语言统计。
- 两个工具默认**不暴露**；模型通过 `skill_read("code-graph")` 加载技能后才进入工具目录，且不触发审批。
- 插件注入 `<allagent-codegraph>` 提示词：有索引时引导模型优先用 CodeGraph；无索引时说明首次查询会自动构建，不会要求用户安装。
- 终端 `/ext codegraph ...` 与 Web 设置页都能查看状态、构建/重建索引和取消操作。
- TUI 和 GUI 都通过 Core 的 `ToolResult.Card` 渲染 CodeGraph 卡片。

## 支持语言

| 语言 | 文件 | 语法 |
|---|---|---|
| C# | `.cs` | tree-sitter-c-sharp |
| TypeScript | `.ts` | tree-sitter-typescript |
| TSX | `.tsx` | tree-sitter-tsx |
| JavaScript | `.js/.jsx/.mjs/.cjs` | tree-sitter-javascript |
| Python | `.py` | tree-sitter-python |

其他文件进入文件表，可按文件名/文本命中，但不解析符号。调用方/影响面按**符号名**匹配，
不做类型推导；结果会标注 `Callers (name match)`。

## 索引

```text
<workspace>/.codegraph/allagent-index.json
```

- 首次 `codegraph_explore` 自动构建；
- 之后按文件 `(mtime, size, language)` 增量更新，查询前最多每 2 秒检查一次；
- 尊重各级 `.gitignore`（注释、取反、目录、锚定、常见 glob）；
- 上限：20,000 文件、单文件 1 MB、构建 60 秒；
- 忽略 `.git bin obj node_modules dist build out coverage .venv venv __pycache__ artifacts target` 等目录；
- 旧版 CLI 的 `codegraph.db` 不再读取，也不会被修改。

建议把 `.codegraph/` 加入项目的 `.gitignore`；插件不会自动修改工作区文件。

## 命令

```text
/ext codegraph status
/ext codegraph init
/ext codegraph sync
/ext codegraph rebuild
/ext codegraph explore <问题>
/ext codegraph help
```

## 权限与安全

- `codegraph_explore` / `codegraph_status` 是 `ToolRisk.Read`，不弹审批。
- 解析和索引全部在插件进程内完成，不启动子进程，不拼 shell 命令。
- 工作区来自 `AgentExecutionScope`，索引只写入 `<workspace>/.codegraph/`。
- 原生语法库从插件自己的 `native/win-x64/` 预加载；加载失败只降级对应语言。

## 体积

只分发 Windows x64 需要的 6 个文件，约 **9.7 MB**：

```text
TreeSitter.dll                 64 KB
tree-sitter.dll               288 KB
tree-sitter-c-sharp.dll       4.97 MB
tree-sitter-typescript.dll    1.45 MB
tree-sitter-tsx.dll           1.48 MB
tree-sitter-javascript.dll    0.49 MB
tree-sitter-python.dll        0.54 MB
```

`TreeSitter.DotNet` 的完整包是 50.9 MB（9 个 RID × 28 种语法），只在构建机首次 restore
时下载；插件用 `PrivateAssets=all` 引用它，只把上述子集复制到输出和安装包。

## 构建

```powershell
dotnet build plugins/AllAgent.NET.CodeGraph -c Debug
dotnet pack plugins/AllAgent.NET.CodeGraph -c Release -o artifacts/packages
```

宿主构建时 `CopyBuiltinPlugins` 会把 DLL、`TreeSitter.dll`、`native/win-x64/*.dll`、
`allagent-plugin.json` 和 `web/**` 复制到 `builtin-plugins/AllAgent.NET.CodeGraph/`；
首次启动由 `PluginHost.InstallBuiltins` 安装到共享插件目录。
