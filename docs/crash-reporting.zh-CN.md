# 崩溃报告与诊断

AllAgent.NET 在终端宿主、隐藏 GUI 宿主和 WPF 窗口外壳中都安装了进程级异常处理。未处理异常不会再只留下一段控制台堆栈：程序会写出一个可检查、可附加到 GitHub Issue 的诊断包，并告诉用户如何提交。

## 捕获范围

| 位置 | 处理方式 |
|---|---|
| 主线程 / 任意线程未处理异常 | `AppDomain.UnhandledException`，写 fatal 报告和内存转储 |
| WPF 窗口外壳 UI 线程异常 | `DispatcherUnhandledException`，写报告后关闭窗口并返回退出码 4 |
| 终端 `Program` 顶层异常 | 写 fatal 报告后返回退出码 1 |
| 命令循环 / 启动阶段被捕获的非预期异常 | 写 non-fatal 报告（不弹窗、不进入启动提示），`allagent crash list` 可见 |
| 未观察的任务异常 | `TaskScheduler.UnobservedTaskException`，写 non-fatal 报告（不弹窗、不进入启动提示） |

参数错误、权限错误、网络错误、取消和超时等预期失败只显示一行错误，不会生成崩溃报告。

`WebUiServer` 原有的未处理异常日志仍然保留；崩溃报告与它是互补关系，不会互相替代。

## 报告位置

默认目录：

```
%APPDATA%\AllAgent.NET\crashes\<本地时间>-<组件>-<ID>\
```

每个目录包含：

| 文件 | 内容 |
|---|---|
| `report.md` | 人类可读报告：版本、系统、运行时、异常链、最近日志尾部 |
| `report.json` | 结构化摘要，供后续统计或自动分析 |
| `report-url.txt` | 预填标题和正文的 GitHub 新建 Issue 链接 |
| `memory.dmp` | Windows 迷你转储（默认开启；可用环境变量关闭或改为完整转储） |

默认保留最近 20 个报告，超出后按目录时间清理。报告自动对常见的 API Key、Bearer Token、JWT、密码字段和 URL 查询参数做脱敏，但本地路径、异常消息中的业务数据仍可能存在；提交前请自行检查。

## 查看与提交

```powershell
allagent crash                 # 最近报告列表（等同 crash list）
allagent crash list            # 列表、总数与最近 7 天数量
allagent crash show [ID]       # 输出 report.md；省略 ID 查看最新
allagent crash open [ID]       # 在浏览器打开预填的 GitHub Issue 页面
allagent crash path            # 输出崩溃报告根目录
```

`crash open` 只是打开 GitHub 的 “New issue” 页面，不会自动上传任何内容。请把 `report.md` 附到 Issue；如果生成了 `memory.dmp`，建议压缩后一起上传。

发生 fatal 崩溃后，下一次正常启动会提示上次崩溃并给出报告路径。隐藏 GUI 宿主改用原生对话框询问是否打开提交页面。设置 `ALLAGENT_NO_CRASH_PROMPT=1` 可关闭启动提示；`allagent crash list` 仍可随时查看。

## 环境变量

| 变量 | 作用 |
|---|---|
| `ALLAGENT_CRASH_DIR` | 覆盖报告根目录（便于测试或集中收集） |
| `ALLAGENT_CRASH_DUMP` | `none` 关闭转储；`full` 写出完整内存转储；默认迷你转储 |
| `ALLAGENT_NO_CRASH_PROMPT` | `1` / `true` 关闭启动时的崩溃提示 |

完整转储可能非常大，且可能包含内存中的敏感数据；只在需要深入分析时临时启用。

## 隐私与遥测

崩溃包只在本地生成，**不会自动上传**，也没有默认的遥测上报。提交渠道是 GitHub Issue：

<https://github.com/luolangaga/AllAgent.NET/issues>

如果团队需要集中收集崩溃率，可以在自己的分发环境里把 `%APPDATA%\AllAgent.NET\crashes` 或其中的 `report.json` 收集起来；这属于显式的运维配置，不是程序的隐藏上报行为。

## 已知边界

- 栈溢出、`Environment.FailFast`、原生访问冲突等 CLR 无法回调的故障可能不会生成托管报告。需要这类转储时，可按 .NET 文档设置 `DOTNET_DbgEnableMiniDump` 等运行时环境变量。
- 报告器自身失败时静默返回，不会用二次异常覆盖原始错误。
- 多个线程同时崩溃时只写第一份报告；后续崩溃由运行时正常终止。
- 非 fatal 的未观察任务异常 5 分钟内相同签名只记录一次。
