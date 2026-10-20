# CI 与发布流水线

仓库有两套 GitHub Actions：

| 工作流 | 触发 | 作用 |
|---|---|---|
| `.github/workflows/ci.yml` | push main、PR、手动 | Web 构建/测试、x64 + ARM64 构建、x64 测试、可选原生 ARM64 测试 |
| `.github/workflows/release.yml` | `v*` tag、手动 | 构建 x64/ARM64 安装包、两个 NuGet 包，发布 GitHub Release、nuget.org 和 GitCode |

## 一、CI（ci.yml）

### web

`npm ci` → `npm run build`（含 tsc、主页面、SDK、插件 UI）→ `npm test`。产物 `web-dist` 供后续构建使用。

### build（win-x64 / win-arm64）

矩阵构建：

1. 按 RID 还原 Terminal 与内嵌 PowerShell worker；
2. 显式构建 `AllAgent.NET.PowerShell`（它按路径收集，不是普通项目引用）；
3. 构建 Terminal；
4. 运行 `tools/build-nuget.ps1`，验证工具包布局（入口 DLL、worker、Web 资源）。

ARM64 使用 `win-arm64` RID 交叉构建；这验证了发布产物，不要求当前的 GitHub runner 是 ARM64。

### test（win-x64）

运行完整 xUnit 测试，上传 `artifacts/test-results` 的 TRX。

### test-arm64（可选）

由仓库变量 `ENABLE_ARM64_RUNNER=true` 启用，使用 `windows-11-arm` 原生 runner。`continue-on-error: true`，因为原生 ARM64 runner 不保证所有仓库计划都可用。

## 二、Release（release.yml）

### resolve

从 tag（`v1.0.8`）或手动输入的版本解析：

- `version`：`1.0.8`
- `tag`：`v1.0.8`
- `prerelease`：版本里带 `-` 时为 true

留空版本时读取 `src/AllAgent.NET.Terminal/AllAgent.NET.Terminal.csproj` 的 `<Version>`。

### web

构建并上传 `release-web-dist`。

### package（win-x64 / win-arm64）

每个 RID 依次执行：

```powershell
tools\build-installer.ps1 -Version <v> -Runtime <rid> -SkipWebBuild
tools\build-nuget.ps1    -Version <v> -Runtime <rid> -SkipWebBuild
```

产物：

- `AllAgent.NET-<v>-<rid>-setup.exe`
- `SHA256SUMS.<rid>.txt`
- `AllAgent.NET.Terminal.<v>.nupkg`（x64）
- `AllAgent.NET.Terminal.arm64.<v>.nupkg`（ARM64）

### release

合并分架构校验和，生成 `SHA256SUMS.txt`，然后用 `softprops/action-gh-release` 创建 GitHub Release。随后把 `artifacts/packages/*.nupkg` 推到 nuget.org（存在 `NUGET_API_KEY` 时）。

### gitcode

1. 从 GitHub Release 读取 release body；
2. 调用 `tools/sync-gitcode-release.ps1` 创建/更新 GitCode Release；
3. 上传与 GitHub 相同的安装包、NuGet 包和 `SHA256SUMS.txt`。

存在 `GITCODE_TOKEN` 时执行；否则打印跳过信息并成功退出。

## 三、必需的 Secrets / Variables

| 名称 | 类型 | 必需 | 说明 |
|---|---|---|---|
| `NUGET_API_KEY` | Secret | 发布 NuGet 时 | nuget.org API Key |
| `GITCODE_TOKEN` | Secret | 同步 GitCode 时 | GitCode 个人访问令牌（`PRIVATE-TOKEN`） |
| `GITCODE_REPOSITORY` | Variable | 否 | 默认 `luolangaga/AllAgent.NET` |
| `ENABLE_ARM64_RUNNER` | Variable | 否 | `true` 时启用原生 ARM64 测试 |

GitCode 仓库需要已经镜像代码和 tag；发布脚本会用 `target_commitish` 创建缺失的 tag（要求该提交在 GitCode 上存在）。

## 四、本地复现发布

```powershell
# Web
npm --prefix src/AllAgent.NET.Web ci
npm --prefix src/AllAgent.NET.Web run build

# x64
tools\build-installer.ps1 -Version 1.0.8 -Runtime win-x64 -SkipWebBuild
tools\build-nuget.ps1    -Version 1.0.8 -Runtime win-x64 -SkipWebBuild

# ARM64
tools\build-installer.ps1 -Version 1.0.8 -Runtime win-arm64 -SkipWebBuild
tools\build-nuget.ps1    -Version 1.0.8 -Runtime win-arm64 -SkipWebBuild

# GitCode 同步（先设置 GITCODE_TOKEN）
tools\sync-gitcode-release.ps1 -Tag v1.0.8 -Name 'AllAgent.NET 1.0.8' `
  -BodyFile release-notes.md `
  -Assets @('artifacts/installer/AllAgent.NET-1.0.8-win-x64-setup.exe',
            'artifacts/packages/AllAgent.NET.Terminal.1.0.8.nupkg')
```

## 五、发布形态

只发布 Windows 安装包和 NuGet 工具包，不发布便携 zip。用户要么使用安装包，要么通过 `dotnet tool` 安装；其他目录下的程序不会自动覆盖。

## 六、ARM64 说明

- 安装包使用 Inno Setup 6 的 `ArchitecturesAllowed=arm64`，输出 `win-arm64-setup.exe`。
- NuGet 使用独立包 ID `AllAgent.NET.Terminal.arm64`，命令仍是 `allagent`。这样 NuGet 才能解析 ARM64 的 worker apphost 和原生资源；单个包无法同时携带两种架构的 tool 入口。
- Hyperlight 微虚拟机目前以 x64 原生资源为主；ARM64 上不可用时自动回退 Monty 沙箱。
- 安装包的“补齐运行环境”仍使用 winget 安装 WebView2 / Python / ripgrep；在 ARM64 上 winget 可能安装 x64 版本并通过 Windows 的 x64 仿真运行。
