# 自动更新（NuGet / GitCode / GitHub）

AllAgent.NET 内置更新器，按安装形态选择更新通道：

| 安装形态 | 检测方式 | 更新方式 |
|---|---|---|
| NuGet 工具 | 程序目录包含 `.dotnet\tools\.store\` | `dotnet tool update`，包 ID 由实际安装决定 |
| Windows 安装包 | 程序目录包含 `unins000.exe` | 下载新的 Inno Setup 安装包并静默安装 |
| 其他目录 | 以上都不是 | 视为 Unknown：可以检查更新，但拒绝自动覆盖 |

架构对应的资源：

- x64：`AllAgent.NET-<版本>-win-x64-setup.exe`
- ARM64：`AllAgent.NET-<版本>-win-arm64-setup.exe`
- NuGet：x64 安装 `AllAgent.NET.Terminal`，ARM64 安装 `AllAgent.NET.Terminal.arm64`。两个包提供同一个 `allagent` 命令。

## 一、来源优先级

安装包：

1. `auto`（默认）：先查 **GitCode**；GitCode 不可达、404 或返回错误时回退 **GitHub**。
2. `gitcode`：只查 GitCode。
3. `github`：只查 GitHub。

NuGet 工具固定从 NuGet 更新，不能从 GitCode/GitHub 的安装包资源更新；这是为了避免把工具安装悄悄变成安装包安装。

GitCode API：

```
GET https://api.gitcode.com/api/v5/repos/{owner}/{repo}/releases/latest?type=latest
```

读取 `tag_name`、`body`、`assets[].name` 和 `assets[].browser_download_url`。私有仓库或触发鉴权时设置 `ALLAGENT_GITCODE_TOKEN`（CI 中使用 `GITCODE_TOKEN`）。

GitHub API：

```
GET https://api.github.com/repos/{owner}/{repo}/releases/latest
```

可选 `ALLAGENT_GITHUB_TOKEN` / `GITHUB_TOKEN`，用于避免匿名速率限制。

仓库坐标默认都是 `luolangaga/AllAgent.NET`，可用 `ALLAGENT_GITCODE_REPO`、`ALLAGENT_GITHUB_REPO` 覆盖。

## 二、命令

```powershell
allagent update --check                  # 只检查，输出当前版本、来源和发布页
allagent update                          # 交互确认后安装
allagent update --yes                    # 非交互确认（脚本/CI 使用）
allagent update --source github          # auto | gitcode | github | nuget
allagent update --pre                    # 预发布通道
allagent update --force                  # 同版本也重新安装
allagent update --rollback               # 回滚到上一版本
allagent update --json                   # 机器可读输出
allagent update --require-signature      # 拒绝未签名安装包
allagent update --allow-missing-checksum # 发布未提供 SHA256SUMS.txt 时仍允许安装
allagent update --no-restart             # 安装后不自动重启
```

聊天中也可以使用：

```text
/update
/update --check
/update --source gitcode
```

Web 工作台顶部会在发现新版本时显示横幅；设置页「更新」卡片提供自动检查、自动安装、来源、通道和签名要求。

## 三、校验与签名

安装包：

1. 下载同版本的 `SHA256SUMS.txt`，按文件名查找 SHA256；不匹配则中止。
2. 默认要求校验和（`update:requireChecksum`）。发布未提供校验和时可用 `--allow-missing-checksum` 放宽。
3. 对 `.exe` 执行 Windows Authenticode 校验：
   - 签名有效：继续；
   - 签名无效：始终拒绝；
   - 未签名：默认允许并提示；设置 `--require-signature`、`update:requireSignature=on` 或 `ALLAGENT_UPDATE_REQUIRE_SIGNATURE=1` 后拒绝。

NuGet：

- `dotnet tool update` 自身执行 NuGet 包完整性与签名验证。
- 更新完成后通过 `allagent.cmd` shim 启动新版，而不是旧 `.store` 里的 DLL 路径。

> 目前发布流程没有代码签名证书，因此默认允许未签名安装包。配置代码签名后，建议把 `update:requireSignature` 设为 `on`。

## 四、安装计划与回滚

更新器不会在当前进程里覆盖自己。它先在 `%LOCALAPPDATA%\AllAgent.NET\updates\<版本>-<时间>\` 写：

- `plan.json`：等待的 PID、安装形态、下载文件、安装参数、重启命令、健康标记和日志路径；
- `apply-update.ps1`：PowerShell 5.1 可执行的更新脚本；
- `update.log`：安装日志；
- `result.json`：成功/失败结果。

然后启动一个隐藏的 PowerShell 进程并退出当前应用。脚本等待旧进程退出后：

- 安装包：`/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /DIR=...`；如果安装在 `Program Files` 等需要管理员的目录，脚本会通过 `-Verb RunAs` 请求一次 UAC，按用户安装到 `%LOCALAPPDATA%` 时不会弹窗；
- NuGet：`dotnet tool update [--global|--tool-path ...] <包 ID> --version <版本>`。

安装完成后启动新版本并等待健康标记 `healthy.json`。如果 90 秒内没有标记、进程提前退出或新版本启动失败：

- 安装包：如果已缓存旧版本安装包，静默重装旧版本；
- NuGet：`dotnet tool update --version <上一版本>`。

失败结果写入 `result.json`，新启动的进程会把它合并进 `update-state.json`，Web 设置页会显示上次错误。

安装包更新会尽力缓存旧版本安装包到 `%LOCALAPPDATA%\AllAgent.NET\updates\rollback\<版本>\`；如果发布中没有对应资源，回滚会退化为手动重新安装。

## 五、启动检查

默认在启动时后台检查一次（24 小时间隔）：

- TUI：在底部提示条显示「发现新版本 vX」；
- Web：顶部横幅 + 设置页更新卡片；
- 自动安装：默认关闭。设置页开启 `update:autoInstall` 或环境变量 `ALLAGENT_UPDATE_AUTO_INSTALL=1` 后，启动检查发现新版本会直接走静默安装并重启。

检查结果缓存在 `%APPDATA%\AllAgent.NET\update-state.json`，不会每次启动都请求网络。

## 六、环境变量

| 变量 | 作用 |
|---|---|
| `ALLAGENT_UPDATE_SOURCE` | `auto` / `gitcode` / `github` / `nuget` |
| `ALLAGENT_UPDATE_CHANNEL` | `stable` / `pre` |
| `ALLAGENT_UPDATE_CHECK` | `0` 关闭启动检查 |
| `ALLAGENT_UPDATE_AUTO_INSTALL` | `1` 启动时静默安装 |
| `ALLAGENT_UPDATE_REQUIRE_SIGNATURE` | `1` 拒绝未签名安装包 |
| `ALLAGENT_UPDATE_REQUIRE_CHECKSUM` | `0` 允许缺少校验和 |
| `ALLAGENT_UPDATE_PUBLISHER` | 期望的 Authenticode 签名者主题包含的字符串 |
| `ALLAGENT_GITCODE_REPO` / `ALLAGENT_GITHUB_REPO` | 覆盖发布仓库坐标 |
| `ALLAGENT_GITCODE_TOKEN` / `ALLAGENT_GITHUB_TOKEN` | 访问令牌 |
| `ALLAGENT_UPDATE_INSTALL_KIND` | 测试用：`nuget` / `installer` |

## 七、发布侧要求

发布工作流（`.github/workflows/release.yml`）会同时产出：

- `AllAgent.NET-<版本>-win-x64-setup.exe`
- `AllAgent.NET-<版本>-win-arm64-setup.exe`
- `AllAgent.NET.Terminal.<版本>.nupkg`
- `AllAgent.NET.Terminal.arm64.<版本>.nupkg`
- 合并后的 `SHA256SUMS.txt`

GitHub Release、nuget.org 和 GitCode 使用同一组资产名。GitCode 同步脚本是 `tools/sync-gitcode-release.ps1`；它要求 GitCode 仓库已经存在对应 tag（或 `target_commitish` 能解析到该提交）。

CI 密钥/变量：

- `NUGET_API_KEY`：nuget.org 发布密钥（可选；未设置则跳过）。
- `GITCODE_TOKEN`：GitCode 个人访问令牌（可选；未设置则跳过同步）。
- `GITCODE_REPOSITORY`：GitCode 仓库坐标（变量，默认 `luolangaga/AllAgent.NET`）。
- `ENABLE_ARM64_RUNNER=true`：启用原生 `windows-11-arm` 测试任务（可选）。

## 八、排错

| 现象 | 处理 |
|---|---|
| `当前安装形态不能从 NuGet 更新` | 改用 `--source auto/gitcode/github` |
| `无法识别当前安装形态` | 只支持 NuGet 工具和 Windows 安装包；请重新安装，或手动更新 |
| `GitCode 需要访问令牌` | 设置 `ALLAGENT_GITCODE_TOKEN`，或改用 `--source github` |
| `更新包没有 Authenticode 签名` | 当前发布未签名；去掉 `--require-signature`，或在发布流程接入代码签名 |
| `SHA256 不匹配` | 下载损坏或发布资产不一致；重新运行 `allagent update --check` |
| 更新后没有重启 | 查看 `%LOCALAPPDATA%\AllAgent.NET\updates\<版本>-*\update.log` |
| 回滚不可用 | 发布中没有旧版本安装包；重新安装对应版本 |
