# CodeGraph 集成（AllAgent.NET）

本文说明 `AllAgent.NET.CodeGraph` 内置插件：它在宿主进程内用 tree-sitter 构建代码索引，
为终端和 Web 宿主提供架构、调用链、影响范围和 where-is 查询。

## 为什么是进程内索引

旧版插件是对 Node CLI `@colbymchenry/codegraph` 的壳：安装依赖 npm 或官方 PowerShell
脚本，查询依赖外部进程，索引依赖 CLI 自己维护的数据库。现在的实现全部在插件进程内：

- 不需要 Node/npm/PowerShell，也不写 PATH 探测和安装逻辑；
- 没有子进程、超时、写锁和 shell 转义问题；
- 工具保持 `ToolRisk.Read`，静默调用；
- 首次 `codegraph_explore` 自动建索引，之后按文件变更增量更新，不需要用户先执行 init。

## 解析范围

使用 [TreeSitter.DotNet](https://github.com/mariusgreuel/tree-sitter-dotnet-bindings) 1.3.0
的真实语法树，不再用正则猜测：

| 语言 | 文件 | 语法 |
|---|---|---|
| C# | `.cs` | tree-sitter-c-sharp |
| TypeScript | `.ts` | tree-sitter-typescript |
| TSX | `.tsx` | tree-sitter-tsx |
| JavaScript | `.js/.jsx/.mjs/.cjs` | tree-sitter-javascript |
| Python | `.py` | tree-sitter-python |

其他文件仍进入文件表，可按文件名/文本命中，但不解析符号。

调用方/被调用方/影响面按**符号名**匹配（语法级，不做类型推导）；结果里会标注
`Callers (name match)`。这与 tree-sitter 版 CodeGraph 的精度同级。

## 组件

| 文件 | 作用 |
|---|---|
| `CodeGraphPlugin.cs` | 插件入口：播种技能、注册工具/命令/bridge、注入提示词 |
| `CodeGraphService.cs` | 索引构建/刷新、状态、查询、后台操作、取消 |
| `CodeIndex.cs` | 索引模型、文件发现、增量、JSON 持久化、检索、调用/影响分析 |
| `GitIgnore.cs` | 各级 `.gitignore` 的最小实现（注释、取反、目录、锚定、常见 glob） |
| `CodeScanner.cs` | tree-sitter 语法树 → 符号 / 引用（C#、TS、TSX、JS、Python） |
| `TreeSitterNative.cs` | 从插件目录预加载 Windows x64 原生语法库，失败时按语言降级 |
| `CodeGraphTools.cs` | `codegraph_explore`、`codegraph_status`（均为 `Read`） |
| `Skills/code-graph/SKILL.md` | 按需披露技能（嵌入资源，运行时播种到 `<shared>/skills/code-graph`） |
| `web/settings.html/js/css` | 通用插件设置区里的状态与操作 iframe |

## 索引

### 位置与格式

```text
<workspace>/.codegraph/allagent-index.json
```

按文件分组存储，便于只替换变更文件的条目：

```json
{
  "version": 1,
  "workspace": "C:/repo",
  "builtAt": "2026-10-20T12:34:56Z",
  "files": {
    "src/App.cs": {
      "language": "csharp",
      "modifiedTicks": 638000000000000000,
      "size": 1234,
      "symbols": [{ "name": "App", "kind": "class", "startLine": 3, "endLine": 40 }],
      "references": [{ "name": "Run", "line": 17 }]
    }
  }
}
```

旧版 CLI 的 `.codegraph/codegraph.db` 不再读取；新索引写在同目录的 `allagent-index.json`，
两者互不冲突。插件不会修改 `.gitignore`，建议项目自行忽略 `.codegraph/`。

### 增量与上限

- 按 `(mtime, size, language)` 检测变更，只重新解析变更文件；
- 变更超过 30%（且至少 20 个文件）或索引损坏/版本不符时全量重建；
- 查询前最多每 2 秒检查一次（`autosync`）；
- 尊重各级 `.gitignore`（注释、取反、目录、锚定、`*`/`**` 等常见形式），被忽略的参考代码/生成目录不会进入索引；
- 最多 20,000 个文件；单个文件超过 1 MB 不解析；构建超过 60 秒返回部分索引并标记；
- 忽略 `.git .svn .vs .idea .allagent .codegraph bin obj node_modules dist build out coverage .venv venv __pycache__ artifacts target` 和隐藏目录。

## 工具

### `codegraph_explore`

- 参数：`query`（自然语言或符号/文件集合，≤4000 字符，不以 `-` 开头）。
- 工作区来自 `AgentExecutionScope.WorkspaceRoot`，不由模型传入路径。
- 符号打分：名字精确/前缀/包含优先，其次全名、文件路径；多词求和，按词的稀有度（IDF）加权，大小写完全匹配和类型符号有额外加成；
- 无符号命中时退化为索引内文本搜索（`path:line: text`）。
- 最多返回 6 个符号，每个包含：
  - `## Name (kind) · path:start-end` 与签名；
  - 源码片段（≤120 行 / 6000 字符）；
  - `Callers (name match)`、`Calls`、`Impact`；
  - 总输出 ≤64 KB，超出截断。
- 返回 `{ workspace, output, truncated, syncedAt }`，并附带 `ToolResult.Card`：
  badge `CODEGRAPH`、title=query、summary=字符数/截断。

### `codegraph_status`

- 无参数；返回 `CodeGraphStatus`（索引路径/大小/更新时间、文件/符号/引用数、语言统计、解析错误）。
- 同样附带 `ToolResult.Card`。

两个工具都通过 `context.AddWorkspaceIndependentTools` 注册；工具不捕获 workspace store，跨工作区 Web 会话也能用。

## 按需披露与提示词

插件在 `Register` 中播种技能：

```markdown
---
name: code-graph
description: 使用内置 CodeGraph 索引回答架构、调用链、影响范围和 where-is 问题...
tools: [codegraph_*]
---
```

`ToolSurface.Discover` 因此生成 `code-graph` capability；`ToolSurface.Builtins` 还有一条兜底
`("code-graph", ["codegraph_*"])`，技能被禁用或缺失时工具仍然隐藏（fail-closed）。

插件同时注册 `AgentTurnExtension`：

- 技能启用且索引存在：要求先 `skill_read("code-graph")`，再用 `codegraph_explore` 回答，
  不要用 grep 重新验证；
- 技能启用但索引不存在：说明首次 `codegraph_explore` 会自动建索引，**不要**让用户安装或构建；
- 技能禁用：不注入任何内容。

`VolatileSystemPrompt` 只在内容变化时追加一条持久化 policy 快照，不影响稳定的 system 前缀。

## 终端命令

```text
/ext codegraph status
/ext codegraph init
/ext codegraph sync
/ext codegraph rebuild
/ext codegraph explore <问题>
/ext codegraph help
```

`init/sync/rebuild` 通过后台操作执行并同步等待结果；`explore` 直接查询并返回文本。

## Web 设置区

`allagent-plugin.json` 声明一个通用设置区：

```json
"settings": [{
  "id": "codegraph",
  "title": "CodeGraph",
  "fields": [
    { "key": "autosync", "label": "查询前自动增量索引", "type": "boolean", "default": "true" }
  ],
  "entry": "web/settings.html"
}]
```

宿主 Settings 页通过通用 `PluginSettingsSection` 渲染：

- `autosync` 保存到 `<shared>/plugins/settings.json`；
- `entry` 在字段下方嵌入插件 iframe，显示索引状态和“构建/更新”“重建”“取消”按钮。

iframe 使用插件 SDK：

```js
const sdk = AllAgentPlugin.connect();
const status = await sdk.invoke('status', { workspace: sdk.context.workspace });
const operation = await sdk.invoke('index', { workspace, mode: 'init' });
```

SDK 每 500ms 重发一次 `hello`，宿主对每个合法 `hello` 都回 `init`，避免 iframe 在宿主
注册监听之前完成加载而握手超时。

## 插件 bridge

```text
plugin.allagent.net.codegraph.status
plugin.allagent.net.codegraph.index
plugin.allagent.net.codegraph.operation
plugin.allagent.net.codegraph.cancel
plugin.allagent.net.codegraph.settings
plugin.allagent.net.codegraph.saveSettings
```

`index` 立即返回 `CodeGraphOperation`（状态 `running/done/error/cancelled`），iframe 轮询 `operation`。

## 原生库与体积

插件只分发 Windows x64 需要的 6 个文件（`native/win-x64/`）：

```text
TreeSitter.dll                 64 KB   (托管绑定)
tree-sitter.dll               288 KB   (解析核心)
tree-sitter-c-sharp.dll       4.97 MB
tree-sitter-typescript.dll    1.45 MB
tree-sitter-tsx.dll           1.48 MB
tree-sitter-javascript.dll    0.49 MB
tree-sitter-python.dll        0.54 MB
合计                         约 9.7 MB
```

`TreeSitter.DotNet` 的完整 NuGet 包是 50.9 MB（9 个 RID × 28 种语法），但只在构建机
首次 restore 时下载；插件项目用 `PrivateAssets=all` 引用它，并只把上述 6 个文件复制到
`native/win-x64/` 和安装包的 `allagent/native/win-x64/`。运行时不需要额外下载。

`TreeSitter.DotNet` 的默认加载器只搜索 `AppContext.BaseDirectory` 和
`BaseDirectory/runtimes/<rid>/native`，不搜索插件目录。插件在 `Register` 时按完整路径
`NativeLibrary.Load` 这些 DLL；Windows 按模块基名复用已加载模块，之后绑定内部的按名
加载会直接命中。任一语法加载失败只禁用对应语言，其余语言和文本检索照常工作。

宿主当前发布目标为 win-x64（见 `installer/AllAgent.NET.iss`）。如果以后发布 win-arm64，
需要把同样 6 个 arm64 原生文件加入 `native/win-arm64/`。

## 数据布局

```text
<workspace>/.codegraph/allagent-index.json          # 内置索引
<shared>/skills/code-graph/SKILL.md                 # 插件播种的按需技能
<shared>/plugins/settings.json                      # 通用插件设置值
<shared>/plugins/AllAgent.NET.CodeGraph-<ver>-builtin/
  AllAgent.NET.CodeGraph.dll
  TreeSitter.dll
  native/win-x64/*.dll
  web/**
```

## 排错

| 现象 | 处理 |
|---|---|
| 状态显示“索引未构建” | 直接 `codegraph_explore`，首次查询会自动构建；或 `/ext codegraph rebuild`。 |
| 状态显示“无法加载 tree-sitter-*.dll” | 重建插件：`dotnet build plugins/AllAgent.NET.CodeGraph`，确认输出包含 `native/win-x64/`。 |
| 某个语言没有结果 | 查看 `codegraph_status` 的解析错误；该语法 DLL 可能缺失，其余语言不受影响。 |
| 编辑后结果略旧 | `autosync` 最多每 2 秒检查一次；稍等或执行 `/ext codegraph sync`。 |
| 大仓库只返回部分结果 | 索引上限 20,000 文件 / 单文件 1 MB / 构建 60 秒；缩小工作区后重建。 |
| 同名方法/重载有歧义 | 调用关系按名字匹配，结果标注 `Callers (name match)`；需要精确结论时再用 grep/读取确认。 |
| 设置页 iframe 握手超时 | SDK 已改为重试；确认宿主和插件 Web 资产是最新构建，然后刷新设置页。 |
| 工具调用被拒绝并提示加载技能 | `/skills disable code-graph` 后工具保持隐藏；重新启用或 `skill_read("code-graph")`。 |

## 构建

```powershell
dotnet build plugins/AllAgent.NET.CodeGraph -c Debug
dotnet pack plugins/AllAgent.NET.CodeGraph -c Release -o artifacts/packages
```

首次 restore 会下载约 51 MB 的 `TreeSitter.DotNet` 包到全局 NuGet 缓存。宿主构建时
`CopyBuiltinPlugins` 会把插件 DLL、`TreeSitter.dll`、`native/win-x64/*.dll`、`allagent-plugin.json`
和 `web/**` 复制到 `builtin-plugins/AllAgent.NET.CodeGraph/`；首次启动由
`PluginHost.InstallBuiltins` 安装到共享插件目录。
