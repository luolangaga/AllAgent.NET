# AllAgent.NET 插件开发入门：从零做一个能干预 Agent 的插件

本文面向第一次开发 AllAgent.NET 插件的读者。你将创建一个真正的 .NET 插件，实现计算工具、中文回答规范、工具禁用、临时 Skills、请求上下文修改和终端命令，最后打包安装到 AllAgent.NET。

本文对应 **AllAgent.NET.Core 1.1.0**。请搭配支持这些接口的终端宿主版本使用。SDK 与项目源码采用 **GPL-3.0-only** 许可证。更完整的安装器、认证、模型提供商和 Hook 说明见 [插件参考文档](https://github.com/luolangaga/AllAgent.NET/blob/main/docs/plugins.zh-CN.md)。

## 1. 先认识四个东西

| 名称 | 可以怎样理解 |
|---|---|
| AllAgent.NET 终端宿主 | 真正运行聊天、模型、沙箱和插件的程序 |
| AllAgent.NET.Core NuGet 包 | 开发插件使用的 SDK，提供接口、模型和工具代码生成器 |
| 你的插件 DLL | 编译后的 C# 代码，由宿主加载 |
| 你的插件 `.nupkg` | 安装包，里面装着 DLL 和入口清单 |

**引用 Core 包，是让你的代码能够编译；在 AllAgent.NET 中导入插件包，才是让插件开始工作。** 两步都要做。

Core 同时包含 Agent 运行库，但单独安装它不会自动得到完整终端、Hyperlight worker、Python 或 PowerShell 环境。写插件时使用现有宿主最容易入门。当前桌面应用尚未加载 `PluginHost`，本文的安装和试用入口是终端。

## 2. 可以扩展什么

| 需求 | 注册方法 | 什么时候生效 |
|---|---|---|
| 给模型增加计算、文件或业务工具 | `AddTools` | 插件加载后，工具进入宿主目录 |
| 改写本轮系统提示词 | `AddTurnExtension` | 一次用户任务开始时 |
| 限制本轮可用工具 | `AddTurnExtension` 的 `Tools` | 沙箱准备之前，目录和实际执行一起限制 |
| 新增、隐藏、替换本轮 Skills | `AddTurnExtension` 的 `Skills` | 本轮任务，结束后恢复 |
| 编辑发送给模型的上下文 | `AddRequestExtension` | 主 Agent 循环每次请求模型之前 |
| 增加用户手动命令 | `AddCommand` | 用户输入 `/ext 名称` 时 |
| 观察生命周期、否决工具调用 | `AddHook` | 对应生命周期阶段 |
| 增加模型协议 | `AddProvider` | 宿主选择该模型提供商后 |
| 登录第三方账号 | `AddAuthentication` | 用户使用登录入口时 |

这里的“本轮”指一次用户输入启动的完整 Agent 任务，可以包含多次模型请求和工具调用。不是只影响模型的一句话。

## 3. 准备开发环境

以下命令在 **PowerShell** 中执行。先确认 SDK：

```powershell
dotnet --info
```

插件目标框架是 `.NET 10`，代码用到 C# 12。Core 携带的工具源生成器使用 Roslyn 4.14，因此不要只用早期 .NET 10 SDK 编译插件；选择包含兼容或更新编译器的 SDK，例如 .NET 10 SDK。仓库当前验证环境使用 .NET 11 预览 SDK。SDK 是编译器和构建工具，目标框架则决定程序面向哪个运行时，两者不必同版本。

SDK 包名为 `AllAgent.NET.Core`，通过 NuGet 安装：

```powershell
dotnet add package AllAgent.NET.Core --version 1.1.0
```

此命令在已有项目的目录中执行。下一节会从创建项目开始，并给出完整项目文件；如果直接使用该项目文件，无需重复添加引用。

## 4. 新建第一个插件项目

选择一个工作目录，例如 `C:\AllAgent.NETPluginLab`：

```powershell
New-Item -ItemType Directory -Force C:\AllAgent.NETPluginLab
Set-Location C:\AllAgent.NETPluginLab
dotnet new classlib -n MyFirstPlugin -f net10.0
Set-Location MyFirstPlugin
```

创建完成后，把 `MyFirstPlugin.csproj` 的内容完整替换为：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <EnableDynamicLoading>true</EnableDynamicLoading>
    <PackageId>MyFirstPlugin</PackageId>
    <Version>1.0.0</Version>
    <Authors>YourName</Authors>
    <Description>我的第一个 AllAgent.NET 插件</Description>
    <PackageTags>AllAgent.NET;LanAgent;Tools;Skills</PackageTags>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="AllAgent.NET.Core" Version="1.1.0"
                      ExcludeAssets="runtime" PrivateAssets="all" />
    <None Include="allagent-plugin.json" Pack="true" PackagePath="allagent/" />
  </ItemGroup>
</Project>
```

这几个配置特别有用：

- `PackageId` 是安装包名称，`Version` 是你自己的插件版本，与 SDK 版本相互独立。
- `PackageTags` 应包含独立标签 `AllAgent.NET`；为兼容旧版宿主，建议同时保留 `LanAgent`。安装器会接受 `AllAgent.NET`、`AllAgent` 和 `LanAgent`，标签不表示官方认证。
- `ExcludeAssets="runtime"` 表示运行时使用宿主已经加载的 Core，不另外部署一份来覆盖宿主。
- `PrivateAssets="all"` 表示开发用的 SDK 引用不作为你的插件包依赖向下传播。
- 工具代码生成器已经包含在 SDK 的 `analyzers/dotnet/cs/` 中，不需要额外引用生成器项目。

恢复依赖：

```powershell
dotnet restore
```

NuGet 会根据项目引用下载 SDK 和它的依赖。首次恢复需要联网；请确保 NuGet 配置启用了 nuget.org 源。

## 5. 添加入口清单

在项目目录创建 `allagent-plugin.json`：

```json
{
  "sdkVersion": 1,
  "assembly": "MyFirstPlugin.dll",
  "entryType": "MyFirstPlugin.MyPlugin"
}
```

`assembly` 是生成的 DLL 文件名；`entryType` 是“命名空间.类名”。两者必须与代码一致。`sdkVersion` 是插件清单协议版本，仍然为 **1**，不是 Core 的包版本 `1.1.0`。

## 6. 写完整插件代码

把模板中的 `Class1.cs` 内容替换为下面的完整代码。文件也可以改名为 `MyPlugin.cs`：

```csharp
using System.ComponentModel;
using AllAgent.NET.Core.Extensions;
using AllAgent.NET.Core.Services;
using AllAgent.NET.Core.Services.AI;
using AllAgent.NET.Core.Tools;

namespace MyFirstPlugin;

public sealed class MyPlugin : IAllAgent.NETPlugin
{
    public string Id => "my.first-plugin";
    public string Name => "我的第一个插件";

    public void Register(PluginContext context)
    {
        context.AddTools(new MyTools());

        context.AddCommand(new PluginCommand("hello", "检查插件是否已加载",
            (args, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult("插件已加载！你输入了：" + string.Join(" ", args));
            }));

        context.AddTurnExtension(new AgentTurnExtension("my.configure", (turn, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            turn.SystemPrompt += "\n请使用中文回答，先给结论，再说明依据。";

            // 既从工具目录中移除，也阻止本轮实际调用。
            turn.Tools.Remove("run_shell");

            // 只影响本轮任务，不删除磁盘中的技能文件。
            turn.Skills.Remove("debug");
            turn.Skills["my-review"] = new InstructionSkill(
                Name: "my-review",
                Description: "按团队约定审查代码，优先关注数据丢失与并发问题",
                Instructions: "读取相关代码，优先检查数据丢失和并发错误。报告具体文件和证据。遵守宿主权限。",
                Path: "",
                Scope: "plugin",
                Enabled: true,
                UserInvocable: false,
                ModelInvocable: true);
            return ValueTask.CompletedTask;
        }, Order: 10));

        context.AddRequestExtension(new AgentRequestExtension("my.request", (request, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            // 每次主循环请求模型前添加，不会重复写入持久化聊天记录。
            request.Messages.Insert(1, new ChatMessage("system",
                "当前启用了团队插件：需要审查代码时，请考虑 my-review 技能。"));
            return ValueTask.CompletedTask;
        }));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

// partial 使 SDK 代码生成器可以补全工具描述与调用分派。
public partial class MyTools
{
    [AgentTool("my_sum")]
    [Description("计算两个整数的和")]
    public Task<ToolResult> SumAsync(
        [Description("第一个整数")] int a,
        [Description("第二个整数")] int b,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(ToolResult.Success(new { sum = (long)a + b }));
    }
}
```

`Register` 是注册入口，不是每次聊天执行的函数。在它里面声明工具和回调；真正的工作在工具方法或回调里发生。需要释放的 HTTP 客户端、锁和其他资源放到 `DisposeAsync` 中处理。

## 7. 编译和打包

在插件项目目录执行：

```powershell
dotnet build -c Release --no-restore
dotnet pack -c Release --no-build -o ./packages
```

成功后得到 `packages/MyFirstPlugin.1.0.0.nupkg`。它至少应包含：

```text
lib/net10.0/MyFirstPlugin.dll
allagent/allagent-plugin.json
```

`.nupkg` 本质是 ZIP 文件，可以用压缩软件打开检查。不要只把清单留在源码目录而忘了打进包里。

## 8. 安装到 AllAgent.NET 并试用

先完成或取消正在运行的任务，然后在 **AllAgent.NET 聊天输入框** 输入以下命令。这些斜杠命令不是 PowerShell 命令：

```text
/plugins import C:\AllAgent.NETPluginLab\MyFirstPlugin\packages\MyFirstPlugin.1.0.0.nupkg
/plugins list
/ext hello 小明
/tools
```

首次导入会要求接受运行该 DLL；确认插件是自己编写的后启用。列表应没有加载错误，问候命令应返回“插件已加载”，工具列表应包含 `my_sum`。

接着发一条普通任务：

```text
请调用 my_sum 计算 12 加 30，然后列出可用技能并读取 my-review。
```

预期计算结果为 `42`，技能工具能列出并读取 `my-review`。注入技能不会强迫模型一定调用它，因此验证时明确要求读取。模型只接收 `execute_code` 入口，内部调用方式类似：

```python
result = call_tool("my_sum", a=12, b=30)
print(result)
```

你通常不需要手写这段 Python，模型会依据工具目录生成它。

注意：回合工具筛选只在任务执行作用域内生效，空闲时的 `/tools` 可能仍列出 `run_shell`；它不是“本轮过滤已应用”的证据。实际任务中 `run_shell` 的目录与调用入口都会受限。Skills 同理：空闲技能管理页不展示只在回合里注入的技能。

禁用并恢复：

```text
/plugins disable MyFirstPlugin
/plugins enable MyFirstPlugin
/plugins remove MyFirstPlugin
```

这里使用的是包 ID `MyFirstPlugin`，不是入口的插件 ID `my.first-plugin`。禁用后的下一轮不再注入提示词和技能，工具也会移除。卸载会移除安装记录并保留旧目录用于恢复。

## 9. 进一步理解提示词与上下文

`turn.SystemPrompt` 是会话的基础系统提示词。用 `+=` 是追加，用 `=` 是替换；它不会直接覆盖项目指令、CodeAct 工具契约或磁盘上的会话设置。

需要改动最终发送的项目指令、记忆块或历史上下文时，使用 `request.Messages`。集合中的元素是 `ChatMessage`，可以添加消息，或用 `with` 生成替换项：

```csharp
// 放在请求扩展回调中：只修改某个自己能够识别的系统消息。
for (var i = 0; i < request.Messages.Count; i++)
{
    var message = request.Messages[i];
    if (message.Role == "system" && message.Content.Contains("MY_TEAM_MARKER"))
        request.Messages[i] = message with { Content = "MY_TEAM_MARKER：新的团队规范" };
}
```

不要依赖固定下标判断某一块永远是项目指令；其他插件也会改变消息顺序。不要随意删除工具调用消息或结果，它们必须配对。提示词只影响模型收到的指令，真正的文件、Shell 和角色权限仍在 C# 宿主中检查。

请求扩展每个模型步骤执行一次，同一步的网络重试不会反复追加。它覆盖主 Agent 循环，不包括自动记忆提炼、上下文压缩等辅助模型请求。

## 10. 工具与 Skills 的区别

**工具负责执行，Skill 负责教模型怎样执行。** “查询订单”适合写工具；“按照团队规则审查订单异常”适合写 Skill。

工具返回 `Task<ToolResult>`，声明类必须为 `partial`。输入参数可以使用字符串、整数、布尔值及 SDK 支持的数组和带属性描述的 record。参数描述会进入模型工具目录。编译报 `LA001` 时，先检查工具签名是否在生成器支持范围内。

只读计算可使用默认 `ToolRisk.Read`；写文件应标记 `Risk = ToolRisk.Write`，启动进程或外部执行应标记 `ToolRisk.Execute`。宿主根据权限模式决定是否询问用户。不要把有副作用的工具伪装成只读工具。

工具名称用独立前缀，如 `my_sum`。不能覆盖宿主保留名称，不能占用 `mcp_`、`skill_` 前缀。新增工具使用 `AddTools`，往 `turn.Tools` 添加一个不存在的名字会失败。后执行的扩展可以恢复之前移除但确实已注册的名字。

`InstructionSkill` 的字段含义：

| 字段 | 说明 |
|---|---|
| `Name` | 小写字母开头，只包含小写字母、数字和短横线，最长 64 字符；不得与内置命令冲突 |
| `Description` | 帮模型判断什么时候应该读这个技能 |
| `Instructions` | 技能正文，可通过 `skill_read` 分页读取 |
| `Path` | 真实 `SKILL.md` 文件路径；纯内存技能填空字符串 |
| `Scope` | 来源说明，示例填写 `plugin` |
| `Enabled` | 本轮是否启用 |
| `UserInvocable` | 用户调用标志；临时技能不会因此自动生成终端斜杠命令 |
| `ModelInvocable` | 是否允许模型通过技能工具读取 |

替换技能时给相同名称赋新值即可；删除使用 `Skills.Remove`。纯内存技能没有支持文件，调用 `skill_reference` 会失败。需要支持文件时，把真实技能目录与文件随插件正确部署，并填写真实路径。技能筛选限制技能工具访问，不封锁通用文件读取工具对磁盘的正常读取。

## 11. 回调顺序、错误和子 Agent

同一类扩展先按 `Order` 从小到大执行，再按名称的序数顺序执行。建议扩展名带插件前缀。后一个插件可以修改前一个插件的结果，所以团队内应约定排序。

例如插件 A（`Order: 10`）追加“使用中文”，插件 B（`Order: 20`）追加“先给结论”，最终系统提示词会同时包含两条。若 B 使用 `turn.SystemPrompt = "只采用这段提示词"`，则会覆盖 A 和基础会话提示词的内容。希望多个插件叠加时使用 `+=`，并约定不互相矛盾的指令。执行顺序能保证文本如何组合，不能保证模型能同时满足相互冲突的要求。

Turn 扩展与 Request 扩展是两个阶段：先完成本轮配置，再构建消息，然后在每个模型步骤执行 Request 扩展。Request 扩展仍可修改前面生成的系统消息，因此要同时检查这两类扩展的顺序。

每个扩展回调默认最多 5 秒。不要在回调中下载大文件、等待用户输入或进行长时间计算；耗时工作应设计为工具或受控后台任务。使用传入的 `CancellationToken`，不要另外启动线程继续修改上下文。回调失败或超时会报告扩展名并停止本次操作。宿主不能强行终止任意 DLL 内部代码。

前台和子 Agent 都会经过扩展，可以使用 `turn.IsSubAgent`、`request.IsSubAgent` 区分行为，或用会话 ID 和工作区路径限定策略。不同会话使用独立的工具和技能选择快照，不修改共享目录。

原有 Hook 依然适合否决调用：

```csharp
context.AddHook(new AgentHook("my.block-shell", (evt, ct) =>
{
    ct.ThrowIfCancellationRequested();
    return ValueTask.FromResult(
        evt.Stage == HookStage.BeforeTool && evt.ToolName == "run_shell"
            ? new HookDecision(false, "团队策略禁止 Shell")
            : new HookDecision());
}));
```

这段代码放在 `Register` 中。`/hooks` 可以管理这些生命周期 Hook；它不单独控制 Turn/Request 扩展，后两者随插件整体启用和禁用。

## 12. 常见问题排查

| 现象 | 首先检查 |
|---|---|
| 找不到 Core 1.1.0 | NuGet 源是否启用 nuget.org、网络是否正常、指定版本是否可用 |
| 找不到 `AddTurnExtension` | 是否引用旧 SDK；宿主也需要重新构建 |
| 生成器提示编译器版本过低 | `dotnet --info`、项目上层 `global.json` 是否锁定旧 SDK |
| `MyTools` 不能转换为 `IAgentToolSet` | 是否写了 `partial`；NuGet 生成器是否加载，是否报 `LA001` |
| 包缺少清单 | `.csproj` 是否包含正确的 `Pack` 与 `PackagePath` |
| 找不到入口类型 | JSON 中的命名空间、类名、程序集名是否与代码一致 |
| 工具名或扩展名冲突 | 添加自己的插件前缀 |
| 工具调用仍被拒绝 | 检查角色限制、Hook、权限模式；改提示词不会改变这些检查 |
| `/skills` 看不到临时技能 | 在真实任务中用技能工具验证；临时技能不是磁盘安装技能 |
| `skill_reference` 无法读取 | 内存技能没有目录；真实技能的路径和文件是否部署完整 |
| 修改代码后没有变化 | 递增插件版本、重新打包导入；仅编译源码不会更新已安装 DLL |
| 禁用插件失败 | 先完成或取消后台任务，避免卸载正在运行的代码 |

不要重复发布不同内容但相同版本的 NuGet 包，NuGet 全局缓存可能保留旧内容。开发迭代时把插件版本从 `1.0.0` 改为 `1.0.1` 更容易排查。

## 13. 在自己的 .NET 宿主中接入

这是进阶用法。终端已经做了这些工作，写普通插件不需要重复实现。

```csharp
await using var plugins = new PluginHost(dataDirectory);
var catalog = new SkillCatalog(store, workspaceRoot, dataDirectory);
plugins.ProvideService(store);
plugins.ProvideService(catalog);
await plugins.LoadEnabledAsync(ct);

var options = new AgentOptions
{
    WorkspaceRoot = workspaceRoot,
    Hooks = plugins.Hooks,
    Extensions = plugins.Extensions,
    Skills = catalog,
    InstructionsProvider = () => ProjectInstructions.Load(workspaceRoot) + "\n" + catalog.Instructions
};
await using var runtime = await AgentRuntime.CreateAsync(options, ct);
runtime.Tools.Register(new SkillTools(catalog));
runtime.Tools.Register(plugins.ToolSet());
var provider = new AiProviderFactory(plugins.Providers).GetProvider(config);
var engine = new AgentEngine(store, provider, runtime.CodeAct, options);
await foreach (var evt in engine.RunAsync(conversationId, "用户任务", config, ct))
    Console.WriteLine(evt);
```

这个片段展示接线关系，不是独立完整程序：`store`、会话、模型配置、路径和 `ct` 需要宿主创建。Core 不会自动下载或附带完整微虚拟机环境。运行前需要配置 Python、PowerShell、Hyperlight worker 等运行资源；缺失时执行会被阻止。自行嵌入还需要管理权限提示、取消、流式事件、持久化、插件切换以及后台任务生命周期。

## 14. SDK 版本与许可证

SDK 的包 ID 为 `AllAgent.NET.Core`，本文示例固定使用 `1.1.0`。更新 SDK 时，在项目文件中修改版本，并使用支持相同接口的宿主。插件自己的版本单独递增。

NuGet 包包含 Core DLL、工具生成器、本文 README、参考文档、GPLv3 许可证正文和第三方声明。项目源码与包信息可在 [AllAgent.NET 仓库](https://github.com/luolangaga/AllAgent.NET) 查看，许可证标识为 `GPL-3.0-only`。第三方组件保留各自的许可证和声明，详见仓库的 `THIRD-PARTY-NOTICES.md`。
