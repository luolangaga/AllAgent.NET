# AllAgent.NET 问题与限制清单

> 元信息：源码版本 `1.0.6`；检查日期 **2026-10-15**；机器 **LAPTOP-CE9T4R0L / Windows 10.0.26200**。
> 方法：源码阅读 + 本机实测。标注【实测】的条目由本机命令输出直接支持；标注【读码】的是静态结论，未在运行中触发。
> 配套文档：[优势总览](allagent-advantages.zh-CN.md)。级别定义：**P0** 阻断可用性 / **P1** 影响正确性或安全认知 / **P2** 能力与资源边界 / **P3** 体验与文档。

## 后续修复记录（2026-10-16）

下文保留原检查结论与证据；当前源码已做这些调整：

- 默认输入改为指导当前任务，在模型步骤边界送达；`/queue add 任务说明` 显式排队。
- 思考与代码预览的行数和展开提示固定靠右，按终端字符宽度补齐。
- 活动内容按 20 fps 构造，输入立即刷新；差异输出跳过未变化的行首、行尾、侧栏和填充。
- 80×24 授权布局缩小输入区，授权快捷键提示不再折行挤走历史。
- README 与执行文档统一为默认完全访问，`--ask` 开启逐次授权。
- 保留完整运行环境门控，失败消息分别报告 Python / PowerShell 状态及检查详情。
- 非 UTF-8 编辑失败增加转换副本后核对内容的指引，未加入自动编码猜测。
- 项目切换的真实终端测试进入禁并行集合；另修复两个终端共用数据目录首次启动时争抢内置技能目录的竞争：临时文件写完后原子发布，保留已有用户内容。

验证：本轮选择的输入、思考、绘制、授权布局、技能初始化及真实终端项目切换检查有 72 项通过。扩大检查中仍有 5 项原有失败：`ExecutionExperienceTests` 的工具卡片颜色/标题预期（3 项）及默认权限预期（1 项），以及 `TerminalProductTests.SkillsImportPreservesMetadataReferencesAndRuntimeDisabling` 对关闭模型调用后的引用读取预期（1 项）。前四项在撤回本轮布局/预览修改后仍可复现；最后一项触及本轮未修改的 `ReadReference` 权限检查。

原生滚动说明：当前聊天是应用管理的全屏历史视口。复用 Windows Terminal 的历史回滚需要改为向主缓冲区持续追加输出，重新设计固定输入区、侧栏和详情交互。本轮优化现有绘制路径，未改为原生历史回滚。跨平台与 Computer Use 能力扩展不在本轮范围。

## 一句话结论

AllAgent.NET 的"适合 Windows"是**用放弃跨平台换来的**：非 Windows 连启动前置检查都过不去；同时存在一条**默认权限与文档表述不一致**的沟通风险，以及若干已被实测复现的小窗口布局与测试稳定性问题。

---

## 问题清单

| 级别 | 问题 | 证据 | 影响 |
|---|---|---|---|
| P0 | 非 Windows 平台无法启动执行链路 | `EnvironmentDoctor.cs:10/28/49/58/73/107` → `AgentRuntime.cs:42` → `SystemTools.cs:41`【读码】 | 迁移/审核/CI 被锁定在 Windows |
| P0 | `run_shell` 被**宿主 Python** 检查门控 | `SystemTools.cs:41`："Python and PowerShell 7.6 must pass environment checks first."【读码】 | 只装 PowerShell 也无法执行命令；Python 故障连带 Shell 不可用 |
| P1 | 代码默认完全访问，但文档说"默认逐次授权" | `AgentOptions.cs:38` `FullAccess = true`、`TerminalArguments.cs:65` `FullAccess = !Flags.Contains("ask")` vs `README.md:155`、`docs/execution.zh-CN.md:19,23`；而 `README.md:42`、`docs/codeact-delivery-review.md:11` 与代码一致 | 用户可能误以为默认有审批保护 |
| P1 | 80×24 小窗口下历史记录被挤出视口 | 【实测】`ExternalDirectoryTests.PermissionBubbleKeepsTranscriptInputAndContextVisible(80,24)` 失败，`120,35` 通过 | 小窗口/窄终端体验与回归防护缺失 |
| P1 | 真实终端测试并行时不稳定 | 【实测】`WorkspaceNavigationTests.RealTerminalSwitchesProjectsWithoutChangingAnotherTerminalOrOldHistory` 并行整跑失败、单跑 2/2 通过（30 s） | CI 结果可信度下降 |
| P1 | 截图仅 PrintWindow，最小化/GPU 窗口拿不到画面 | `WindowScreenshot.cs`（拒绝 ≤0 尺寸、标注 GPU 可能空白）；【实测】`computer_windows` 中多个窗口 `bounds.isEmpty = true`（0×0） | 最小化窗口需先恢复（但恢复=动作会触发提示层并改变桌面） |
| P1 | 无 OCR / 坐标动作 / Windows Graphics Capture | `docs/computer-use.zh-CN.md`："尚未接入 Windows Graphics Capture、OCR 或坐标动作"【文档】 | 自绘、Canvas、游戏类界面基本无能 |
| P1 | 动作副作用不可撤销 | `docs/execution.zh-CN.md`、`docs/computer-use.zh-CN.md`：Shell/MCP/外部路径/电脑操作不在工作区检查点内 | 误点/误改只能人工恢复 |
| P1 | UIA Invoke 仍可能让目标应用自行弹窗或抢焦点 | `docs/computer-use.zh-CN.md`（后台模式的已知副作用） | 后台模式并非绝对"零打扰" |
| P2 | Shell 表达力受限：无管道、无重定向、无通配展开 | 工具说明 + `run_shell.ps1`（字面量数组、无 shell 解析） | 复杂流水线必须写成脚本文件或多次调用 |
| P2 | 无提权路径 | `docs/host-tools.zh-CN.md`：不要用 `Start-Process/RunAs`，提升权限需用户明确授权 | 需要管理员的操作无法自动完成 |
| P2 | `file_edit` 仅支持 UTF-8 文本 | `SystemTools.EditAsync`：非 UTF-8 返回"file_edit supports UTF-8 text only" | GBK/GB2312/UTF-16 文件（中文 Windows 常见）无法编辑 |
| P2 | 资源与预算上限 | `AgentOptions.cs:47`（full 600 s / ask 60 s）、`docs/execution.zh-CN.md:37`（20,000 / 64,000 字符）、图片 4 MB、单次粘贴 8 张 | 长时间构建/超大输出需落盘接力 |
| P2 | 依赖供应链偏预览版 | `README.md`：Hyperlight 相关依赖含预览版本，升级需重跑实际虚拟机测试；`AllAgent.NET.Sandbox.csproj` 引用 `Microsoft.Agents.AI.Hyperlight 1.24.0-preview.*` | 升级回归成本高 |
| P2 | 精确要求 PowerShell 7.6.x | `EnvironmentDoctor.cs:53-58`（`version.Major == 7 && version.Minor == 6`） | 7.4/7.5 一律判为缺失并触发安装流程 |
| P2 | Monty 兜底首次安装需要联网 | `docs/nuget-tool.zh-CN.md:11` | 离线机器两个沙箱都不可能的场景需人工准备 |
| P2 | 隔离模式需自备环境 | `docs/computer-use.zh-CN.md`：不创建 VM、不切桌面、不绕 UAC；需 .NET 8 Desktop Runtime + token 文件 + URL ACL + SSH 隧道 | "不打扰用户"的能力有部署门槛 |
| P2 | Worker 不支持 Session 0 | `ComputerUse/Program.cs`：`requires an interactive Windows desktop session` | 服务/计划任务方式不可用 |
| P2 | 测试资源占用与桌面干扰 | `tests/AllAgent.NET.Tests/HyperlightCollection.cs`（禁并行）、`ComputerUseOverlayTests` 会在所有显示器弹提示层 | 本机跑全量测试会打断用户（本次未运行该组测试） |
| P3 | 数据格式单向迁移 | README：v3 拆分会话格式，旧版本程序读不了新格式 | 回退旧版本不可行 |
| P3 | `allagent-projects.json` 写在**程序目录** | `Terminal/ProjectDataDirectory.cs` | Program Files 安装且无写权限时会失败或需改 `--data-dir` |
| P3 | 外部技能只扫描 `~/.agents/skills/` | README 明确不扫 `.codex/.claude/.config/opencode` | 已有技能目录需手工迁移 |
| P3 | 普通模式拒绝 UNC 工作区与 reparse point | `Tools/WorkspacePolicy.cs` | 网络盘/联接目录项目需完全访问模式 |
| P3 | 提示层本身是一种打扰 | `docs/computer-use.zh-CN.md`：全屏柔光边框 + 底部胶囊 | 用户忙碌时仍可见（设计取舍，非缺陷） |

---

## 分节说明

### 1. 平台阻断（P0，设计选择但代价明确）
非 Windows 的失败是**链条式**的，而不是某几个功能缺失：
1. `EnvironmentDoctor.CheckAsync` 只探测 `python.exe/python3.exe/py.exe`（`:28`）、只认 `pwsh.exe` 与 `%ProgramFiles%\PowerShell\7\pwsh.exe`（`:49`），因此 Linux/macOS 上即使装了 pwsh 7.6 也判为缺失（`:58`）；
2. 补装通道只有 winget（`:73`、`:107`）；
3. `AgentRuntime.CreateAsync` 在 `environment.Ready` 为 false 时直接抛异常（`AgentRuntime.cs:42`）；`run_shell` 亦直接失败（`SystemTools.cs:41`）；
4. 运行期还有硬拒绝：DPAPI 凭据（`OAuthAuthentication.cs:29,36`）、`Computer Use requires Windows.`（`ComputerUseTools.cs:18`）、`attach 需要 Windows 交互终端`（`TerminalSession.cs:27`）。

**结论**：可以直接把它定义为"Windows 专用工具"，但任何"跨平台"预期都需要在路线图上明确否定。

### 2. 门控耦合（P0/P1）
`run_shell` 的预检要求**宿主 Python 与 PowerShell 同时就绪**。这让一个纯 PowerShell 的能力被 Python 的可用性绑定：宿主 Python 版本不符（例如 3.9 或 3.15）时会连带禁用命令执行，而错误信息只有一句话，排障需要先看 `doctor`。
建议：把门控拆成"每个工具只依赖自己需要的运行时"，或在错误里分别列出 Python/PowerShell 的检查结果。

### 3. 默认权限与文档不一致（P1，需要优先修文档或改代码）
- 代码：`AgentOptions.cs:38` `FullAccess { get; init; } = true`；CLI：`TerminalArguments.cs:65` `FullAccess = !Flags.Contains("ask")` → **默认完全访问，`--ask` 才开审批**。
- 文档冲突：`README.md:42` 与 `docs/codeact-delivery-review.md:11` 与代码一致；但 `README.md:155`（"默认使用逐次授权模式"）与 `docs/execution.zh-CN.md:19,23`（"默认逐次授权 / 新终端默认逐次授权"）与代码相反。
- 风险：安全预期被抬高。真实默认是"宿主工具不再逐次审批 + 允许工作区外路径"（隔离沙箱仍然生效，且不授予管理员权限）。

### 4. 实测缺陷（P1）
**4.1 小窗口布局**
```
dotnet test tests/AllAgent.NET.Tests --filter FullyQualifiedName~ExternalDirectoryTests.PermissionBubbleKeepsTranscriptInputAndContextVisible
```
80×24 变体失败：断言 `Assert.Contains("上一条执行记录", console.Output)` 找不到，实际帧以 ` AllAgent.NET │ 开始新的对话 ` 开头；120×35 通过。即授权气泡+输入区+底栏占满 24 行后，历史记录被挤出视口。

**4.2 并行下的真实终端测试**
```
dotnet test tests/AllAgent.NET.Tests --filter "FullyQualifiedName~WorkspaceNavigationTests|FullyQualifiedName~SessionWorkspaceTests|FullyQualifiedName~ExternalDirectoryTests|FullyQualifiedName~FileSearchTests"
```
→ 28 项中 2 项失败（上述 80×24 + `WorkspaceNavigationTests...`）；单独重跑 WorkspaceNavigation 项 **2/2 通过（30 s）**。倾向并行/时序敏感。

### 5. Computer Use 的能力边界（P1/P2）
- 截图：PrintWindow 对 GPU/透明窗口可能返回空白（`WindowScreenshot.cs` 自己给出了这句 note）；【实测】本机部分窗口 `bounds.isEmpty = true`（最小化或无窗口矩形），这类目标无法截图；恢复窗口本身是需要用户可见动作。
- 视觉能力：无 WGC、无 OCR、无坐标点击（文档明确列出）。
- 语义操作：UIA Invoke 可能触发目标应用弹窗或切换焦点；最小化窗口可能不提供完整 UIA 树；自绘应用取决于其可访问性接口（`docs/computer-use.zh-CN.md`）。
- 真机前置：Session 0 不支持；isolated 模式要求用户自备独立交互桌面/VM + token 文件 + URL ACL + 隧道，功能本身不创建 VM、不切用户桌面、不绕 UAC。
- 托盘：图标属于 Explorer，不能靠激活隐藏主窗口替代托盘操作。

### 6. 副作用不可撤销（P1）
`/undo` 只覆盖**工作区内可快照的文件工具修改**。Shell 命令、MCP 调用、外部路径写入、Computer Use 的界面修改都被标记为外部行为，**明确不承诺撤销**。这意味着：把审批与沙箱做到位之后，"犯错成本"仍主要落在用户侧。

### 7. 自动化表达力缺口（P2）
- 没有管道/重定向/通配：`exe + args` 是字面量数组，`&`、`|`、`>` 都只是普通字符（这点同时也带来了安全与可预测性）。
- `pwsh` 以 `-NonInteractive` 启动（`SystemTools.cs`），交互式程序没有输入通道。
- 无提权：文档要求不要用 `Start-Process/RunAs/动态 --%`，需要管理员的操作必须由用户授权完成。
- 一次调用只能启动一个程序；多步骤要么多次 `run_shell`，要么写成脚本文件。

### 8. 依赖与供应链（P2）
- Hyperlight 依赖带预览版本，升级需重跑真实 microVM 测试（README）。
- 两个沙箱都不可用时明确报错、不降级为宿主 Python（`FallbackCodeSandbox.cs:51`）——安全上正确，但离线/无虚拟化环境的可用性下降。
- 项目目标框架 net8.0，但解决方案用 `.slnx`，需要 .NET 9+ SDK 才能构建（README 快速开始表）。
- `file_search` 依赖系统 `rg`；缺失时需 `doctor install`（随包的 Everything 引擎不依赖 rg，但默认不注册为工具；按名字查找现在同样走 rg `--files`）。

### 9. 数据与部署（P3）
- 项目索引 `allagent-projects.json` 在程序目录；若程序装在 Program Files 且无写权限，索引写入会失败（可用 `--data-dir` 规避，但报错信息不一定指向根因）。
- v3 格式单向：旧版本程序无法读取新格式。
- 插件/OAuth 凭据/用户技能/Python 公共库默认仍在 `%APPDATA%\AllAgent.NET`，只有显式 `--data-dir` 才整体迁移——多项目用户容易混淆两套根目录的边界。

### 10. 未验证 / 待观察
- Hyperlight microVM 的实际执行（未跑 `[Collection("Hyperlight acceptance")]` 测试）。
- isolated Computer Use（本机 `isolatedConfigured=false`）。
- winget 自动补装与 DPAPI 真实凭据读写（只用随机探针验证了 DPAPI 能力本身）。
- 完整模型对话链路（本机未见已配置模型，"请先添加模型配置"）。

---

## 建议修复优先级

| 优先级 | 建议 | 对应问题 |
|---|---|---|
| 1 | 统一默认权限表述：要么把默认改成 `ask`，要么修正 `README.md:155` 与 `docs/execution.zh-CN.md:19,23` | P1 文档冲突 |
| 2 | 修 80×24 布局：把授权气泡改为可折叠/滚动，或让历史区保底行数 | P1 实测失败 |
| 3 | 把 `run_shell` 门控与"宿主 Python 就绪"解耦，或错误信息分别列出检查项 | P0 门控耦合 |
| 4 | 截图按能力降级：PrintWindow 失败时给出"窗口最小化/需恢复"的明确提示，或后续接入 WGC | P1 截图上限 |
| 5 | 把真实终端/UI 测试标为需交互、串行集合，并在 CI 中隔离 | P1 不稳定 |
| 6 | 文档显式声明"非 Windows 不受支持"，避免用户按跨平台预期安装 | P0 平台阻断 |
| 7 | `file_edit` 增加编码识别（GBK/UTF-16）或明确报错指引 | P2 中文场景 |

---

## 附：本次探测命令

```powershell
rg -n "FullAccess" src/AllAgent.NET.Core/Runtime/AgentOptions.cs src/AllAgent.NET.Terminal/TerminalArguments.cs
rg -n "默认" docs/execution.zh-CN.md
dotnet test tests/AllAgent.NET.Tests --filter FullyQualifiedName~ExternalDirectoryTests.PermissionBubbleKeepsTranscriptInputAndContextVisible
dotnet test tests/AllAgent.NET.Tests --filter FullyQualifiedName~WorkspaceNavigationTests.RealTerminalSwitchesProjectsWithoutChangingAnotherTerminalOrOldHistory
```

从宿主工具侧可直接复现路径护栏（无需修改任何文件）：

```python
call_tool("file_read", path="NUL")                 # -> Windows device names and ambiguous path suffixes are not allowed.
call_tool("file_read", path="README.md:secret")    # -> Invalid workspace path...
```
