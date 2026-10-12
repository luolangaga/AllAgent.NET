# LanAgent .NET Tool

LanAgent 的终端 AI Agent，安装后的命令为 `lanagent`。

## 环境要求

目前 Agent 执行支持 Windows x64，需要 .NET 8 或更高版本运行时、硬件虚拟化和 Windows Hypervisor Platform。工具已启用主版本向前滚动，因此安装了较新 .NET 运行时的电脑也可启动它。安装工具仍需 .NET SDK 提供 `dotnet tool` 命令。Python 3.10–3.14 与 PowerShell 7.6.x 在执行前检查。工具包自带 sandbox worker、Hyperlight 原生组件和 Python guest；宿主 Python 与 PowerShell 仍需安装。

## 安装与使用

发布到 NuGet.org 后：

```powershell
dotnet tool install --global LanAgent.Terminal
lanagent --help
lanagent doctor
lanagent doctor --install
lanagent chat --workspace C:\Projects\MyProject
```

进入聊天后按 F2 配置模型或使用 `/login`。更新和卸载：

```powershell
dotnet tool update --global LanAgent.Terminal
dotnet tool uninstall --global LanAgent.Terminal
```

## 本地打包与安装

在仓库根目录运行（打包需 .NET SDK）：

```powershell
dotnet pack src/LanAgent.Terminal -c Release
dotnet tool install --global LanAgent.Terminal --add-source ./artifacts/packages --version 1.0.0
```

版本默认为 1.0.0，可通过 `-p:Version=1.0.1` 覆盖。sandbox 的文件作为工具运行文件放入包内的 `sandbox/` 子目录，不依赖另外发布 LanAgent.Core 或 LanAgent.Sandbox 包。

## 发布

确认包 ID 在 NuGet.org 可用、版本及作者信息正确，并用有权发布该包的 API Key 执行：

```powershell
dotnet nuget push ./artifacts/packages/LanAgent.Terminal.1.0.0.nupkg --source https://api.nuget.org/v3/index.json --api-key <API_KEY>
```

API Key 请通过本地安全配置提供，不要提交到仓库。这里只配置和生成工具包；公共发布需要单独执行上述命令。
