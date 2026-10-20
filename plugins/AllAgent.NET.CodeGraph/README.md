# AllAgent.NET.CodeGraph

把 [CodeGraph](https://github.com/colbymchenry/codegraph) 的预构建代码知识图接入 AllAgent.NET。Agent 用一次 `codegraph_explore` 就能拿到相关源码、调用链和影响范围，替代反复 grep + 逐文件读取。

## 功能

- `codegraph_explore`：只读工具，按自然语言问题或符号/文件集合返回源码、调用路径与 blast radius。
- `codegraph_status`：只读工具，报告 CLI、索引位置、大小和 `codegraph status` 统计。
- 两个工具默认**不暴露**；模型通过 `skill_read("code-graph")` 加载技能后才进入工具目录，且不触发审批。
- 插件注入 `<allagent-codegraph>` 提示词：只要工作区有 `.codegraph/` 索引，就引导模型先读技能、优先用 CodeGraph、不要再用 grep 重新验证。
- 终端 `/ext codegraph ...` 与 Web 设置页的通用插件设置区都能安装 CLI、构建/同步/重建索引、取消操作。
- TUI 和 GUI 都通过 Core 的 `ToolResult.Card` 渲染 CodeGraph 卡片，不使用宿主专用的 HTML 工具卡。

## 安装

插件会按以下顺序查找 `codegraph`：

1. 设置里的 `path`。
2. `ALLAGENT_CODEGRAPH_PATH`。
3. PATH 上的 `codegraph.exe` / `codegraph.cmd` / `codegraph.bat` / `codegraph`。
4. 常见安装位置（npm 全局目录、`~/.codegraph/bin`、`%LOCALAPPDATA%\Programs\codegraph`、WinGet Links）。

找不到时：

```text
/ext codegraph install --yes
```

或在 Web 设置页点击“安装 CodeGraph”。安装优先使用 npm（`@colbymchenry/codegraph@1.6.2`），没有 npm 时在 Windows 上执行官方 PowerShell 安装脚本。

## 索引

```text
/ext codegraph status     # CLI + 当前工作区索引
/ext codegraph init       # 无索引时 init；已有索引时 sync
/ext codegraph sync       # 增量同步
/ext codegraph rebuild    # index --force
/ext codegraph explore 问题
/ext codegraph path [绝对路径]
/ext codegraph unlock
```

索引默认写入 `<workspace>/.codegraph/`，尊重 `CODEGRAPH_DIR`。建议把 `.codegraph/` 加入项目的 `.gitignore`。插件不会自动修改工作区文件。

## 权限与安全

- `codegraph_explore` / `codegraph_status` 是 `ToolRisk.Read`，不弹审批。
- 索引构建、同步、安装只能由用户显式触发，不暴露给模型。
- 子进程通过 `ProcessStartInfo.ArgumentList` 启动，不拼 shell；query 拒绝 `-` 开头和控制字符。
- 子进程默认 `NO_COLOR=1`、`CODEGRAPH_TELEMETRY=0`；可在设置中打开遥测。
- 默认每个工作区串行执行 sync/index/explore；如果同一项目已有其他 codegraph writer，会返回明确诊断。

## 构建

```powershell
dotnet build plugins/AllAgent.NET.CodeGraph -c Debug
dotnet pack plugins/AllAgent.NET.CodeGraph -c Release -o artifacts/packages
```

宿主构建时 `CopyBuiltinPlugins` 会把 DLL、`allagent-plugin.json` 和 `web/**` 复制到 `builtin-plugins/AllAgent.NET.CodeGraph/`；首次启动由 `PluginHost.InstallBuiltins` 安装到共享插件目录。
