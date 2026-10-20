# 插件 Web 前端扩展指南（Plugin Web Extensions）

> 插件不再只是终端和后端能力：通过 `allagent-plugin.json` 的 `web` 块，插件可以向 Web 工作台（`/web-ui`、`gui` 窗口、GUI 模式）新增导航页面、自定义工具卡与命令面板入口，并在宿主提供的**沙箱 iframe** 中运行完全自绘的界面（任意框架、任意样式）。插件后端与自己的界面通过命名空间桥方法双向通信，主题与上下文由宿主实时同步。

配套文档：[通用插件开发与使用指南](plugins.zh-CN.md) · [插件开发入门](plugin-development-beginner.zh-CN.md)

## 1. 自由度分档

| 档位 | 形态 | 由谁渲染 | 自由度 | 现状 |
|---|---|---|---|---|
| ① 声明式贡献 | manifest `web.contributes`（页面、顶部弹窗、工具卡、命令） | 宿主（Iconiq 组件） | 中 | ✅ 已实现 |
| ② 沙箱视图 | 插件自带 HTML/CSS/JS，入驻宿主的**独立源 iframe**，经 SDK 双向通信 | 插件自绘 | 极高 | ✅ 已实现 |
| ③ 受信任原生模块 | `web.mode:"native"`，ESM 直接使用宿主 React 组件 | 插件 + 宿主组件库 | 最高 | ⏳ 规划中（设计已预留字段） |

大多数场景用 ②：插件可以完全掌控自己页面里的每个像素，同时通过 SDK 拿到宿主数据、主题和事件，不牺牲任何安全性边界。

## 2. 体系结构

```
┌─ 宿主进程（allagent / -guimode / /web-ui）────────────────────────────────┐
│ PluginHost  ──  解析 web 清单 / 贡献快照 / plugin.<包名>.* 桥方法           │
│ PluginWebEvents  ──  插件后端 → 浏览器界面的事件通道                       │
│ WebUiServer  :A  ◄── 应用页（origin A）+ /bridge WebSocket                 │
│ PluginAssetServer  :B  ◄── 插件资产源（origin B）+ /__sdk 脚本 + /p/资产   │
└──────────────┬────────────────────────────────────┬───────────────────────┘
       (origin A)│ React 运行时                        │(origin B) 沙箱 iframe
   contributions store / pluginRuntime  ◄─postMessage─►  SDK（AllAgentPlugin）
```

关键点：

- **独立源**：插件资产由第二个 loopback 端口（`PluginAssetServer`）提供，与应用页不同源。iframe 由此既不能触碰宿主 DOM，也无法直连 `/bridge`——`WebUiServer` 的 Origin 校验会拒绝其他来源（403）。
- **命名空间隔离**：插件界面只能调用 `plugin.<自己的包名>.*`（由插件后端注册）与已声明能力对应的 `host.*`；其他插件、任意宿主方法都不可达。
- **失败隔离**：插件 Web 清单校验失败只禁用该插件的 Web 部分（插件本体照常工作）；帧握手超时/崩溃只显示错误卡，不影响宿主。

## 3. 快速开始（以样例插件为例）

仓库中的 `samples/AllAgent.NET.SamplePlugin` 展示了全部现状能力：页面「示例仪表盘」、`example_clock` 的自定义工具卡、命令面板入口、后端桥方法与事件推送。

### 3.1 清单：`allagent-plugin.json`

```json
{
  "sdkVersion": 1,
  "assembly": "AllAgent.NET.SamplePlugin.dll",
  "entryType": "AllAgent.NET.SamplePlugin.ExamplePlugin",
  "web": {
    "sdkVersion": 1,
    "entry": "web/dashboard.html",
    "capabilities": ["notifications", "storage"],
    "contributes": {
      "pages": [
        { "id": "dashboard", "title": "示例仪表盘", "icon": "gauge", "entry": "web/dashboard.html", "order": 40 }
      ],
      "toolCards": [
        { "tool": "example_clock", "entry": "web/tool-card.html" }
      ],
      "commands": [
        { "id": "open-dashboard", "title": "打开示例仪表盘", "page": "dashboard" }
      ]
    }
  }
}
```

### 3.2 页面：`web/dashboard.html`（片段）

```html
<script src="/__sdk/allagent-plugin-sdk.js"></script>
<script>
  const sdk = AllAgentPlugin.connect()
  sdk.on('tick', data => updateClock(data.utc))          // 插件后端推送
  sdk.ready.then(async () => {
    sdk.autoResize()
    const result = await sdk.invoke('history', { limit: 12 })   // 自己的桥方法
  })
</script>
```

### 3.3 后端：桥方法与推送

```csharp
public void Register(PluginContext context)
{
    // 暴露给本插件界面：plugin.allagent.net.sampleplugin.history
    context.AddWebBridge<ClockHistoryQuery?, ClockHistoryResult>("history", (query, ct) =>
        tools.ReadHistoryAsync(Math.Clamp(query?.Limit ?? 20, 1, 100), ct));

    // 推送事件：界面 sdk.on('tick', …) 即可收到
    context.Web.Push("tick", new { utc = DateTimeOffset.UtcNow });
}
```

### 3.4 打包

```xml
<None Include="web\**\*" Pack="true" PackagePath="allagent/web/" />
```

包内 `allagent/web/**` 会被原样解到安装目录的 `web/` 下（`allagent/allagent-plugin.json` 同规则）。`dotnet pack` 后通过 `/plugins import` 或插件商店安装，启用即生效。

## 4. manifest `web` 块参考

| 字段 | 类型 | 说明 |
|---|---|---|
| `sdkVersion` | int | Web 扩展契约版本，当前为 `1`。高于宿主支持版本时**只禁用 Web 部分**并在插件页给出原因。 |
| `entry` | string? | 默认入口文件（相对安装目录，必须位于 `web/` 内）。可选。 |
| `capabilities` | string[] | 声明需要的宿主能力（见 §7）。未声明的能力调用会返回 `capability_denied`。 |
| `devUrl` | string? | 本地开发服务器地址（仅 loopback http 有效，且宿主以 `ALLAGENT_PLUGIN_DEV=1` 启动时才生效，见 §11）。 |
| `contributes.pages[]` | object[] | 导航页：`{ id, title, icon?, entry, order? }`。`icon` 取内置图标名（`gauge`、`chart`、`dashboard`、`activity`、`sparkles`、`list`、`globe`、`plug`、`terminal`、`wrench`、`rocket`、`star`、`package`、`blocks`、`wand`、`puzzle`），未知名称回退拼图图标。 |
| `contributes.panels[]` | object[] | 顶部工具栏弹窗：`{ id, title, icon?, entry, order? }`。宿主在右上工具栏渲染图标按钮，点击打开可拖动、缩放的浮动沙箱面板；不占用侧栏导航位。 |
| `contributes.toolCards[]` | object[] | 自定义工具卡：`{ tool, entry }`。`tool` 为工具名；命中后该工具调用在对话轨迹中由插件自绘渲染。 |
| `contributes.commands[]` | object[] | 命令面板入口：`{ id, title, page?, panel? }`，指向本插件的页面 id 或弹窗 id，点击即打开。 |
| `contributes.settings[]` | object[] | 通用设置区：`{ id, title, fields?, entry? }`。宿主 Settings 页渲染原生字段；`entry` 可再嵌一个插件帧显示状态/操作。字段 `key` 仅允许小写字母、数字、`_ - .`。 |
| `contributes.composerActions / statusBar` | object[] | **清单已预留**（宿主会保留字段并原样下发），当前版本暂不渲染；后续版本开放。 |

校验规则（加载时执行，失败即整体禁用该插件的 Web 部分并显示原因）：

1. 所有 `entry` 必须位于安装目录的 `web/` 子目录内、且文件真实存在；`..`、绝对路径、盘符一律拒绝。
2. 插件的页面 / 面板 / 命令 / 设置区 / 动作 / 状态栏项 id 以及 `AddWebBridge` 方法名必须在插件内唯一、且仅含字母数字与 `_ - .`。
3. `web.sdkVersion` 高于宿主 → Web 部分禁用（插件本体与其余贡献不受影响）。

> 兼容性：旧宿主会忽略 `web` 块，插件其余能力正常；新宿主对没有 `web` 块的插件（如内置 CommandCode）零影响。

## 5. 桥方法与事件（插件后端 ⇄ 插件界面）

### 5.1 注册桥方法

```csharp
// 强类型重载：参数按 camelCase 反序列化，返回值自动序列化
context.AddWebBridge<MyRequest?, MyResponse>("update", async (request, ct) => new MyResponse(...));

// 原始 JSON 重载：直接操作 JsonElement
context.AddWebBridge("raw", (payload, ct) => Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(new { ok = true })));
```

- 宿主注册的完整方法名为 `plugin.<包名小写>.<方法名>`，例如 `plugin.allagent.net.sampleplugin.history`。
- 方法随插件「启用 / 禁用」和「Web 界面开关」即时装载、卸载——禁用后界面侧调用会得到「Unknown method」。
- 仅本插件自己的帧可以调用；跨插件与宿主方法不可达。

### 5.2 推送事件

```csharp
context.Web.Push("tick", new { utc = DateTimeOffset.UtcNow });
```

- 广播为桥事件 `plugin.<包名小写>.<事件名>`，仅转发给**订阅过的**插件帧。
- 界面侧：`sdk.on('tick', data => …)`，SDK 自动发送 `subscribe/unsubscribe`。
- Web UI 未运行时推送是 no-op（不缓存、不重放）。

### 5.3 宿主事件订阅

除插件自己的事件外，帧还可以订阅宿主事件：

| 事件名 | 数据 | 说明 |
|---|---|---|
| `theme` | `{ mode, vars }` | 主题或宿主配色变化 |
| `context` | `{ workspace, conversationId }` | 工作区或当前会话变化 |
| `conversation.changed` | `{ workspace, conversationId }` | 当前会话切换（需订阅） |
| `workspace.changed` | `{ workspace, dataDirectory }` | 宿主切换工作区（需订阅） |
| `workspace.folderPicked` | `{ path, error }` | 原生目录选择结果（`host.pickFolder` 之后，需订阅） |
| `tool.update` | 见 §6 工具卡 | 工具卡帧专用，宿主自动推送 |

## 6. 自定义工具卡

`contributes.toolCards` 命中后，该工具调用在轨迹中不再是通用 JSON 卡片，而是插件自绘的帧：

- 挂载参数：`sdk.view = { kind: "toolcard", id: <调用ID>, params: { name: <工具名> } }`。
- 数据推送：宿主把调用状态节流（100ms）推为 `tool.update` 事件：

```ts
sdk.on('tool.update', call => {
  // call: { id, name, status: 'running'|'done'|'error'|'cancelled', args, output, note, rejected }
})
```

- `args` / `output` 由宿主裁剪（各 2 万 / 1.6 万字符上限），`output` 为可读文本结果。
- 帧高度自适应（`sdk.autoResize()` 或 `sdk.resize(h)`），宿主限制在 140–420 dpi 像素区间内；帧失败时回退通用卡片，不影响对话。

### 6.1 声明式工具卡（TUI + GUI）

插件工具可以在 `ToolResult` 上附带一个平台无关的卡片，TUI 和 GUI 都会原生渲染，不需要 HTML：

```csharp
return ToolResult.Success(data) with
{
    Card = new AgentToolCard(
        Badge: "CODEGRAPH",
        Title: "query",
        Summary: "12.4k 字符",
        Body: "code",              // text | code | paths | search | json
        Text: preview,
        Fields: [new("工作区", "LanAgent"), new("截断", "否", "muted")])
};
```

- GUI 的 `describeToolCard` 会优先读取结果根级的 `card`，映射到现有卡片布局；TUI 的 `ToolCardPresentation` 使用同一份数据。
- 卡片字段有硬上限（title 200、summary 500、text 4000、fields 8×200），`ToolResultBudget` 会统一裁剪。
- 复杂交互仍可使用 `contributes.toolCards` 的自绘帧；简单卡片优先用 `ToolResult.Card`，两端一致且没有静态资产。

### 6.2 通用设置区

`contributes.settings[]` 由宿主 Settings 页通用渲染：

```json
"settings": [{
  "id": "codegraph",
  "title": "CodeGraph",
  "fields": [
    { "key": "path", "label": "可执行文件路径", "type": "text" },
    { "key": "autosync", "label": "自动同步", "type": "boolean", "default": "true" }
  ],
  "entry": "web/settings.html"
}]
```

- `fields` 支持 `text / number / boolean / select / password`；值保存在 `<shared>/plugins/settings.json`，通过宿主桥 `web.settings.get` / `web.settings.set` 读写（宿主只允许写清单声明的 key）。
- `entry` 在字段下方渲染一个 `view.kind = "settings"` 的沙箱帧，适合状态、按钮和长任务轮询。帧仍只能调用自己的 `plugin.<包名>.*` 与已声明能力。
- 插件后端可直接用 `PluginContext.Settings.Get/Set` 读写同一个 store。

## 7. 宿主能力（capabilities）与 `host.*` API

在清单里声明后，界面通过 `sdk.invoke('host.…')` 使用。所有能力默认关闭（deny by default），未声明即拒绝。

| capability | API | 参数 → 返回 |
|---|---|---|
| `notifications` | `host.notify` | `{ title, message?, appearance?: 'default'\|'success'\|'info'\|'warning'\|'destructive' }` → `true` |
| `storage` | `host.storage.get` / `set` / `remove` | `{ key }` → `string \| null`；`{ key, value }` → `true`（单值 ≤ 256 KB，按插件命名空间隔离） |
| `storage` | `host.storage.keys` | `()` → `string[]`（本插件的键） |
| `clipboard.write` | `host.clipboard.writeText` | `{ text }` → `true` |
| `conversation.read` | `host.conversation.active` | `()` → `{ id, title, modelId, providerId, messageCount } \| null` |
| `conversation.read` | `host.conversation.messages` | `{ limit? }` → 最近的 `{ id, role, content, createdAt }[]`（limit ≤ 200，正文单条裁剪 8000 字符） |
| `workspace.read` | `host.workspace.info` | `()` → `{ workspace, dataDirectory, sharedDirectory }` |
| `workspace.read` | `host.workspace.files` | `{ query?, limit? }` → 工作区文件列表（`@` 补全同源） |
| `workspace.pick` | `host.pickFolder` | `()` → `true`（打开宿主原生目录选择器；结果以 `workspace.folderPicked` 事件返回 `{ path, error }`） |
| `markdown` | `host.markdown.render` | `{ text }` → 宿主渲染并净化的 GFM HTML |
| `navigation` | `host.navigate` | `{ page }` → `true`（内置页 `chat`/`plugins`/… 或插件路由 `plugin:<包名>:<页面id>`） |
| `navigation` | `host.openConversation` | `{ id }` → `true` |
| `composer.write` | `host.composer.insertText` | `{ text }` → `true`（填入输入框草稿并切换到对话页） |

错误返回 `PluginClientError`，`code` 可能为：`capability_denied`、`denied`（命名空间不允许）、`unknown_method`、`invalid_params`、`failed`。

## 8. 帧 SDK 参考

### 8.1 引入

```html
<!-- 宿主自带，无需安装：由插件资产源提供 -->
<script src="/__sdk/allagent-plugin-sdk.js"></script>
<script>
  const sdk = AllAgentPlugin.connect()
</script>
```

也提供 ES 模块版本 `/__sdk/allagent-plugin-sdk.mjs`（`import { connect } from '…/allagent-plugin-sdk.mjs'`）。

### 8.2 客户端接口

```ts
const sdk = AllAgentPlugin.connect()

sdk.ready                 // Promise<void>：握手完成（15 秒超时 → handshake_timeout）
sdk.plugin                // { packageId, name, version }
sdk.view                  // { kind: 'page' | 'toolcard' | 'panel' | 'settings', id, params? }
sdk.capabilities          // string[]
sdk.theme                 // { mode: 'light' | 'dark', vars: Record<string,string> }
sdk.context               // { workspace?, conversationId?, … }

await sdk.invoke('history', { limit: 5 })        // plugin.<包名>.history
await sdk.invoke('host.notify', { title: '嗨' }) // 能力 API
const off = sdk.on('tick', data => …)            // 订阅；返回取消函数
sdk.resize(320)                                  // 上报内容高度
const stop = sdk.autoResize()                    // ResizeObserver 自动高度
sdk.openExternal('https://example.com')          // 在新窗口打开（best-effort）
```

### 8.3 主题同步

宿主把当前主题落到帧根节点：`data-theme="dark|light"`，以及 `--ag-*` 变量。插件样式直接用变量即可获得宿主观感，并自动跟随明暗切换：

```css
body { background: var(--ag-background); color: var(--ag-foreground); }
.brand { color: var(--ag-brand); }
```

可用变量：`--ag-background`、`--ag-foreground`、`--ag-surface`、`--ag-muted`、`--ag-muted-foreground`、`--ag-border`、`--ag-accent`、`--ag-brand`、`--ag-destructive`、`--ag-ring-color`、`--ag-sidebar`。

## 9. 资产服务与安全模型

| 路由 | 说明 |
|---|---|
| `GET /__sdk/allagent-plugin-sdk.js \| .mjs` | 宿主自带 SDK（固定白名单文件）。 |
| `GET /p/<packageId>/<web 内相对路径>` | 插件资产；仅「已启用 + Web 开关打开 + 清单有效」的插件可访问。 |

安全约束（逐条实现）：

1. **loopback-only**：只有 `127.0.0.1 / localhost / ::1` 的 Host 头会被服务；其他 Host 一律拒绝（DNS-rebinding 防护）。
2. **路径围栏**：只服务安装目录 `web/` 内的文件；`GetFullPath` 归一化后越界即 403。
3. **独立源隔离**：iframe 与应用页不同源；`sandbox` 属性限定为 `allow-scripts allow-same-origin allow-forms allow-popups allow-popups-to-escape-sandbox allow-downloads`。应用页的 `/bridge` WebSocket 校验 Origin，插件帧直连会被 403。
4. **握手校验**：宿主为每个帧生成一次性 token，随 URL 下发；只有 `event.source === 帧窗口`、Origin 匹配、token 正确的 `hello` 才能建立会话，之后每条消息都校验来源与 Origin。
5. **能力白名单 + 命名空间**：帧只能调用 `plugin.<自己包名>.*` 与已声明能力的 `host.*`；其余返回 `denied` / `capability_denied`。
6. **响应头**：`X-Content-Type-Options: nosniff`；`.html/.js/.mjs/.css/.json` 使用 `no-cache`（避免升级后新旧界面混杂），其余静态资源 `max-age=3600`。
7. **失败隔离**：帧 15 秒内未完成握手 → 错误卡 + 重试；帧崩溃不影响宿主（独立浏览上下文）。
8. **止损开关**：插件页「Web 界面」开关（`web.setEnabled`）可只关闭界面而保留插件其余能力；禁用整个插件会同时移除页面、工具卡、命令与桥方法。

> 信任边界说明：插件 DLL 本身以当前用户权限运行（与既有插件模型一致），iframe 隔离保护的是**宿主界面的完整性与数据访问面**，不是对插件后端的沙箱。

## 10. 宿主提供的观察点

- **贡献快照**：桥方法 `web.contributions` 返回 `{ sdkVersion, assetBaseUrl, plugins[] }`，前端 shell 据此渲染导航、工具卡与命令；插件装卸/启停/开关变化时推送 `web.contributions.changed`。
- **软开关**：桥方法 `web.setEnabled { packageId, enabled }`（默认 `true`，随插件启用自动生效，持久化在 `installed.json` 的 `webEnabled` 字段）。
- **插件设置**：桥方法 `web.settings.get { packageId }` → `{ packageId, values }`；`web.settings.set { packageId, key, value }`。宿主只接受清单 `contributes.settings[].fields` 声明过的 key。
- **终端面板**：`/plugins` 列表会显示每个插件的 `Web n 界面 / m 工具卡`，Web 清单异常时标注「Web 异常」。
- **插件页（Web UI）**：每个带 `web` 块的插件卡片显示页面/工具卡数量、能力清单、错误原因与开关。

## 11. 开发与调试

### 11.1 迭代闭环

1. 后端改动：`dotnet build` → 在插件页「禁用 → 启用」重新加载 DLL（无需重启宿主）。
2. 前端改动（无构建流程的 HTML/CSS/JS）：重新打包（`dotnet pack`）→ 重新导入安装，或在开发期使用 `devUrl`（见下）。
3. `ALLAGENT_PLUGIN_DEV=1` + `web.devUrl`：清单声明本地开发服务器（仅 `http://127.0.0.1:<port>` 会被接受）：

```json
"web": { "sdkVersion": 1, "devUrl": "http://127.0.0.1:5174", "contributes": { "pages": [ … ] } }
```

帧将直接指向该地址（带 `?token&parent&theme` 参数），Vite/任意 dev server 的 HMR 可正常工作；宿主重启后生效（devUrl 在插件加载时解析）。**发布前删除 `devUrl`。**

> 提示：宿主的 Vite 预览（`npm run dev`，无后端时展示演示插件界面）依赖 `import.meta.env.DEV`。如果 shell 里全局设置了 `NODE_ENV=production`，预览会走 WebSocket 模式并一直显示「连接中」，用 `NODE_ENV=development` 启动即可恢复演示数据。

### 11.2 日志与观测

- 资产服务日志：`%APPDATA%/AllAgent.NET/allagent.log`（`[PluginAssetServer] …`）。
- 桥调用日志：同文件 `[BridgeDispatcher] OK/ERR <method>`。
- 帧内 `console.error` 可在浏览器开发者工具（`?bridge=` 打开的页面）或 GUI 窗口的 DevTools（`dotnet run … -- gui --gui-url …` + `--devtools`）中查看。

### 11.3 故障排查

| 现象 | 原因与处理 |
|---|---|
| 插件页显示「Web 扩展未生效」+ 原因 | 清单校验失败：入口文件不存在 / 越界 / id 冲突 / `web.sdkVersion` 过高。按提示修正后重新打包安装。 |
| 页面显示「插件界面加载失败」 | 帧 15 秒未握手：SDK 未引入（检查 `<script src="/__sdk/allagent-plugin-sdk.js">`）、页面脚本报错，或资产服务未启动。可先点「重试」。 |
| `capability_denied` | 清单 `capabilities` 未声明对应能力；加上后重新加载插件。 |
| `denied` | 调用了非本插件命名空间的方法（如其他插件或宿主方法）。 |
| 调用返回 Unknown method | 桥方法未注册或插件/Web 开关被关闭；检查 `AddWebBridge` 名称与开关状态。 |
| 工具卡仍是通用卡片 | `toolCards[].tool` 与工具名不一致，或插件未启用/Web 开关关闭。 |
| 页面样式发灰 / 主题不同步 | 使用 `--ag-*` 变量并监听 `sdk.on('theme')`，不要在初始化时硬编码颜色。 |

## 12. 常见问题

**可以用 React/Vue/Svelte 吗？** 可以。iframe 是普通浏览器环境，任何框架、任何构建器都行；打包产物放进 `web/` 即可。需要 SDK 时：无构建项目用 `<script src="/__sdk/allagent-plugin-sdk.js">`；有构建项目可引入 `.mjs` 版本（`import { connect } from '/__sdk/allagent-plugin-sdk.mjs'` 需要相对资产源的绝对路径，或在你的构建里把 SDK 源码内联）。

**插件界面能改宿主全局样式吗？** 不能。iframe 与宿主不同源，样式天然隔离；列表、卡片等“宿主风格”请用 `--ag-*` 变量贴近，或使用声明式贡献。

**页面路由是什么？** 插件页面的 hash 路由为 `plugin:<包名小写>:<页面id>`；`host.navigate` 与命令贡献都使用同一格式。`panels` 弹窗不占路由，由右上工具栏按钮或命令面板打开，关闭后保留上次位置与尺寸。

**能不能覆盖宿主已有界面？** 当前开放的是「新增页面/命令 + 覆盖工具卡片渲染」；对话、设置等核心视图不可替换。`web.mode:"native"`（第三档）在规划中，将提供与宿主组件库的原生互操作。

**Web UI 没开时插件会怎样？** 后端桥方法不装载、推送为 no-op；插件其余能力（工具、命令、提供商、Hooks）不受影响。GUI 窗口与浏览器 `/web-ui` 都通过终端宿主提供资产，行为一致。

**旧插件/旧宿主的兼容性？** 旧宿主忽略 `web` 块；新宿主对无 `web` 块的插件零影响；`web` 块校验失败只禁用 Web 部分。

## 13. 参考实现

- 后端：`samples/AllAgent.NET.SamplePlugin/ExamplePlugin.cs`（`AddWebBridge` / `context.Web.Push` / 工具历史）
- 清单：`samples/AllAgent.NET.SamplePlugin/allagent-plugin.json`
- 页面与工具卡：`samples/AllAgent.NET.SamplePlugin/web/dashboard.html`、`web/tool-card.html`、`web/style.css`
- 宿主实现：`src/AllAgent.NET.Core/Extensions/PluginWeb.cs`、`src/AllAgent.NET.Core/Bridge/PluginAssetServer.cs`、`src/AllAgent.NET.Terminal/WebUiSession.cs`（`web.*` 桥方法）
- 前端运行时：`src/AllAgent.NET.Web/src/lib/pluginRuntime.ts`、`src/components/plugins/*`、SDK 源码 `src/AllAgent.NET.Web/src/plugin-sdk/`

## 14. 在插件里复用 Iconiq UI

宿主提供可选的本地组件模块 `/__sdk/allagent-plugin-ui.mjs`（无需声明新能力）。它导出同一份 React 运行时、`createRoot`、仓库现有的 `Button`、`Input`、`Badge` 以及 Dialog / Tabs / Select 组件家族；后者保留各自源文件的命名导出。只在插件需要 UI 时加载。模块不包含宿主 store 或内部 bridge 访问权。

React 插件示例（无需构建即可使用 `createElement`；若使用 JSX，构建时将此 SDK URL 作为 external，并确保 React 来自同一入口）：

```html
<div id="app"></div>
<script type="module">
  import { React, createRoot, Button, installStyles } from '/__sdk/allagent-plugin-ui.mjs'
  import { connect } from '/__sdk/allagent-plugin-sdk.mjs'
  const sdk = connect()
  await sdk.ready
  const stopThemeSync = installStyles() // 注入控件样式，并跟随 SDK 的 data-theme
  createRoot(document.getElementById('app')).render(
    React.createElement(Button, { onClick: () => sdk.invoke('refresh') }, '刷新')
  )
  // 单页应用卸载时调用 stopThemeSync()，释放主题观察器。
</script>
```

框架无关 HTML 插件可使用按钮适配层，内部同样渲染真实 Iconiq Button，样式隔离于 Shadow DOM，跟随主题、支持键盘和禁用状态，以及外层表单提交：

```html
<ag-button variant="outline" id="refresh">刷新</ag-button>
<ag-button type="submit" class="primary">启动流程</ag-button>
<script type="module">
  import { registerElements } from '/__sdk/allagent-plugin-ui.mjs'
  registerElements()
  document.getElementById('refresh').addEventListener('click', () => console.log('refresh'))
</script>
```

适配器默认 `type="button"`，`variant` 支持 default / outline / ghost / secondary / destructive / link，支持 `disabled` 属性和同名 JS 属性、`aria-label` 与 `size="sm|default"`。设置 `textContent` 更新文案。`class="primary"` 在未指定 variant 时选用 default。

Iconiq 样式源码仍在 `src/components/ui/`；共享包由 `vite.plugin-ui.config.ts` 生成，`npm run build` 包含此步骤。所有资源通过本机插件资产源提供。旧版不带此模块的宿主需要升级。

## 15. 在指定工作区启动 Agent

宿主提供 `PluginWorkspaceTasks` 服务，插件通过 `context.GetService<PluginWorkspaceTasks>()` 取得。请在用户选择目标工作区之后调用；服务复用宿主会话、存储、审批、后台生命周期与流日志，不自行打开另一份 DataStore。

```csharp
var tasks = context.GetService<PluginWorkspaceTasks>();
var workspaces = tasks.List();
var started = await tasks.StartAsync(new PluginTaskRequest(
    workspacePath, "任务标题", "包含明确工作流程的完整提示词"), ct);
// started: Workspace、ConversationId、StreamId
// Prompt 只发送给模型；会话显示 Title。可选 Message 参数可提供独立的公开任务摘要。
```

工作区必须已存在，模型选择优先使用终端记忆配置，否则使用已启用配置的默认模型。TUI 的操作等待克隆完成；GUI 插件应将长操作放到自己的后台任务中，返回句柄并轮询，避免超出 bridge 的一分钟请求期限。GitHub 插件演示了这一模式及 Issue / PR 两套流程提示词。

普通 `AddTools` 注册的工具可能捕获当前工作区的存储，仅在原工作区启用。对于只使用共享账号或远程服务、完全不捕获本地工作区状态的工具，可显式使用 `AddWorkspaceIndependentTools` 注册；宿主会把此子集同时提供给新工作区的 Agent。GitHub 的只读上下文及按 SHA 读取文件工具采用此方式。

`host.openConversation` 现在接受可选的 `workspace`。相同工作区仍只需 `navigation`；跨工作区跳转还须声明 `workspace.switch`，宿主先切换，再加载并打开会话：

```js
await sdk.invoke('host.openConversation', {
  id: started.conversationId,
  workspace: started.workspace,
})
```

