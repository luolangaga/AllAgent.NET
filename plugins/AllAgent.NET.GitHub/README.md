# GitHub 插件

GUI 侧栏的 **GitHub** 页面支持仓库列表、Issue / PR 搜索、详情与讨论、PR 文件补丁、通知及标记已读。列表分页，支持 `assignee:@me`、`author:@me`、`review-requested:@me` 等 GitHub 搜索条件。

## 登录

在 GitHub 页面粘贴 Personal Access Token，或在 TUI 使用 `/login github` 的保密输入框。Token 经 `/user` 验证，使用 Windows DPAPI 加密保存，不写入项目设置、提示词或 Git 远程地址。退出删除本机凭据，不会撤销 GitHub 端 Token。

私有仓库使用 classic Token 的 `repo`，通知需要 `notifications`，账号资料使用 `read:user`。Fine-grained Token 需对相应仓库授权 Issues / Pull requests / Contents 读取；GitHub Notifications API 不支持 fine-grained Token，消息页需 classic Token。组织资源还可能需要 SSO 授权。

可选浏览器登录：创建**自己的 GitHub OAuth App**，启用 Device Flow，启动宿主前设置 `ALLAGENT_GITHUB_CLIENT_ID` 为该 App 的 Client ID。GUI 点“浏览器授权”，复制页面显示的设备码到系统浏览器完成授权。无需 Client Secret；未配置时仍可用 Token 登录。

## Agent 流程

Issue 详情点“交给 Agent 解决”，PR 详情点“AI 审查 PR”。弹窗选择已有工作区或克隆到空目录，并可填写补充要求。内置流程提示词仅在后端注入模型，不下发插件界面；会话中只展示任务摘要、GitHub 链接和用户补充要求。

- 已有工作区必须为对应 GitHub 仓库根目录，检查 `origin`；保留现有分支、文件和未提交改动。
- 克隆目标必须为空或尚不存在。PR 克隆使用 `refs/pull/N/head`，核对读取的 head SHA，再以 detached HEAD 检出，支持 fork PR。中断/失败时保留文件，使用新的空目录重试。
- `git` 需在 PATH。克隆不加载子模块，禁用 hooks，凭据只经子进程环境传入，不进入磁盘 Git 配置或命令参数。
- Issue 流程：分析验收条件 → 复现/定位 → 计划与最小修复 → 回归测试/检查 diff → 根因、修改和实际验证报告。
- PR 流程：核对 base/head → 理解需求与上下文 → 逐文件检查 → 验证发现 → 按 P0–P3 汇报文件位置、触发条件和影响 → 覆盖范围及限制。提示词要求不修改源代码，工具仍受宿主当前权限控制。
- GitHub 正文、评论和补丁作为不可信数据传入，流程要求忽略其中的操作指令。大响应有上限，缺失/截断补丁会明确提醒 Agent 补充读取或说明限制。
- Agent 可调用只读工具 `github_context` 补充 Issue/PR 详情，或 `github_file` 使用宿主凭据按精确提交 SHA 分段读取文件（含私有仓库）；工具不返回 Token，可在新克隆工作区使用。
- 每次启动独立的普通 Agent 会话，采用已启用模型（优先终端记忆的配置），复用宿主持久化、流回放、审批与后台运行。启动前应配置模型及 Agent 运行环境。流程不自动提交、推送、评论、关闭 Issue、发表 Review 或合并。
- 克隆准备异步执行，GUI 可刷新后重新找到运行中操作或已启动会话。Agent 会话可以从普通对话页停止。

## TUI

```text
/login github
/ext github help
/ext github repos
/ext github issues owner/repo --query "is:open assignee:@me"
/ext github prs owner/repo --query "is:open review-requested:@me"
/ext github notifications --page 2
/ext github read 12345678
/ext github show owner/repo 123 issue
/ext github show owner/repo 456 pr
/ext github workspaces
/ext github solve owner/repo 123 "C:\Projects\repo"
/ext github review owner/repo 456 --clone "C:\Projects\review-456" --instructions 重点检查并发
```

`solve` / `review` 均支持已有目录或 `--clone`。返回工作区及会话 ID；用 `/workspace`、`/session` 查看保存结果，`/web-ui` 查看实时执行。准备过程可用终端取消键停止；取消后不会删除克隆文件。

## 构建与安装

终端宿主内置此插件，首次启动自动安装。已有安装不会被构建覆盖；更新需重新导入包或清理测试用安装。

```powershell
dotnet build src/AllAgent.NET.Terminal
dotnet pack plugins/AllAgent.NET.GitHub -c Release -o artifacts/packages
```

插件商店或 `/plugins import 文件.nupkg` 可导入。包包含 DLL、清单和 `web/` 静态资产；插件自身无需前端构建。

GUI 按钮复用宿主提供的 Iconiq UI（`/__sdk/allagent-plugin-ui.mjs`）。HTML 插件可注册 `<ag-button>`；React 插件可导入同一模块的 React、createRoot、Button、Input、Badge、Dialog、Tabs 和 Select。组件和样式在 iframe 内运行，主题跟随宿主，无需访问外部 CDN 或宿主 DOM。宿主完整 Web 构建会同时生成此共享包。

宿主新增 `PluginWorkspaceTasks` 服务（列出工作区、在指定工作区创建并启动会话）；GUI 的 `host.openConversation` 可带 `workspace`，跨工作区跳转须同时声明 `navigation` 与 `workspace.switch`。

GitHub 官方参考：[REST 通知 API](https://docs.github.com/en/rest/activity/notifications)、[OAuth Device Flow](https://docs.github.com/en/apps/oauth-apps/building-oauth-apps/authorizing-oauth-apps#device-flow)。
