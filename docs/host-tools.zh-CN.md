# 宿主文件与命令工具

文件和命令 I/O 仍在 C# 宿主执行。Python 通过 `call_tool` 调度，权限、审批、审计和检查点机制继续生效。工具行为参考 Pi，输出预算适配 AllAgent.NET 的 JSON 返回上限。

## 读取文本与图片

```python
r = call_tool("file_read", path="src/File.cs", offset=250, limit=80)
print(r)
# offset 是从 1 开始的行号；下一页使用 r["data"]["nextOffset"]。
```

每页最多 2,000 行、20,000 UTF-8 字节，另有 JSON 转义大小预算。普通分页不切断行，返回 `content`、`offset`、`nextOffset`、`totalLines`、`outputLines`、`outputBytes`、`truncatedBy` 和 `notice`。空文件正常返回空文本；越过 EOF 返回明确错误。文本文件不设置人为大小上限。

当第一行自身超过预算时，返回该行的一段完整 Unicode 文本和 `nextColumnOffset`。保持同一 `offset`，把该值传入 `column_offset`，可继续读取单行，最后一段的 `nextColumnOffset` 为 null。`column_offset` 是 C# 字符偏移，必须在 Unicode 字符边界上。

```python
print(call_tool("file_read", path="design.png"))
```

PNG/JPEG/GIF/WebP 按文件内容识别，返回图片元数据并通过模型附件通道传送真实图片，不在工具 JSON 中输出 base64。沿用单图 4 MB 的限制，暂不增加图片缩放或 BMP 转换。

## 搜索与路径查找

```python
# 按文件名查找：names_only 只列文件、不读内容，pattern 可留空
print(call_tool("file_search", names_only=True, path="src", file_pattern="*.cs"))
# 搜索文件内容，返回行号和上下文
print(call_tool("file_search", pattern="QuickMode|FastMode", regex=True,
                path="src", file_pattern="*.cs", context_lines=2))
```

`file_search` 由宿主直接启动已安装的 ripgrep，无 Shell 命令拼接，不额外依赖 fd。缺少 rg 时返回明确错误，终端可通过 `/doctor install` 修复。默认读取 `.gitignore` 等 rg 忽略规则，包括子目录规则和否定规则；没有 `.git` 的目录也适用。默认还排除常见构建/依赖及代理缓存目录。`include_ignored=true` 显式绕过忽略规则。始终不跟随符号链接。

`names_only=true` 时 `file_search` 改为名字模式：只列文件、不读内容，`file_pattern` 按文件名或相对路径匹配，`pattern` 作为可选名字过滤（`regex=true` 时用正则）。名字模式仍尊重 `.gitignore` 并排除构建/依赖目录，`include_ignored=true` 可放开；返回工作区相对路径和 `nextOffset`，目录本身请用 `list_directory` 查看。

`file_search` 默认使用普通文本，`regex=true` 使用 rg 正则。结果返回工作区相对路径、从 1 开始的行号和有界上下文。`file_pattern` 无路径分隔符时匹配文件名，有分隔符时匹配搜索目录下的相对路径（`*` 匹配单个路径段，`**` 可匹配零层或多层目录）；过滤不会覆盖 `.gitignore`。匹配正文最多 1,000 字符，上下文每行最多 500 字符，`linesTruncated=true` 表示应使用 `file_read` 读取对应行。

搜索/查找接受任意正数 limit、非负 offset 和非负 context_lines；超过单次输出预算时返回分页或裁剪的上下文，不拒绝参数。`nextOffset` 只推进已返回的条目。`matchLimitReached`、`outputTruncated` 表示仍可继续分页；`linesTruncated`/`contextTruncated` 表示正文或上下文未全部返回，可用 `file_read` 分段查看。扫描不设置固定文件数量上限或超时，也不按文件大小跳过文本文件；用户取消仍会终止进程。

## 目录列表

`list_directory` 保留排序、目录/链接标记、条目分页和 `include_ignored`。新增序列化字节预算、`outputTruncated`、`entryLimitReached` 与续查提示，使用 `nextOffset` 继续。只列一层，不解释 `.gitignore`。

## 命令输出

```python
r = call_tool("run_shell", exe="dotnet", args=["build"], timeout_seconds=900)
print(r)
```

仍使用 `exe + args` 和受控 PowerShell 宿主，参数独立传递。timeout_seconds 默认 0，表示不设超时；任意正数表示调用方主动指定的超时，没有前台/后台最大值限制。可信别名、审批、工作目录授权和进程树取消机制继续生效。

stdout/stderr 返回末尾的有界文本，分别限制行数、UTF-8 字节和 JSON 转义大小。超过预算时完整的清理后输出保存到工作区 `.allagent/tool-output/`，并返回 `stdoutPath`/`stderrPath`；短输出不创建日志。可按行读取日志，无需重新运行命令。日志随工作区保留，不自动删除。

超时返回 `ok=false`、`timedOut=true`、退出码 -1、超时说明和已捕获输出；用户取消仍作为取消传播。后台命令同样保留最近输出，并在输出截断时报告日志路径。通用 BoundedProcess 调用维持原有行为，仅命令工具启用尾部/落盘模式。

需要 PowerShell `-EncodedCommand` 时，直接使用 guest 中的 CPython 标准库：

```python
import base64
script = "Write-Output '中文和引号'"
encoded = base64.b64encode(script.encode("utf-16-le")).decode("ascii")
print(call_tool("run_shell", exe="pwsh", args=["-NoProfile", "-EncodedCommand", encoded]))
```

guest 补入原版 CPython 3.14 的 base64、struct、UTF-16/UTF-16LE/UTF-16BE 模块，编码转换在 Python 中完成。每次恢复干净快照后重新载入，用户修改模块不会污染后续调用；无需专用宿主转换工具。普通程序启动直接用目标 exe 和参数；不要为普通启动套上 Start-Process、RunAs 或动态 `--%`。提升权限需要用户明确授权。

首次初始化检查 PowerShell 7.6，缺少时先提示通过 winget 安装，成功后停止并提示重启 AllAgent.NET。宿主 Python 检查可执行性和 3.10–3.14 版本范围，缺少或版本不符时提示通过 winget 安装 Python 3.13 并在成功后重启；环境就绪后在 `python/venv` 中安装常用 pip 库并生成 `pip.md`。随后实际验证 Hyperlight guest 的标准库导入、编码往返及 struct 操作；只有成功后才记录初始化完成。`/doctor` 和聊天启动也使用相同 guest 校验。宿主 pip 库仍通过目录中明确的 Python 可执行文件与 run_shell 使用，脚本需要显式 import；它们不自动成为 guest 的库或全局变量。

Hyperlight worker 的初始化、执行、快照恢复和释放固定在同一线程。检测到原生 Tokio 上下文错误或 poisoned lock 时丢弃 worker，下一次调用创建新的 microVM；不自动重放失败的代码，因为之前的宿主副作用可能已经发生。普通 Python 异常保持原诊断并由下一次干净快照恢复。

## 按需披露

部分工具家族（`computer_*`、`browser`）常驻注册但默认不出现在工具目录里：技能用 `SKILL.md` 的 `tools:` 字段声明自己披露哪些工具，模型 `skill_read` 成功后 schema 才进入提示，披露按会话保持。未披露的调用会被拒绝并点名所需技能。细节见 [按需披露的宿主工具](on-demand-tools.zh-CN.md)。

## PowerShell 持久会话

```python
r = call_tool("powershell_script", script="$s = Get-Service | Select-Object -First 5", output="objects")
r = call_tool("powershell_script", script="$s | Select-Object Name, Status")
```

`powershell_script` 在随包 SDK worker 的命名持久 runspace 中执行 PowerShell 代码：变量、函数、导入模块和当前目录跨调用保留，输出可为有界对象投影、纯文本或类型反射（`schema`），并返回错误、警告、详细、调试、信息和进度六类流。默认会话 main、默认超时 120 秒；超时、取消或崩溃会重置会话并在结果中标记。每次调用需要执行审批，工作目录沿用外部目录授权。完整参数、字段、上限和会话语义见 [PowerShell 持久会话](powershell-session.zh-CN.md)。

## 写入与编辑

`file_write` 继续要求显式 `overwrite=true` 才覆盖已有文件，沿用原子写入、审批、差异预览、检查点及同文件排队。输入、最终文件大小、每批编辑条目数和检查点文件数量不设置固定上限；冲突检查、原子写入和恢复校验继续生效。

`file_edit` 使用 `path + edits[]`，每个 `{old_string,new_string}` 匹配同一原文件；拒绝歧义、重叠和整批无变化，保留 BOM 与换行格式，不受整文件写入的输入字符上限影响。详见 [Agent 交互](agent-interaction.md)。

参考源码：[Pi tools](https://github.com/earendil-works/pi/tree/main/packages/coding-agent/src/core/tools)。授权说明见仓库 THIRD-PARTY-NOTICES.md。
