# AllAgent.NET 通用 .NET 插件开发与使用指南


## 安装与管理

在终端按 **F1 → 插件商店 / 已安装插件**，或者输入：

```text
/plugins search
/plugins search 关键词
/plugins install 包ID 版本
/plugins import C:\Plugins\MyPlugin.1.0.0.nupkg
/plugins list
/plugins disable 包ID
/plugins enable 包ID
/plugins remove 包ID
```

搜索使用 NuGet V3 服务索引发现搜索地址，提交 `tags:AllAgent.NET` 查询，再对返回标签做不区分大小写的完整匹配。公开源暂时没有匹配包时会显示空结果；不会编造插件。仓库附带可直接打包、安装的示例。

安装先下载并检查包、读取依赖、选择 .NET 10 兼容的托管库，验证 `AllAgent.NET` 标签和入口清单，拒绝路径越界及依赖冲突，然后询问是否加载 DLL。**DLL 以当前用户权限运行，`AllAgent.NET` 标签不是官方认证，程序集加载上下文也不是权限沙箱。** 只安装信任的作者。Python 模型代码仍在 Hyperlight 中执行；这和已安装的 C# 插件是不同的信任边界。[.NET 插件加载说明](https://learn.microsoft.com/en-us/dotnet/core/tutorials/creating-app-with-plugin-support)

每包下载上限 64 MB，依赖最多 64 个，展开总大小上限 256 MB。安装器不会运行包的 MSBuild targets、安装脚本或可执行文件。依赖按照兼容框架及版本范围解析；遇到需要版本回溯的冲突会明确拒绝，请作者统一依赖版本。第一版主要支持托管 DLL；有原生依赖的插件需自行在 `allagent/` 中携带适配资产和正确的依赖清单，并测试对应 Windows 架构。

插件状态与内容位于数据目录的 `plugins/`，默认 `%APPDATA%/AllAgent.NET/plugins/`。每次安装使用独立目录，保留包 SHA-256 清单。卸载移除注册和安装记录，保留旧目录便于排查及恢复；需要清理磁盘时退出应用后删除已不使用的目录。禁用调用 `DisposeAsync()` 并请求卸载程序集加载上下文；不保证插件私自保留静态引用、线程或原生句柄时立即释放全部资源。

独立命令行也支持相同管理操作：

```powershell
./artifacts/terminal-codeact/allagent.exe plugins search --json
./artifacts/terminal-codeact/allagent.exe plugins import ./artifacts/packages/AllAgent.NET.SamplePlugin.1.0.0.nupkg
```

自动化安装需显式传入 `--trust-plugin`；该选项表示调用方接受运行这个 DLL，不影响 Agent 工具的逐次授权。`--nuget-source` 可以指定兼容 V3 的私有源。默认源为 `https://api.nuget.org/v3/index.json`。[NuGet 搜索接口](https://learn.microsoft.com/en-us/nuget/api/search-query-service-resource)

## 从示例开始

仓库示例位于 `samples/AllAgent.NET.SamplePlugin/`，包含实际 DLL 插件、强类型工具、源生成器 Schema、离线示例提供商、终端命令和审计 Hook。

```powershell
dotnet pack src/AllAgent.NET.Core -c Release -o artifacts/packages
dotnet pack samples/AllAgent.NET.SamplePlugin -c Release -o artifacts/packages
./artifacts/terminal-codeact/allagent.exe plugins import ./artifacts/packages/AllAgent.NET.SamplePlugin.1.0.0.nupkg
./artifacts/terminal-codeact/allagent.exe chat
```

安装后输入 `/ext hello 小明`；`/hooks` 会显示 `example.audit`。`/tools` 中可查看 `example_clock`、`example_note`。后者写入插件自己的数据目录，依然要求逐次授权。插件模型 ID 为 `example-local`，协议 ID 为 `example`，明确标记为离线演示模型，不会伪装成远程 LLM。

SDK 包 `AllAgent.NET.Core` 包含公共契约和 Roslyn 工具源生成器，使用 `dotnet add package AllAgent.NET.Core --version 1.1.0` 从 NuGet 引用。源码和 SDK 使用 `GPL-3.0-only` 许可证，包中附带许可证正文与第三方声明。构建使用支持 Roslyn 4.14 的 SDK，目标运行时为 .NET 10。

## 包与入口清单

插件项目示例：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <EnableDynamicLoading>true</EnableDynamicLoading>
    <PackageId>Company.AllAgent.NET.MyPlugin</PackageId>
    <Version>1.0.0</Version>
    <PackageTags>AllAgent.NET;LanAgent;Tools</PackageTags>
    <Authors>YourCompany</Authors>
    <Description>插件功能说明</Description>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="AllAgent.NET.Core" Version="1.1.0"
                      ExcludeAssets="runtime" PrivateAssets="all" />
    <None Include="allagent-plugin.json" Pack="true" PackagePath="allagent/" />
  </ItemGroup>
</Project>
```

DLL 位于 NuGet 包的 `lib/net10.0/`，入口清单必须位于 `allagent/allagent-plugin.json`：

```json
{
  "sdkVersion": 1,
  "assembly": "Company.AllAgent.NET.MyPlugin.dll",
  "entryType": "Company.AllAgent.NET.MyPlugin.MyPlugin"
}
```

清单还可以带一个可选的 `web` 块，向 Web 工作台贡献页面、工具卡与命令，并在沙箱 iframe 中运行插件自绘界面（见 [插件 Web 前端扩展指南](plugin-web-ui.zh-CN.md)）：

入口是可公开构造的类型，实现 `AllAgent.NET.Core.Extensions.IAllAgent.NETPlugin`。SDK v1 与宿主共享 `AllAgent.NET.Core` 程序集，不能携带另一个 Core 覆盖宿主契约。

```csharp
using AllAgent.NET.Core.Extensions;

public sealed class MyPlugin : IAllAgent.NETPlugin
{
    public string Id => "company.my-plugin";
    public string Name => "我的插件";

    public void Register(PluginContext context)
    {
        context.AddTools(new MyTools());
        context.AddCommand(new PluginCommand("hello", "显示问候",
            (args, ct) => Task.FromResult("你好！")));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
```

宿主在独立、可卸载的 `AssemblyLoadContext` 中加载插件，注册成功后才提交扩展。失败插件显示诊断，其他插件继续可用。工具名、提供商 ID、命令名、认证 ID、Hook 名不能冲突；工具不能使用内置名称或 `mcp_`、`skill_` 保留前缀。建议所有工具使用插件前缀。

## 强类型工具与 CodeAct

```csharp
using System.ComponentModel;
using AllAgent.NET.Core.Tools;

public partial class MyTools
{
    [AgentTool("myplugin_sum"), Description("计算两个整数的和")]
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

方法所在类必须为 `partial`，返回 `Task<ToolResult>`，参数支持 `string`、`int`、`bool`、这些类型的数组、带 Description 的基础类型属性 record 及其数组，以及 `CancellationToken`。源生成器生成 `IAgentToolSet` 实现、参数 Schema 和强类型调用分派；不支持的签名给出 `LA001` 编译诊断。文件写入使用 `Risk = ToolRisk.Write`，进程或外部执行使用 `Risk = ToolRisk.Execute`；默认 Read 只能用于读取和纯逻辑。

模型依然只收到 `execute_code`。插件工具通过 CodeAct 描述注册，调用格式为：

```python
result = call_tool("myplugin_sum", a=2, b=3)
print(result)
```

`call_tool` 使用一个字面量工具名和关键字参数，不能把整个参数对象作为第二个位置参数。Python 禁止 import、eval、直接文件和网络访问。C# 层验证参数、执行前 Hook、授权及审计；返回结构化 JSON，文本字段最多 20000 字符，聚合结果最多 64000 字符。工具作者需裁剪敏感数据、校验路径/主机范围，尊重取消及超时。宿主无法替不可信 DLL 实施系统权限隔离。

## 干预系统提示词、工具与 Skills

除了原有的工具、模型提供商、命令、认证和生命周期 Hook，现在可以通过 `AddTurnExtension` 与 `AddRequestExtension` 参与 Agent 运行。终端已接入，前台、后台与子 Agent 均继承这些扩展；自定义类库宿主需把 `plugins.Extensions` 和对应 `SkillCatalog` 分别传给 `AgentOptions.Extensions`、`AgentOptions.Skills`。桌面宿主目前未加载 `PluginHost`，不会自动获得终端已安装插件的这些能力。

```csharp
using AllAgent.NET.Core.Extensions;
using AllAgent.NET.Core.Services;
using AllAgent.NET.Core.Services.AI;

context.AddTurnExtension(new AgentTurnExtension("myplugin.configure", (turn, ct) =>
{
    ct.ThrowIfCancellationRequested();
    // 追加或直接替换会话系统提示词，不修改持久化会话。
    turn.SystemPrompt += "\n回答时先给出结论，再列出证据。";
    // 对内置、MCP 和插件工具均可按注册名称筛选。
    turn.Tools.Remove("run_shell");
    // 本轮隐藏技能，或注入/替换同名技能。
    turn.Skills.Remove("debug");
    turn.Skills["my-review"] = new InstructionSkill(
        "my-review", "团队审查规范", "优先检查数据丢失和并发错误，引用具体文件。",
        "", "plugin", true, false, true);
    return ValueTask.CompletedTask;
}, Order: 10));

context.AddRequestExtension(new AgentRequestExtension("myplugin.context", (request, ct) =>
{
    ct.ThrowIfCancellationRequested();
    // 主 Agent 循环每次调用模型前执行，可检查、追加、替换或删除发送的消息。
    // 这也允许修改完整的系统上下文，包括项目指令和记忆块。
    request.Messages.Insert(1, new ChatMessage("system", "采用团队约定的输出格式。"));
    return ValueTask.CompletedTask;
}));
```

`AgentTurnContext` 包含 `SessionId`、`WorkspaceRoot`、`IsSubAgent`、`UserMessage`、可写的 `SystemPrompt`、工具名集合 `Tools` 和技能字典 `Skills`。它在每个用户回合准备沙箱之前执行一次；工具筛选同时影响目录、沙箱注册和实际调用，猜出被禁用的名称也不能调用。新增工具继续使用 `AddTools`；集合不能启用未注册的工具，角色与宿主授权仍会检查。

Skills 使用本轮快照，`skill_list`、`skill_read`、`skill_reference` 和注入的技能目录保持一致。删除或禁用的技能不能通过技能工具访问，不改写磁盘或全局启用设置。内存技能的 `Path` 可为空，此时没有支持文件；需要支持文件时填写真实 `SKILL.md` 路径。`UserInvocable` 不会自动为临时技能创建终端斜杠命令。通用文件读取工具仍遵循宿主文件权限，技能筛选不负责封锁磁盘文件。

`AgentRequestContext` 包含会话 ID、子 Agent 标志、从 1 开始的模型步骤编号 `Iteration` 和可编辑的 `Messages`。修改只作用于当前请求，不回写会话历史；同一次网络重试复用处理结果，不会重复追加。该接口作用于主任务的模型循环，不包含自动记忆提炼和上下文压缩的辅助请求。修改发送的工具协议消息时，插件作者需保持工具调用与结果配对。模型工具入口仍固定为 `execute_code`，提示词修改不会改变宿主真正的执行权限。

同类扩展按 `Order` 升序、再按扩展名排序；后执行者可以修改前面的结果。每次回调最多 5 秒，支持同步回调超时、取消与包含扩展名的错误诊断；异常和超时会停止本次操作。每个回调操作独立副本，超时后继续运行的回调不能提交迟到的修改。修改仅在回调内有效，禁止保留上下文并在后台修改。不同会话使用独立作用域，共享注册表/技能目录时不会互相污染。扩展随插件启用和禁用，无需重启；`/hooks` 开关仅控制原有生命周期 Hook。

示例插件现已包含 `example.turn`、`example.request` 及仅在任务中可见的 `example-review` 技能，可直接打包试用。

## 模型提供商与终端命令

`context.AddProvider(IAiProvider)` 注册自定义提供商，实现流式 `StreamChatAsync` 与真实 `ListModelsAsync`。协议 ID 为 `IAiProvider.ProviderId`。在 `/providers add` 或 edit 的协议列表中选择插件 ID，即可使用同一模型选择、CodeAct 循环、会话持久化和思考界面。不要把系统工具重新暴露进模型的 tools 列表。

插件命令通过 `context.AddCommand(...)` 注册，入口为 `/ext 名称 参数...`，自动加入命令提示和搜索面板。返回普通文本，由宿主统一裁剪和安全渲染；不要在插件内部读终端按键、清屏、接管全屏界面或输出 ANSI 转义。命令由用户明确调用，和模型工具是不同入口；需要受控系统操作时应复用宿主工具/任务服务，不能偷偷扩大授权。

## Web 前端扩展

`web` 块让插件把能力延伸到 Web 工作台，无需修改宿主前端：

```json
"web": {
  "sdkVersion": 1,
  "capabilities": ["notifications", "storage"],
  "contributes": {
    "pages": [{ "id": "dashboard", "title": "仪表盘", "icon": "gauge", "entry": "web/dashboard.html" }],
    "toolCards": [{ "tool": "myplugin_sum", "entry": "web/card.html" }],
    "commands": [{ "id": "open", "title": "打开仪表盘", "page": "dashboard" }]
  }
}
```

- 页面与工具卡在**独立源的沙箱 iframe** 中运行（任意框架、任意样式），通过宿主自带的 `/__sdk/allagent-plugin-sdk.js` 与自己的后端桥方法（`context.AddWebBridge`）通信，推送用 `context.Web.Push`；主题经 `--ag-*` 变量实时同步。
- 打包时把 `web/**` 放进 `allagent/web/**`：`<None Include="web\**\*" Pack="true" PackagePath="allagent/web/" />`。
- Web 界面随插件启用自动生效，插件页提供独立的「Web 界面」开关与错误诊断；版本兼容上旧宿主会忽略 `web` 块。
- 完整清单字段、SDK API、能力清单、安全模型与调试方式见 [插件 Web 前端扩展指南](plugin-web-ui.zh-CN.md)。

## Hooks 生命周期

```csharp
context.AddHook(new AgentHook("myplugin.policy", (evt, ct) =>
{
    ct.ThrowIfCancellationRequested();
    if (evt.Stage == HookStage.BeforeTool && evt.ToolName == "run_shell")
        return ValueTask.FromResult(new HookDecision(false, "此工作区禁用 Shell"));
    return ValueTask.FromResult(new HookDecision());
}));
```

| 阶段 | 时机与数据 |
|---|---|
| BeforeTurn | 调用模型前；会话 ID、用户任务 |
| BeforeTool | 参数校验后、授权和执行前；工具名、JSON 参数，可否决 |
| AfterTool | 主机工具返回并审计后；工具名、成功状态 |
| AfterTurn | 任务完成前；会话 ID、完成是否正常 |
| TurnError | 非取消错误；会话 ID、失败标志 |

每次回调最多 5 秒。Before 阶段拒绝、异常、超时都会阻止操作；After 阶段异常记录到 `/hooks errors`，保持已经执行操作的真实结果，避免模型误以为写入没发生。取消会传播到 Hook。超时不能强行中断任意 .NET 代码，作者必须响应取消，禁止启动不受管理的后台线程。

`/hooks` 打开管理面板，支持查看、启用、禁用和错误记录；`/hooks list` 只展示列表。`/hooks disable 名称` 和 enable 持久化开关。后台任务运行时不能切换插件或 Hooks，以免卸载正在使用的回调。Hook 的参数可能含用户内容与凭据，作者应只记录必要信息；示例只记录阶段、ID、工具名和成功状态。

## 后台任务与子 Agent

```text
/background 检查项目中的测试问题，给出报告
/agent 分析某个模块，不修改其他目录
/tasks
/tasks show 任务ID
/tasks approve 任务ID
/tasks answer 任务ID
/tasks deny 任务ID
/tasks cancel 任务ID
/tasks wait 任务ID
```

任务默认最多 2 个并行，32 个未完成任务；每个使用独立会话、引擎和 Hyperlight worker，主界面继续接受输入。子任务最多 20 轮、50 次主机工具调用（父任务配置更小时取较小值），继承任务总超时及权限模式。后台任务的写入/外部执行停在“等待授权”，缺少信息停在“等待回答”，侧栏显示待处理数量；通过任务面板处理。后台线程不会抢夺输入或在其他弹窗中接受授权。

父模型可以通过 CodeAct 的 `subagent` 派发（single / parallel / chain），`cancel_agent` 停止运行中的子任务。前台子代理默认阻塞等待结果并随父 turn 取消；`background=true` 的后台子代理独立运行，完成后以通知回传结果。派发和取消本身也要授权。子 Agent 不注册再次派发的工具。它们共用工作区，**不等于独立 Git worktree**；并发编辑同一文件需要任务分工。后台修改不进入前台检查点；前台还原时仍会检验文件冲突。

退出应用会取消任务和沙箱，保存结果；重启遇到未结束状态会标记“退出中断”，不会自动重放外部操作。任务不是脱离应用常驻的 Windows 服务。结果预览最多 20000 字符，完整会话在持久化会话中；任务完成或取消后可用 `/resume 会话ID` 恢复。活动任务的会话不能同时在前台继续运行。

一个数据目录同时只允许一个终端写入进程，避免会话和任务状态互相覆盖；另一个进程可用 `allagent tasks list` 或 `tasks show ID` 只读查看，不会将活动任务标记为中断。授权、回答和取消在原聊天界面处理。要并行运行多个独立终端，请为各自设置不同的 `--data-dir`。

宿主向插件提供 `DataStore`、`SkillCatalog`、`McpToolService`、`BackgroundTasks`，以及进入 Agent 模式后提供的 `AgentTaskLauncher`：

```csharp
context.AddCommand(new PluginCommand("check", "启动受控 Agent 检查",
    (args, ct) => {
        ct.ThrowIfCancellationRequested();
        var launcher = context.GetService<AgentTaskLauncher>();
        return Task.FromResult(launcher.Start("检查当前项目并给出报告"));
    }));
```

不同宿主可以提供不同服务；`GetService<T>()` 不存在时会给出明确错误。插件使用任务服务也必须遵守子 Agent 的范围及授权要求，不应另建无限派发循环。类库宿主可用 `PluginHost.ProvideService<T>()` 接入自己的服务，无需引入终端 UI。

## OAuth 与其他认证插件

内置认证支持公共客户端 **浏览器 PKCE S256** 和 **设备码授权**，校验随机 state、使用绑定环回地址的临时回调监听、限时登录、刷新令牌；设备流程支持显示 user_code、授权等待和 slow_down。参考 [PKCE RFC 7636](https://www.rfc-editor.org/rfc/rfc7636)、[设备码 RFC 8628](https://www.rfc-editor.org/rfc/rfc8628)。

用户入口统一为 F1 → 账号登录，或者 `/login` 选择内置提供商及已启用认证插件。原 `/auth`、端点填写向导和 `/auth add/login/logout` 已移除。插件作者通过代码注册专用认证流程，用户不再需要填写 Client ID、Scope 和 OAuth 端点。

```text
/login
/login codex
/login codex new
/login my-service
/login status
/logout my-service
/mcp auth 服务名 my-service
/mcp connect 服务名
/providers auth 模型配置名 my-service
```

内置登录的具体能力与限制见 [账号登录说明](login.zh-CN.md)。Codex 使用官方 Continue with ChatGPT 公共客户端注册流程，校验 ID token 并连接 Responses 模型目录。账号登录、模型目录连接和实际推理成功分别展示。

认证 ID 绑定到模型提供商或 HTTP MCP 配置；访问令牌不会写入普通 API Key 配置。Windows 使用当前用户 DPAPI 加密保存在 `credentials/`。`GetCredentialAsync` 可同时提供令牌与不含秘密的账号上下文 ID，避免账号切换后复用另一个账号的加密思考状态；旧插件只实现 `GetAccessTokenAsync` 仍可运行。`ValidateModelConfiguration` 可限制令牌的协议和目标来源，必须在任何发送前调用。MCP 限制请求来源，不跟随重定向。

专用服务可以直接复用公共客户端 PKCE / 设备码类；旧 `oauth-profiles.json` 仍作为迁移兼容读取。通用 MCP 自动授权发现与动态注册尚未实现，仍需服务专用插件，参考 [MCP 授权规范](https://modelcontextprotocol.io/specification/2025-11-25/basic/authorization)。

```csharp
context.AddAuthentication(new OAuthAuthentication(
    new OAuthProfile("my-service", "我的服务", "client-id",
        "https://auth.example.com/token",
        AuthorizationEndpoint: "https://auth.example.com/authorize",
        Scope: "service.read"),
    new TokenVault(Path.Combine(context.DataDirectory, "credentials"))));
```

其他认证实现 `IAuthenticationProvider`：`Id`、`Name`、可选 `LoginInstructions`、`GetStatusAsync`、`GetCredentialAsync`、`ValidateModelConfiguration`，以及 LoginAsync、GetAccessTokenAsync、LogoutAsync。内置登录 ID `codex` 保留给宿主，插件请使用自己的前缀。LoginAsync 接收宿主打开浏览器的回调；不要自行控制终端。GetAccessTokenAsync 可以刷新令牌，但不能未经用户请求弹出登录窗。请加并发锁、防止刷新令牌轮换冲突，确保取消和秘密存储。

## 测试、打包与发布

```powershell
dotnet build AllAgent.NET.slnx -c Release -p:SkipWebBuild=true
dotnet test tests/AllAgent.NET.Tests -c Release
dotnet pack samples/AllAgent.NET.SamplePlugin -c Release -o artifacts/packages
```

发布到 NuGet 需要作者自己的包 ID 和账号。确认包内含 DLL、清单和 `AllAgent.NET` 标签，再通过自己的 NuGet 发布流程上传；宿主只负责搜索与安装，不会替作者发布包。升级采用安装指定新版本，完成或取消当前后台任务后操作。

交付前至少验证：DLL 加载失败可诊断，扩展 ID 不冲突，工具输入不合法时拒绝，拒绝授权无副作用，取消会释放资源，After Hook 失败不改变操作结果，令牌不泄漏，依赖及路径越界拒绝，禁用后模型不再获得插件工具。仓库测试包含真实 DLL 和 NuGet 包加载、真实 Hyperlight 父子 Agent、HTTP MCP 认证，以及本地 OAuth 回调与刷新。
