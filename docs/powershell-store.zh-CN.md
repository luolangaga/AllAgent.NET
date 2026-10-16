# PowerShell 能力商店

AI 可使用 `powershell_invoke` 直接调用单个已验证模块的命令。例如：

```python
r = call_tool("powershell_invoke", module="Microsoft.PowerShell.Management", command="Get-Process", parameters={"Name": "dotnet"})
print(r)
```

参数通过 SDK `AddCommand` / `AddParameter` 绑定，字符串不作为脚本表达式解析。`powershell_command_info(module=..., command=...)` 返回实际参数名称、类型和参数集；模块导入可能执行代码，因此该查询也需要执行授权。执行结果为有界 JSON records（最多 5 层、64 属性、100 数组项、字符串 4000 字符），超量输出保存在日志文件。执行保留工作目录授权、审批、超时和取消；不会隔离已经批准命令的文件访问或其他宿主副作用。外部程序及复杂脚本继续使用 `run_shell`。

终端命令和聊天命令共用一个模块目录。无需安装系统 PowerShell。

交互终端直接输入 `/powershell`（或 `lanagent powershell`）打开管理面板：查看已安装模块、导入状态与命令数量，选择模块分页查看导出命令，搜索 PSGallery 并查看作者、版本和说明后安装，也可按名称指定版本安装。面板支持刷新清单和返回聊天。非交互或 `--json` 模式下，无参数入口仍返回模块列表；所有现有子命令继续可用。

```powershell
lanagent powershell search Pester
lanagent powershell install Pester 5.7.1
lanagent powershell list
lanagent powershell commands Pester
```

聊天内对应 `/powershell search Pester`、`/powershell install Pester 5.7.1`、`/powershell list`、`/powershell commands Pester`。省略版本安装当前稳定版；安装使用明确模块名，不接受通配符。命令清单每页 100 个，`commands 模块 100` 查看下一页。`--json` 可用于独立终端命令。

来源为 PowerShell Gallery。首次搜索或安装自动下载微软 Microsoft.PowerShell.PSResourceGet 1.2.0。使用其 [Save-PSResource](https://learn.microsoft.com/powershell/module/microsoft.powershell.psresourceget/save-psresource) 下载模块及依赖到 LanAgent 专属目录，不修改系统 PowerShell 安装。安装命令接受所选模块及依赖的许可证。模块是会执行的代码，安装表示允许导入它及其依赖。

默认位置为 `%APPDATA%/LanAgent/powershell/Modules`；显式 `--data-dir` 时跟随共享数据目录。首次安装需要联网。每次新建 Agent runtime 时实际导入验证并生成能力清单；聊天内管理命令会同步刷新当前 runtime 的清单。模型上下文包含模块名、版本、导入错误和命令数量，可调用 `powershell_capabilities(module="Pester", skip=0, limit=50)` 分页查询导出命令。

安装后导入失败会明确报错，失败模块会保留错误详情，不标记为可用。模块可能还需要账号、外部软件或 PowerShell 版本支持；成功导入并不代表完成这些配置。此版本提供终端面板、搜索、安装和发现，不包含卸载或启停管理。

AI 使用模块时通过现有 `file_write` 写入 `.ps1`，再以脚本绝对路径作为 `run_shell.exe` 执行，保留工作区授权、执行审批、超时和输出限制。模块商店自身不新增模型侧的任意执行或免审批安装工具。内部 SDK worker 不支持 `pwsh -Command` 参数。
