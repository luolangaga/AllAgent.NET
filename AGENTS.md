# AllAgent.NET — Agent Instructions

## Architecture

Projects in `AllAgent.NET.slnx`:

| Project | Stack | Purpose |
|---|---|---|
| `src/AllAgent.NET.Core` | .NET 10 class lib | Models, DataStore, AI providers, bridge, skills |
| `src/AllAgent.NET.Terminal` | .NET 10 + Spectre.Console | Main terminal Agent host |
| `src/AllAgent.NET.Sandbox` | .NET 10 + Microsoft.Agents.AI.Hyperlight | Isolated microVM worker; ships in host's `sandbox/` folder |
| `src/AllAgent.NET.ToolGenerator` | Roslyn incremental generator | AgentTool JSON Schema and typed dispatch |
| `tests/AllAgent.NET.Tests` | xUnit | Tool security, agent lifecycle and actual Hyperlight tests |
| `src/AllAgent.NET.Desktop` | Avalonia 11.2 + WebView | Windows desktop shell; embeds React frontend |
| `src/AllAgent.NET.Web` | React 19 + Iconiq UI + Vite 5 + TypeScript | Chat UI rendered inside the WebView |
| `tools/InspectWV` | .NET console | Debug tool for Avalonia WebView type inspection |

**Communication**: C# ↔ JS via a custom JSON-RPC bridge.
- JS → C#: `window.chrome.webview.postMessage` (Windows WebView2) or `window.webkit.messageHandlers.bridge` (macOS WebKit)
- C# → JS: `_webView.InvokeScript("window.__bridgeCallback(...)")`

## Dev commands

```powershell
# Full dev environment (Vite + Avalonia desktop app)
.\dev.ps1

# Or start pieces manually:
# Terminal 1 — Vite dev server
npm --prefix src/AllAgent.NET.Web run dev    # (or cd src/AllAgent.NET.Web && npm run dev)

# Terminal 2 — Desktop app (skips Vite build, uses live dev server)
dotnet run --project src/AllAgent.NET.Desktop -p:SkipWebBuild=true
```

**Dev mode routing**: `#if DEBUG` in `App.axaml.cs` loads `http://127.0.0.1:5173`. Release mode starts an `EmbeddedWebServer` on a random port serving `avares://AllAgent.NET.Desktop/Assets/web/`.

## Build and typecheck

```powershell
# Web: typecheck then build (output → AllAgent.NET.Desktop/Assets/web/)
cd src/AllAgent.NET.Web
npm run build          # runs: tsc --noEmit && vite build

# Desktop (builds web first automatically unless SkipWebBuild=true)
dotnet build src/AllAgent.NET.Desktop -p:SkipWebBuild=true   # skip web build
dotnet build src/AllAgent.NET.Desktop                         # includes web build

# Standalone typecheck (no emit)
cd src/AllAgent.NET.Web
npx tsc --noEmit
```

**Important**: Desktop's `.csproj` has a `BeforeBuild` target that invokes `npm run build` in the Web project. Pass `-p:SkipWebBuild=true` to suppress it during dev iteration.

## Key conventions

- **Iconiq UI** registry sources live in `src/AllAgent.NET.Web/src/components/ui/`. Reuse these React components before adding custom controls. Frontend dependencies belong to the Web project; Vue/Pinia and Naive UI have been removed.
- **Web `@/` alias** maps to `src/AllAgent.NET.Web/src/` (configured in both `vite.config.ts` and `tsconfig.json`).
- **TypeScript strict** with `noUnusedLocals` and `noUnusedParameters`. Unused imports or variables will fail the build.
- **Data persistence**: Terminal project data defaults to `<workspace>/.allagent/data/`; `--data-dir` overrides it. `allagent.json` is a v3 manifest/settings file; immutable per-conversation revisions live in `conversations/`, retaining versions referenced by the main/backup manifests. Legacy v2 data migrates with a preserved backup. The program directory contains `allagent-projects.json` mapping workspaces to data paths. Shared Python libraries, plugins, OAuth credentials and user skills still use `%APPDATA%/AllAgent.NET` unless `--data-dir` is explicit. Desktop and default embedded `DataStore` keep the AppData root. Atomic writes, fsync and checksum read-back protect persistence; rendering must not discover or rewrite data on every frame.
- **Bridge handlers** are registered in `BridgeHandlerRegistrar.RegisterAll()`, called at app startup in `App.axaml.cs`. New bridge methods must be registered there.
- **Built-in skills** (shell_exec, file_read, file_write, file_edit, list_directory) are seeded at startup if not already present. See `App.SeedBuiltinSkills()`.
- **Built-in plugins** ship in `builtin-plugins/<PackageId>/` next to the terminal host (build target `CopyBuiltinPlugins`); `PluginHost.InstallBuiltins` installs them into the shared plugins directory on first start and never overwrites an existing install. CommandCode is shipped this way; NuGet tool packages carry the same folder.
- **Solution uses `.slnx`** (new .NET 9+ XML format). Standard `dotnet` CLI works, but IDEs may need updating.
- **WebView bridge shim**: The JS bridge (`src/bridge/index.ts`) has mock fallbacks for dev mode when the native host is absent. Tests or standalone browser dev should still work with mock data.

## AI Providers

Agent execution uses `AgentEngine` + `CodeActProvider`. Only `execute_code` is exposed to the LLM. Register typed C# tools with `[AgentTool]` and `[Description]`; the generator emits `IAgentToolSet`. Shell command strings/templates are disabled. `run_shell` requires a host allowlist, the bundled PowerShell SDK worker, and per-call approval. `powershell_script` runs PowerShell code in named persistent sessions through the worker's `--serve` protocol (variables, functions, imports and working directory survive between calls); it returns bounded object projections, text or schema plus error/warning/verbose/debug/information/progress streams, and requires per-call approval. All I/O stays in C# host tools. Missing Python/PowerShell/microVM support must block execution. See README.md for terminal commands, permissions, migration and library embedding.

Four providers in `src/AllAgent.NET.Core/Services/AI/`:
- `openai` → `OpenAiProvider`
- `anthropic` → `AnthropicProvider`
- `gemini` → `GeminiProvider`
- `ollama` → `OpenAiProvider` (same class, different base URL)

All implement `IAiProvider` with streaming chat via `IAsyncEnumerable<StreamChunk>`.

## Logging

App logs go to `%APPDATA%/AllAgent.NET/allagent.log`. Both `MainWindow` and `BridgeDispatcher` write here. Look here for bridge call debugging.



PowerShell SDK 7.6.7 is embedded in `src/AllAgent.NET.PowerShell` (.NET 10). Hosts ship its output in `powershell/`; system PowerShell is not required. `--pwsh` / `AgentOptions.PowerShellPath` override the SDK worker path, not a system pwsh executable. The Roslyn generator intentionally remains netstandard2.0.
