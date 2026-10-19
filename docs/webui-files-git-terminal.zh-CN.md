# Web 工作台：文件浏览、Git 管理与集成终端

> `/web-ui` 与 GUI 窗口（`allagent -guimode`）的顶栏新增三个入口：**工作区文件**、**Git 管理**、**集成终端**。三者都以浮窗气泡的形式覆盖在页面之上，不改变以对话为主的既有布局；窗口可拖动、可缩放，尺寸与开关状态按面板保存在浏览器本地。

## 1. 入口与交互

| 入口 | 顶栏按钮 | 快捷键 | 命令 |
|---|---|---|---|
| 工作区文件 | 文件夹图标 | `Ctrl+Shift+E` | `/files` |
| Git 管理 | 分支图标（带未提交改动数徽标） | `Ctrl+Shift+G` | `/git` |
| 集成终端 | 终端图标 | `` Ctrl+` `` | `/terminal` |

- 三个面板可以同时打开；点击某个面板会把它提到最上层（45–48 层），命令面板与对话框始终盖在它们之上。
- 标题栏可拖动，右下角手柄可缩放；窗口位置不会拖出屏幕，窄屏（≤820px）自动变为近似全屏。
- Git 徽标显示当前工作区仓库的未提交改动数（暂存 + 未暂存 + 未跟踪 + 冲突）。
- 关闭面板不会中断终端会话或 Git 状态；重新打开即可继续。

## 2. 工作区文件（只读）

### 2.1 文件树

- 顶栏「工作区文件」打开；左树右预览，宽度可拖。树、预览头部与 Git 列表统一使用 VSCode 同款的 **Material Icon Theme** 文件类型图标（按扩展名与文件名映射，如 tsx→React、cs→C#、package.json→npm、文件夹有开合两种），只按需打包用到的 SVG。
- 目录按需加载：点击展开时才调用 `workspace.tree`，一次只列一层，单目录最多 2000 项。
- 「隐藏常见忽略目录」默认开启（`.git`、`node_modules`、`dist`、`build`、`bin`、`obj`、`__pycache__` 等），可一键显示。
- 搜索框输入后切换为平铺结果（复用输入框 `@` 补全的 `workspace.files` 模糊匹配），点击直接打开预览。
- 行尾悬停操作：在资源管理器中打开、在终端中打开（以该目录为 cwd 新建终端标签）。
- 符号链接/目录联接显示「链接」标记，不会被展开或读取。

### 2.2 预览

- 代码与文本：`CodeBlock` 渲染（按扩展名映射语言、行号、复制按钮），单文件最多 256 KB，超出截断提示。
- Markdown：使用宿主同一套 `marked + highlight.js + DOMPurify` 渲染。
- 图片：png / jpg / jpeg / gif / webp / bmp / ico / svg，走 base64 data URL，上限 12 MB。
- 二进制：只显示大小与修改时间，提供「默认程序」打开。
- 预览头部操作：复制工作区相对路径、**引用到对话**（把 `@路径 ` 放进输入框）、用系统默认程序打开。
- 聊天里点击文件路径（Markdown 链接或行内代码）现在会优先在文件气泡中打开预览；目录、工作区外的路径或不存在文件仍交给宿主的 `shell.openPath`（浏览器可预览的类型开浏览器，其余开资源管理器）。

### 2.3 安全边界

所有路径都经过 `WorkspacePolicy` 解析：相对路径以工作区为根，`..`、工作区外路径、reparse point、备用数据流与 UNC 一律拒绝。文件通道只读，不提供写入/删除。

## 3. Git 管理

Git 面板按工作区绑定仓库根（工作区位于仓库子目录时自动上溯到仓库根）。每次操作返回后自动刷新状态；`workspace.changed` 时重置。非仓库时提供「初始化仓库」。

### 3.1 更改（Rider 风格单一列表）

- 暂存、未暂存与未跟踪合并成**一个「更改」列表**，按路径排序；行内用状态字母（M/A/D/R/U…）、文件类型图标和「已暂存」小标记表达 git 状态，不再拆成「暂存的更改 / 更改」两组。
- 每行复选框决定**本次提交包含哪些文件**：默认全选，取消勾选即排除。提交时宿主的做法是：先 `git add -A` 暂存整棵工作区，再把未勾选但已暂存的文件移出索引——这样**已暂存删除的文件**（index 与工作区都不存在）也能正常提交，不会触发 `fatal: pathspec … did not match any files`。已不在更改列表中的过期路径（比如刚在终端提交/删除过）会被自动忽略。
- 工具栏：刷新、全选/取消全选（支持半选状态）、丢弃勾选的更改（需确认），并显示「N / M 已勾选」。
- 冲突单独一组（红字），提供「我的 / 他们的 / 已解决」操作；存在冲突时提交按钮禁用并提示先解决。
- 悬停行操作：丢弃（包含已暂存内容，需确认；未跟踪文件为删除）。
- 点击文件名在右侧显示该文件差异（有暂存内容时显示暂存差异，否则显示工作区差异）。
- 未跟踪文件的差异由宿主读取文件内容合成「全部新增」视图。

底部提交区：

- **修正(M)**：`git commit --amend`；提交信息留空时沿用原信息。
- **撤销上次提交**：`git reset --soft HEAD~1`，改动保留在暂存区（需确认）。
- **查看日志**：跳到「日志」标签。
- **AI 生成**：见 3.2。
- **提交** / **提交并推送**：`Ctrl+Enter` 等同提交；提交并推送在无上游时自动 `push -u origin <branch>`。
- 提交身份按钮：仓库级设置 `user.name` / `user.email`；提交因身份缺失失败时会自动弹出该表单。

### 3.2 AI 生成提交信息

- 位置：「更改」页提交区的 **AI 生成** 按钮（也可手动编辑生成结果后再提交）。
- 数据来源：有暂存改动时使用 `git diff --cached`（含 `--stat`）；否则使用工作区 diff 并把未跟踪文件的前 1500 字符一并带上；同时读取最近 10 条提交标题作为**风格与语言参考**。
- 模型选择：**默认跟随输入框当前选择的模型**（页面把配置 id 显式传给宿主）；可在「设置 → Git 提交信息」里固定一个配置（例如专门用于提交信息的快模型）。未指定时宿主依次回退到终端最近使用的模型、第一个启用的带模型配置。
- 所选配置的提供商由插件提供时（例如 WorkBuddy），若插件未启用或未登录，会给出明确提示（含配置名与提供商），而不是笼统的 Unknown provider。
- 输出清洗：去掉 Markdown 代码围栏、「提交信息：」「Commit message:」等前缀与包裹引号；上限 4000 字符。
- 前端超时 180 秒；失败原因（未配置模型、无改动、网络错误）直接以通知显示。
- 桥方法：`git.generateCommitMessage { apiConfigId? }`（返回字符串）。

### 3.3 日志（提交图）

- 左侧引用树：搜索「分支或标签」，`所有分支` / 本地分支（当前分支带 HEAD 标记）/ 远程分支 / 标签；点击后按该引用过滤提交图（`git log <ref>`）。
- 工具条：**文本或哈希**搜索——普通文本走服务端固定字符串 `--grep`；输入 4–40 位十六进制时在已加载页面内按哈希前缀定位。右侧显示当前范围与刷新。
- 提交图：`git log --topo-order` 返回 commits + parents，前端计算泳道；彩色折线、合并曲线、泳道回折、提交圆点与分支色保持。默认排除 `refs/stash`（WIP 快照不是历史，画成分支会让图变乱），并隐藏 `refs/stash` 与远程符号引用（如 `origin/HEAD`）的徽标。列：描述（含分支/远程/标签彩色徽标，过长时在列内截断，不侵占作者列）、作者、日期（`yyyy/MM/dd HH:mm`）。
- 分页：每次 60 条，底部「加载更多」。
- 选中提交：右侧详情显示提交哈希、作者、正文、文件列表；点击文件查看该提交的逐文件 diff；提供复制哈希与**软回退到此提交**（`reset --soft`，需确认）。

### 3.4 分支 / 贮藏 / 远程

- 分支页：本地分支（当前标记、上游、最近提交，操作：切换 / 合并 / 变基 / 删除；未合并分支删除时二次确认后强制删除）、远程分支（检出为本地跟踪分支）、标签（只读）、远程仓库 URL。
- 贮藏页：新建贮藏（说明、包含未跟踪文件）、应用 / 弹出 / 删除。
- 合并、变基冲突时顶部显示操作横幅：继续 / 中止，冲突文件在「更改」页处理。
- 网络操作（获取 / 拉取 / 推送）以流式 job 运行：输出逐行进入操作控制台，完成后自动刷新状态并提示。推送被拒（non-fast-forward）时提供 `--force-with-lease` 的二次确认。

### 3.5 桥方法

| 方法 | 参数 → 返回 |
|---|---|
| `git.summary` | {} → 可用性、版本、仓库根、分支/上游/领先落后、操作中状态、四类计数 |
| `git.status` | {} → 暂存 / 未暂存 / 未跟踪 / 冲突（路径、原路径、index/worktree 状态字母） |
| `git.diff` | { path?, staged?, commit? } → 行数组、+N/−N、截断与二进制标记 |
| `git.log` | { skip, take, all, ref?, grep? } → commits（含 parents 与 refs）、more |
| `git.show` | { hash } → 提交元数据、正文、文件列表 |
| `git.stage` / `git.unstage` | { paths, all? } → status |
| `git.discard` | { paths, staged?, untracked? } → status |
| `git.commit` | { message, amend?, paths? } → status（paths 为勾选文件时只提交这些文件） |
| `git.identity` / `git.setIdentity` | {} / { name, email } → identity |
| `git.branches` | {} → 本地 / 远程 / 标签 |
| `git.switch` | { name, create?, startPoint? } → status |
| `git.deleteBranch` / `git.merge` / `git.rebase` | { name, force? } → status |
| `git.resolveConflict` | { path, side: ours\|theirs } → status |
| `git.operationContinue` / `git.operationAbort` | {} → status |
| `git.resetSoft` / `git.undoLastCommit` | { hash } / {} → status |
| `git.fetch` / `git.pull` / `git.push` | { rebase? } / { setUpstream?, forceWithLease? } → { id, label }（job） |
| `git.stashList` / `stashPush` / `stashApply` / `stashPop` / `stashDrop` | 见上表 |
| `git.remotes` / `git.init` / `git.installGit` | 远程列表 / 初始化 / winget 安装 |
| `git.generateCommitMessage` | { apiConfigId? } → 字符串（默认使用页面当前选择的模型） |

事件：`git.output { jobId, text, error }`、`git.done { jobId, label, ok, message }`。

### 3.6 安全与限制

- 所有 git 命令通过字面量参数调用（`ProcessStartInfo.ArgumentList`），从不拼接 shell 字符串；`GIT_TERMINAL_PROMPT=0`，`GIT_EDITOR=:`。
- 分支名、哈希、路径都有格式校验；网络命令以流式 job 运行，不受 60 秒桥请求超时限制。
- 破坏性操作（丢弃、删除未跟踪文件、强制删除分支、强制推送、软回退、撤销提交）全部需要界面确认；不提供硬回退（`reset --hard`）。
- 终端缺少 git 时，Git 页提供「通过 winget 安装 Git」（`Git.Git`，长超时）；安装后当前进程刷新 PATH 并重新探测。

## 4. 集成终端

- 基于仓库中已有的 **ConPTY** 封装（`PseudoConsole`）运行真实交互式 shell；会话由宿主进程托管，**刷新页面、切换视图、切换工作区都不会中断**，关闭标签或退出宿主才会结束进程。
- 可用 shell（存在才显示）：Windows PowerShell 5.1（默认）、命令提示符（cmd）、PowerShell 7（`pwsh.exe`）、Git Bash（`bash.exe`）。启动命令强制 UTF-8（`chcp 65001` / `[Console]::OutputEncoding`），避免中文 Windows 下输出乱码。
- 标签条：新建（默认 shell）、▾ 选择 shell 并设为默认、「关闭全部终端」；标签显示标题、运行状态，退出后标记「已退出」并显示退出码，可一键重启。
- 终端渲染使用 xterm.js（`@xterm/xterm` + fit + web-links 插件）；主题跟随工作台明暗模式；点击链接可在浏览器打开。
- 数据通道：PTY 输出按 16 ms / 64 KB 合并后 base64 推送（每会话递增序号）；输入为 UTF-8 字节 base64；窗口尺寸变化经 fit 插件去抖后调用 `terminal.resize`。每个会话保留最近 256 KB 回放缓冲，重新 attach 时先写回放再补齐新帧，序号去重保证不乱序。
- 快捷键：`` Ctrl+` `` 打开/关闭并聚焦终端；文件树「在终端中打开」以目录为 cwd 新建标签。
- 上限：同时最多 12 个会话；关闭标签会结束整棵进程树（先关闭 ConPTY，2 秒未退再终止进程树）。

桥方法：

| 方法 | 参数 → 返回 |
|---|---|
| `terminal.shells` | {} → 可用 shell 列表 |
| `terminal.list` | {} → 会话列表（标题、shell、cwd、尺寸、存活、退出码、序号） |
| `terminal.create` | { shell?, cwd?, cols?, rows? } → 会话 |
| `terminal.attach` | { id } → 会话 + 回放（base64）+ 当前序号 |
| `terminal.write` | { id, data(base64) } → bool |
| `terminal.resize` | { id, cols, rows } → bool |
| `terminal.close` | { id } → bool |

事件：`terminal.output { base64 }`（streamId = 会话 id，带 sequence）、`terminal.exit { exitCode }`、`terminal.changed`。

## 5. 故障排查

| 现象 | 处理 |
|---|---|
| Git 页提示未找到 git | 点「通过 winget 安装 Git」，或安装 Git for Windows 后重启 AllAgent；也可手动确认 `git.exe` 在 PATH |
| 提交报「提交身份未设置」 | 在提交区点身份按钮填写 `user.name` / `user.email`（写入仓库 local 配置） |
| AI 生成失败：未配置模型 | 先在「模型与 API」中启用一个可用模型；生成与对话共用凭据 |
| 终端中文乱码 | 终端启动已强制 UTF-8；若在 shell 内切回 GBK（`chcp 936`）需要自行切回 `chcp 65001` |
| 终端输入中文无响应 | 使用浏览器输入法组合完成后提交；ConPTY 会话会收到整段 UTF-8 文本 |
| 推送被拒（non-fast-forward） | 先「拉取」合并远程；确认远程无他人新提交后可用 force-with-lease 覆盖 |
| 操作提示文件已不在工作区/索引 | 该文件在页面加载后已被提交、删除或重命名；面板会自动刷新，重新勾选后再提交即可（过期路径会被忽略） |
| 面板位置异常 | 面板尺寸/开关存于 localStorage（`allagent.panel.<id>`）；清除该键或拖动/缩放即可复位 |

## 6. 实现索引

| 区域 | 文件 |
|---|---|
| 浮窗底座 / 面板状态 | `src/AllAgent.NET.Web/src/components/FloatingPanel.tsx`、`src/stores/panels.ts` |
| 文件气泡 | `src/components/WorkspacePanel.tsx`、`src/components/FileTypeIcon.tsx`、`src/stores/workspaceBrowser.ts`、`src/AllAgent.NET.Terminal/WorkspacePreviewBridge.cs` |
| Git 面板 | `src/components/GitPanel.tsx`、`src/components/git/*`、`src/stores/git.ts`、`src/lib/gitGraph.ts` |
| Git 后端 | `src/AllAgent.NET.Terminal/Git/GitRunner.cs`、`Git/GitBridge.cs`、`Git/UnifiedDiff.cs` |
| 终端面板 | `src/components/TerminalPanel.tsx`、`TerminalView.tsx`、`src/stores/terminal.ts` |
| 终端后端 | `src/AllAgent.NET.Terminal/WebTerminalManager.cs`、`TerminalBridge.cs`、`PseudoConsole.cs` |
| 桥接注册 | `src/AllAgent.NET.Terminal/WebUiSession.cs`（静态：`terminal.*`；按工作区：`git.*`、`workspace.tree/readFile`） |
