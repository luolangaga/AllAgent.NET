# CodeGraph 集成（AllAgent.NET）

本文说明 `AllAgent.NET.CodeGraph` 内置插件：它把 [CodeGraph](https://github.com/colbymchenry/codegraph) 的代码知识图接入终端和 Web 宿主。

## 为什么用 CLI，而不是 MCP

codegraph 的 MCP 输出与 CLI 等价（`codegraph explore`），但现有 `McpToolSet` 会把所有外部工具固定为 `ToolRisk.Execute`，每次调用都要审批；工具名还会变成 `mcp_<hash>_codegraph_explore`，无法稳定写进 SKILL.md 做按需披露。插件直接调用 CLI：

- 保持 `Read` 风险，静默调用；
- 自己控制超时、输出截断和错误诊断；
- 不引入 MCP 连接、重连和进程生命周期；
- 仍然复用 codegraph 的 `.codegraph/` 索引和后台 daemon。

## 组件

| 文件 | 作用 |
|---|---|
| `CodeGraphPlugin.cs` | 插件入口：播种技能、注册工具/命令/bridge、注入提示词 |
| `CodeGraphService.cs` | 检测、安装、status/explore、后台索引操作、取消、日志 |
| `CodeGraphLocator.cs` | 解析 `codegraph` 可执行文件；Windows `.cmd` 通过 `cmd /c call` 启动 |
| `CodeGraphTools.cs` | `codegraph_explore`、`codegraph_status`（均为 `Read`） |
| `Skills/code-graph/SKILL.md` | 按需披露技能（嵌入资源，运行时播种到 `<shared>/skills/code-graph`） |
| `web/settings.html/js/css` | 通用插件设置区里的状态与操作 iframe |

## 按需披露与提示词

插件在 `Register` 中播种技能：

```markdown
---
name: code-graph
description: 使用 CodeGraph 的预构建代码知识图回答架构、调用链、影响范围和 where-is 问题...
tools: [codegraph_*]
---
```

`ToolSurface.Discover` 因此生成 `code-graph` capability；`ToolSurface.Builtins` 还有一条兜底 `("code-graph", ["codegraph_*"])`，技能被禁用或缺失时工具仍然隐藏（fail-closed）。

插件同时注册 `AgentTurnExtension`：当技能启用且 `<workspace>/.codegraph/codegraph.db` 存在时，向 `VolatileSystemPrompt` 追加：

```text
<allagent-codegraph>
This workspace has a CodeGraph index...
1. Load the `code-graph` skill with call_tool("skill_read", name="code-graph").
2. Answer with `codegraph_explore` before grep or file-by-file reads...
3. Do not re-verify CodeGraph results with grep...
</allagent-codegraph>
```

`VolatileSystemPrompt` 只在内容变化时追加一条持久化 policy 快照，不影响稳定的 system 前缀。

## 工具

### `codegraph_explore`

- 参数：`query`（自然语言或符号/文件集合，≤4000 字符，不以 `-` 开头）。
- 工作区来自 `AgentExecutionScope.WorkspaceRoot`，不由模型传入路径。
- 调用前若索引存在且距上次 sync > 2 秒，先执行 `codegraph sync <workspace>`；失败只警告。
- 返回 `{ workspace, output, truncated, syncedAt }`，并附带 `ToolResult.Card`：
  - badge `CODEGRAPH`、title=query、summary=字符数/截断；
  - body `code`，预览最多 4000 字符；
  - 字段：工作区、同步时间、是否截断。

### `codegraph_status`

- 无参数；返回 `CodeGraphStatus`（CLI 路径/版本、索引路径/大小/更新时间、`codegraph status` 文本）。
- 同样附带 `ToolResult.Card`。

两个工具都通过 `context.AddWorkspaceIndependentTools` 注册；工具不捕获 workspace store，跨工作区 Web 会话也能用。

## 终端命令

```text
/ext codegraph status
/ext codegraph install --yes
/ext codegraph init
/ext codegraph sync
/ext codegraph rebuild
/ext codegraph explore <问题>
/ext codegraph path [绝对路径]
/ext codegraph unlock
/ext codegraph help
```

当前工作区来自 `PluginWorkspaceTasks.Current`（终端命令执行时惰性获取）。`init/sync/rebuild` 同步等待并返回最后输出；Web 侧走后台操作。

## Web 设置区

`allagent-plugin.json` 声明一个通用设置区：

```json
"settings": [{
  "id": "codegraph",
  "title": "CodeGraph",
  "fields": [
    { "key": "path", "label": "codegraph 可执行文件路径", "type": "text" },
    { "key": "autosync", "label": "查询前自动同步索引", "type": "boolean", "default": "true" }
  ],
  "entry": "web/settings.html"
}]
```

宿主 Settings 页通过通用 `PluginSettingsSection` 渲染：

- 原生字段（text/boolean/select）保存到 `<shared>/plugins/settings.json`；
- `entry` 在字段下方嵌入插件自己的 iframe，显示 CLI/索引状态和安装/构建/同步/取消按钮。

iframe 使用插件 SDK：

```js
const sdk = AllAgentPlugin.connect();
const status = await sdk.invoke('status', { workspace: sdk.context.workspace });
const operation = await sdk.invoke('index', { workspace, mode: 'sync' });
```

## 插件 bridge

```text
plugin.allagent.net.codegraph.status
plugin.allagent.net.codegraph.install
plugin.allagent.net.codegraph.index
plugin.allagent.net.codegraph.operation
plugin.allagent.net.codegraph.cancel
plugin.allagent.net.codegraph.settings
plugin.allagent.net.codegraph.saveSettings
```

`index`/`install` 立即返回 `CodeGraphOperation`（状态 `running/done/error/cancelled`），iframe 轮询 `operation`。

## 数据布局

```text
<workspace>/.codegraph/codegraph.db                 # codegraph 索引
<shared>/skills/code-graph/SKILL.md                 # 插件播种的按需技能
<shared>/plugins/settings.json                      # 通用插件设置值
<shared>/plugins/AllAgent.NET.CodeGraph-<ver>-builtin/data/logs/*.log
```

不把索引写入 `allagent.json`；不自动修改 `.gitignore`。

## 排错

| 现象 | 处理 |
|---|---|
| 状态显示“未安装” | `/ext codegraph install --yes`，或在设置页安装；也可设置 `ALLAGENT_CODEGRAPH_PATH` / 设置里的 path。 |
| `status` 正常但 `explore` 报索引不存在 | 在目标工作区运行 `/ext codegraph init`。 |
| sync/index 报 writer lock | 同一项目已有其他 codegraph writer；关闭其他会话或执行 `/ext codegraph unlock`。 |
| `codegraph` 在 PATH 但解析不到 | Windows 下可能是 npm 的 `.cmd`；插件会包装为 `cmd /c call`。仍失败时在设置里填绝对路径。 |
| 工具调用被拒绝并提示加载技能 | `/skills disable code-graph` 后工具保持隐藏；重新启用或 `skill_read("code-graph")`。 |
| 编辑后结果略旧 | 插件在每次 explore 前最多每 2 秒 sync 一次；等待下一次查询。 |
