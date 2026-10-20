# AllAgent.NET — Agent Instructions

## Architecture

Projects in `AllAgent.NET.slnx`:

| Project | Stack | Purpose |
|---|---|---|
| `src/AllAgent.NET.Core` | .NET 10 class lib | Models, DataStore, AI providers, bridge, skills |
| `src/AllAgent.NET.Terminal` | .NET 10 + Spectre.Console | Main terminal Agent host |
| `src/AllAgent.NET.Sandbox` | .NET 10 + Microsoft.Agents.AI.Hyperlight | Isolated microVM worker; ships next to the host executable |
| `src/AllAgent.NET.ToolGenerator` | Roslyn incremental generator | AgentTool JSON Schema and typed dispatch |
| `tests/AllAgent.NET.Tests` | xUnit | Tool security, agent lifecycle and actual Hyperlight tests |
| `src/AllAgent.NET.Desktop` | — | removed: the WPF GUI mode replaced it |
| `src/AllAgent.NET.Gui` | .NET 10 + WPF + WebView2 | Native window shell for GUI mode; hosts the Web UI in a WebView2, no Core dependency |
| `src/AllAgent.NET.Web` | React 19 + Iconiq UI + Vite 5 + TypeScript | Chat UI rendered inside the terminal-hosted Web UI |

**Communication**: C# ↔ JS via a custom JSON-RPC bridge.
- GUI mode and `/web-ui`: the page is served over loopback HTTP and talks to the host over the `/bridge` WebSocket (`invoke()` in `src/bridge/index.ts`). GUI mode always passes an explicit `?bridge=ws://127.0.0.1:<port>/bridge`, which wins over the WebView2 probe.
- `window.chrome.webview.postMessage` + `window.__bridgeCallback` remain for hosts that load the page from a custom scheme; `window.chrome.webview` alone does not select that transport.

## Dev commands

```powershell
# Full dev environment (Vite + native GUI window)
.\dev.ps1

# Or start pieces manually:
# Terminal 1 — Vite dev server
npm --prefix src/AllAgent.NET.Web run dev    # (or cd src/AllAgent.NET.Web && npm run dev)

# Terminal 2 — GUI window against the Vite dev server
dotnet run --project src/AllAgent.NET.Terminal -- gui --gui-url http://127.0.0.1:5173/

# GUI mode in product form: detached host, window survives the shell prompt
dotnet run --project src/AllAgent.NET.Terminal -- -guimode
```

**GUI mode**: `allagent -guimode` validates the payload, spawns a hidden copy of the host (`ALLAGENT_INTERNAL_TERMINAL_MODE=gui`, no console window) and returns at once; that copy bootstraps the backend, starts `WebUiSession` and launches `gui/AllAgent.NET.Gui.exe --url …`. `allagent gui` runs the same host in the foreground (Ctrl+C ends it) and is what dev tooling uses. The window closes when the host dies and the host exits when the window closes; one window per data directory (named mutex).

## Build and typecheck

```powershell
# Web: typecheck then build (output → src/AllAgent.NET.Web/dist/, copied to web/ next to the host)
cd src/AllAgent.NET.Web
npm run build          # runs: tsc --noEmit && vite build

# Terminal host (builds the web bundle first unless SkipWebBuild=true)
dotnet build src/AllAgent.NET.Terminal -p:SkipWebBuild=true   # skip web build
dotnet build src/AllAgent.NET.Terminal                         # includes web build

# Standalone typecheck (no emit)
cd src/AllAgent.NET.Web
npx tsc --noEmit
```

**Important**: `AllAgent.NET.Terminal` has a `BeforeBuild` target that invokes `npm run build` in the Web project and copies `dist/` into `web/` next to the host. Pass `-p:SkipWebBuild=true` to suppress the npm step during dev iteration. Every worker (WPF shell, browser, computer, sandbox, PowerShell) ships in the host's own directory and shares one self-contained runtime there (targets `CopyGuiShellFiles` / `IncludeGuiShellPublishFiles` and the siblings in `Directory.Build.targets`).

## Key conventions

- **Iconiq UI** registry sources live in `src/AllAgent.NET.Web/src/components/ui/`. Reuse these React components before adding custom controls. Frontend dependencies belong to the Web project; Vue/Pinia and Naive UI have been removed.
- **Web `@/` alias** maps to `src/AllAgent.NET.Web/src/` (configured in both `vite.config.ts` and `tsconfig.json`).
- **TypeScript strict** with `noUnusedLocals` and `noUnusedParameters`. Unused imports or variables will fail the build.
- **Data persistence**: Terminal project data defaults to `<workspace>/.allagent/data/`; `--data-dir` overrides it. `allagent.json` is a v3 manifest/settings file; immutable per-conversation revisions live in `conversations/`, retaining versions referenced by the main/backup manifests. Legacy v2 data migrates with a preserved backup. The program directory contains `allagent-projects.json` mapping workspaces to data paths. Shared Python libraries, plugins, OAuth credentials and user skills still use `%APPDATA%/AllAgent.NET` unless `--data-dir` is explicit. The GUI window and `/web-ui` share the terminal's store; the default embedded `DataStore` keeps the AppData root. Atomic writes, fsync and checksum read-back protect persistence; rendering must not discover or rewrite data on every frame.
- **Bridge handlers** are registered in `BridgeHandlerRegistrar.RegisterAll()` (shared surface) plus the host-specific registrations in `WebUiSession` (workspace, session controls, skills, plugins). New bridge methods must be registered in one of them.
- **On-demand tool disclosure**: host tool *registration* and *disclosure* are separate. Tools stay registered for dispatch, approvals and audit, while `ToolSurface` (`src/AllAgent.NET.Core/Tools/ToolSurface.cs`) decides what the prompt catalog shows. Tools stay out of `## Host tool catalog` until their skill is loaded with `skill_read`, and the skill declares them in `SKILL.md` frontmatter (`tools: [computer_*]`). A gated call before disclosure is refused with the skill name; a successful `skill_read` returns `activatedTools` and lets `AgentEngine` rewrite the contract block mid-turn. Hosts keep one `ToolSurface` per conversation (`AgentService.SurfaceFor`); children never inherit it. The Hyperlight `SandboxTools` list stays the full registry so disclosure never restarts the microVM. See `docs/on-demand-tools.zh-CN.md`.
- **Built-in skills** (shell_exec, file_read, file_write, file_edit, list_directory) are seeded at startup if not already present; the terminal seeds the bundled SKILL.md catalog through `SkillCatalog.SeedBundled()`.
- **Built-in plugins** ship in `builtin-plugins/<PackageId>/` next to the terminal host (build target `CopyBuiltinPlugins`); `PluginHost.InstallBuiltins` installs them into the shared plugins directory on first start and never overwrites an existing install. CommandCode, GitHub, WorkBuddy and CodeGraph are shipped this way; NuGet tool packages carry the same folder. CodeGraph registers `codegraph_*` tools behind the `code-graph` skill, injects its usage policy through `AgentTurnExtension`, and builds/queries an in-process tree-sitter index (Windows x64 grammar DLLs under `native/win-x64`); it has no Node/npm/CLI dependency. Its settings section uses the generic plugin settings API.
- **Background main sessions**: a turn keeps running when the view moves elsewhere (`/new`, `/session`, another workspace). Workspace-scoped services live in process-level hosts (`TerminalHost`/`TerminalHosts` for the terminal, `WebUiSession` bundle cache for the browser), and runs are separated from views: `SessionRuns` pumps events into a `BridgeStreamJournal` and a view attaches/detaches. New running-time commands must be added to `TerminalUi.RunningHints` and the `RunningCommand` switch; new bridge methods that answer per workspace go through the bundle bindings (`BridgeDispatcher.Install`), while host-level ones (like `conversations.running`) are registered once in `RegisterStaticHandlers`.
- **Plugin web extensions**: a plugin's `allagent-plugin.json` may carry a `web` block (`PluginWebManifest` in `src/AllAgent.NET.Core/Extensions/PluginWeb.cs`). `PluginHost` validates it (entries must resolve inside the install directory's `web/`, fail-soft: a bad block disables only the web part), exposes the `web.contributions` snapshot plus `plugin.<packageId>.*` bridge methods from `PluginContext.AddWebBridge`, and `PluginAssetServer` serves the payload from its own loopback origin (SDK at `/__sdk/…`, assets at `/p/<packageId>/…`). The Web shell renders plugins through `src/AllAgent.NET.Web/src/lib/pluginRuntime.ts` + `components/plugins/*` (sandboxed frames, capability-gated `host.*` API, theme/context sync) with the frame-side SDK in `src/AllAgent.NET.Web/src/plugin-sdk/` (built to `dist/sdk/` by `vite.sdk.config.ts`). Docs: `docs/plugin-web-ui.zh-CN.md`.
- **Solution uses `.slnx`** (new .NET 9+ XML format). Standard `dotnet` CLI works, but IDEs may need updating.
- **WebView bridge shim**: The JS bridge (`src/bridge/index.ts`) prefers an explicit `?bridge=` socket address, then the native WebView transports, then mock data for dev preview. Standalone browser dev keeps working with mock data.

## AI Providers

`AgentEngine` is a compatibility facade over `IAgentKernel`. `FrameworkAgentKernel` uses Microsoft.Agents.AI 1.24.0; `ALLAGENT_AGENT_KERNEL=legacy|framework` selects the migration loop. DataStore remains the only history writer. See `docs/agent-architecture.zh-CN.md` for pipeline, rollback and validation details.

Compose instructions through `PromptComposer`, restoring the host execution contract after request extensions. Do not add unconditional delegation guidance to child/Simple/read-only prompts. All model-originated callbacks go through `IHostToolGateway`; task scopes and role limits are enforced again after approval. Runtime leases are shared by data directory, then isolated by workspace and conversation, never by role. New delegation should use `contract` and explicit `editablePaths`; use `wait_agents` for owned child results. See `docs/agent-prompts.zh-CN.md` and `docs/subagent-contract.zh-CN.md`.

Agent execution uses `AgentEngine` + `CodeActProvider`. Only `execute_code` is exposed to the LLM. Register typed C# tools with `[AgentTool]` and `[Description]`; the generator emits `IAgentToolSet`. Shell command strings/templates are disabled. `run_shell` requires a host allowlist, the bundled PowerShell SDK worker, and per-call approval. `powershell_script` runs PowerShell code in named persistent sessions through the worker's `--serve` protocol (variables, functions, imports and working directory survive between calls); it returns bounded object projections, text or schema plus error/warning/verbose/debug/information/progress streams, and requires per-call approval. All I/O stays in C# host tools. Missing Python/PowerShell/microVM support must block execution. See README.md for terminal commands, permissions, migration and library embedding.

Four providers in `src/AllAgent.NET.Core/Services/AI/`:
- `openai` → `OpenAiProvider`
- `anthropic` → `AnthropicProvider`
- `gemini` → `GeminiProvider`
- `ollama` → `OpenAiProvider` (same class, different base URL)

All implement `IAiProvider` with streaming chat via `IAsyncEnumerable<StreamChunk>`.

## Logging

App logs go to `%APPDATA%/AllAgent.NET/allagent.log`. The GUI shell writes `[Gui]` lines, the hidden GUI host `[GuiHost]`, and `BridgeDispatcher` logs every bridge call. Look here for bridge and GUI startup debugging.



PowerShell SDK 7.6.7 is embedded in `src/AllAgent.NET.PowerShell` (.NET 10). Hosts ship its output next to the host executable with the SDK modules under `Modules/`; system PowerShell is not required. `--pwsh` / `AgentOptions.PowerShellPath` override the SDK worker path, not a system pwsh executable. The Roslyn generator intentionally remains netstandard2.0.
