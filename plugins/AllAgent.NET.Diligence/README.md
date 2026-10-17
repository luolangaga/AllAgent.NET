# AllAgent.NET 执行积极度插件

用 `/ext effort` 调节 Agent 做事时是“懒一点”还是“勤快一点”。默认 **标准**。

| 命令 | 效果 |
| --- | --- |
| `/ext effort` | 查看当前设置 |
| `/ext effort lazy` 或 `/ext effort 懒` | 省力：集中完成必要工作，减少可选探索和扩展 |
| `/ext effort normal` 或 `/ext effort 标准` | 标准：平衡完整性与成本 |
| `/ext effort diligent` 或 `/ext effort 勤快` | 勤快：主动排障、检查边界、验证交付结果 |
| `/ext effort off` 或 `/ext effort 关闭` | 停止添加积极度指令，恢复宿主和其他插件的原有策略 |

“省力”仍要求完成任务并做必要验证；“勤快”仍遵守用户范围、预算与审批。

## 安装

需要支持 `AgentTurnExtension` 的 AllAgent.NET 宿主（Core 1.1.0 或以上）；使用 Terminal 的插件导入功能。

取得发布的 `AllAgent.NET.Diligence.1.0.0.nupkg` 后，在 **AllAgent.NET 会话输入框**执行：

```text
/plugins import "你的包文件路径/AllAgent.NET.Diligence.1.0.0.nupkg"
```

导入后如未启用，执行：

```text
/plugins enable AllAgent.NET.Diligence
/ext effort 勤快
```

不要把这些斜杠命令输入 PowerShell。自定义嵌入宿主需要把 `PluginHost.Extensions` 接到 `AgentOptions.Extensions`，并自行提供插件命令入口。

## 怎么使用

例如先执行 `/ext effort 勤快`，再发送“修复导出失败的问题并验证”，Agent 会收到主动排查、修复并验证的策略。
只想完成一个范围明确的小改动时，可以先执行 `/ext effort 省力`，再发送任务。

设置保存在宿主分配的插件数据目录中的 `effort.json`，重新启动后保留。它对同一插件实例所服务的所有会话生效，不是每个会话各自的设置；不同宿主进程不会实时同步。
每轮任务开始时读取内存中的档位快照，正在运行的轮次不会被中途改变。新启动的子 Agent 按启动时的设置执行。

## 原理与边界

插件注册一个 Order 为 100 的 Turn 扩展，向已有 system 提示词追加一段策略，不覆盖原有内容；关闭时不追加。
其他插件仍可按扩展顺序修改提示词。若后续插件覆盖 system，积极度策略也可能被覆盖。它不修改工具列表、skills、模型参数、token 上限或最大执行轮数。

积极度是给模型的自然语言指引，具体表现取决于模型、任务和其他指令，不能保证固定耗时、调用次数或一定成功；它不是模型的 `reasoning_effort` 参数。
卸载或禁用插件后，新任务不再注入这些指令。

## 从源码构建

在仓库根目录执行：

```powershell
dotnet pack plugins/AllAgent.NET.Diligence -c Release -o artifacts/packages
```

项目通过仓库内的 Core 项目引用编译；插件包不携带 Core，运行时使用宿主提供的 SDK。
如需在独立仓库开发，可将项目引用改为 `AllAgent.NET.Core` 1.1.0 的 PackageReference，并设置 `ExcludeAssets="runtime" PrivateAssets="all"`。

## 许可证

GPL-3.0-only。NuGet 包包含许可证元数据和完整 LICENSE 文件。
