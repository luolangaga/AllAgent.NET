# 按需披露的宿主工具（progressive disclosure）

宿主工具的"注册"和"披露"是两件事：

- **注册**：工具常驻 `ToolRegistry`，调用能被解析、审批、审计，缺失时能给出可操作错误。
- **披露**：模型在系统提示的 `## Host tool catalog` 里看到的名称和 JSON Schema。

`ToolSurface`（`src/AllAgent.NET.Core/Tools/ToolSurface.cs`）把披露做成按需：家族工具默认隐藏，只在对应技能被 `skill_read` 加载后进入目录。computer_*（12 个）和 browser 桥多年常驻注册，但 schema 只在需要时出现。

## 生命周期

| 阶段 | 目录（系统提示） | 调用 |
| --- | --- | --- |
| 未加载技能 | 不出现 schema，只有 `## On-demand capabilities` 一行指针（技能名、工具数、前几个名字） | 被拒绝，错误里点名要 `skill_read` 哪个技能 |
| `skill_read` 成功 | 结果多出 `activatedTools` 和 `toolSurface`，说明解锁了哪些工具 | 同一段 Python 里后续调用即可执行 |
| 之后 | schema 进入目录；同一轮内 `AgentEngine` 会重写契约块，下一轮同样是新目录 | 一直可用 |
| 技能被禁用 / 删除 | 家族重新隐藏，且不再有任何技能能披露它 | 拒绝（"no enabled skill …"），不会靠猜参数硬跑 |

激活状态按**会话**保存：`AgentService` 为每个 conversation 保留一个 `ToolSurface`（`SurfaceFor(conversationId)`），所以每轮重建的 `AgentRuntime` 不会丢掉披露；子代理有自己的 surface，不继承父会话的激活。终端聊天复用同一个 runtime，行为相同。`/clear` 换新会话即重置。

## 技能如何声明

`SKILL.md` frontmatter 增加可选字段：

```yaml
---
name: computer-use
description: ...
tools: [computer_*]     # 精确名或末尾 * 的前缀
---
```

- 只允许声明工具名/前缀（`^[a-z][a-z0-9_.:*-]{0,63}$`，最多 32 项），不能声明命令或模板；技能仍然只是说明文本，不能执行任何东西。
- 多项技能声明同一个工具时，先声明者拥有；顺序按技能名排序，结果稳定。
- 老安装的 `computer-use/SKILL.md` 没有该字段也能用：`ToolSurface.Discover` 对 `computer_*` 和 `browser` 有内置回退，只要技能启用就能加载。
- 内置 `browser-use` 的声明写在代码里（`SkillCatalog.Discover`），因为它是 `builtin://` 技能而不是磁盘文件。

## 不变量

- **披露不是授权**。风险等级、逐次审批、只读角色策略、`HostToolsProvider` 白名单和扩展作用域都照旧生效，顺序是"角色白名单 → surface"，角色永远只能收窄，不能被披露放宽。
- **沙箱不变**：传给 Hyperlight 的 `SandboxTools` 始终是全量注册表，只让提示里的目录随披露变化，否则 `_toolsFingerprint` 变化会重启 microVM。
- **缓存**：目录 JSON 按 `registry.Version + surface.Version + 可见工具名` 缓存；激活只失效一次提示前缀，重复加载同一技能不再改写。
- **可观测**：`/tools` 增加"披露"列（`always` / `按需 <技能>` / `已加载 <技能>`），`CodeActProvider.Surface` 与 `AgentService.SurfaceFor` 供宿主或桥读取。

## 代码位置

| 位置 | 作用 |
| --- | --- |
| `Core/Tools/ToolSurface.cs` | 能力声明、前缀匹配、激活状态与版本号 |
| `Core/CodeAct/CodeActProvider.cs` | 可见性过滤、`## On-demand capabilities`、调用闸门、`skill_read` 后披露 |
| `Core/Services/SkillCatalog.cs` | frontmatter `tools:` 解析与内置声明 |
| `Core/Runtime/AgentRuntime.cs` | 建面（`Discover`）与 `RefreshToolSurface()` |
| `Core/Services/AgentService.cs` | 会话级 `SurfaceFor`，父子会话隔离 |
| `Core/Services/AgentEngine.cs` | 披露后同轮刷新契约块 |
| `Terminal/TerminalApp.cs` | `/tools` 披露列 |
