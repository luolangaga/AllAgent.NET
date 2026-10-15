# LanAgent — Agent Instructions

## Architecture

Projects in `LanAgent.slnx`:

| Project | Stack | Purpose |
|---|---|---|
| `src/LanAgent.Core` | .NET 8 class lib | Models, DataStore, AI providers, bridge, skills |
| `src/LanAgent.Terminal` | .NET 8 + Spectre.Console | Main terminal Agent host |
| `src/LanAgent.Sandbox` | .NET 8 + Microsoft.Agents.AI.Hyperlight | Isolated microVM worker; ships in host's `sandbox/` folder |
| `src/LanAgent.ToolGenerator` | Roslyn incremental generator | AgentTool JSON Schema and typed dispatch |
| `tests/LanAgent.Tests` | xUnit | Tool security, agent lifecycle and actual Hyperlight tests |
| `src/LanAgent.Desktop` | Avalonia 11.2 + WebView | Windows desktop shell; embeds React frontend |
| `src/LanAgent.Web` | React 19 + Iconiq UI + Vite 5 + TypeScript | Chat UI rendered inside the WebView |
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
npm --prefix src/LanAgent.Web run dev    # (or cd src/LanAgent.Web && npm run dev)

# Terminal 2 — Desktop app (skips Vite build, uses live dev server)
dotnet run --project src/LanAgent.Desktop -p:SkipWebBuild=true
```

**Dev mode routing**: `#if DEBUG` in `App.axaml.cs` loads `http://127.0.0.1:5173`. Release mode starts an `EmbeddedWebServer` on a random port serving `avares://LanAgent.Desktop/Assets/web/`.

## Build and typecheck

```powershell
# Web: typecheck then build (output → LanAgent.Desktop/Assets/web/)
cd src/LanAgent.Web
npm run build          # runs: tsc --noEmit && vite build

# Desktop (builds web first automatically unless SkipWebBuild=true)
dotnet build src/LanAgent.Desktop -p:SkipWebBuild=true   # skip web build
dotnet build src/LanAgent.Desktop                         # includes web build

# Standalone typecheck (no emit)
cd src/LanAgent.Web
npx tsc --noEmit
```

**Important**: Desktop's `.csproj` has a `BeforeBuild` target that invokes `npm run build` in the Web project. Pass `-p:SkipWebBuild=true` to suppress it during dev iteration.

## Key conventions

- **Iconiq UI** registry sources live in `src/LanAgent.Web/src/components/ui/`. Reuse these React components before adding custom controls. Frontend dependencies belong to the Web project; Vue/Pinia and Naive UI have been removed.
- **Web `@/` alias** maps to `src/LanAgent.Web/src/` (configured in both `vite.config.ts` and `tsconfig.json`).
- **TypeScript strict** with `noUnusedLocals` and `noUnusedParameters`. Unused imports or variables will fail the build.
- **Data persistence**: Terminal project data defaults to `<workspace>/.lanagent/data/`; `--data-dir` overrides it. `lanagent.json` is a v3 manifest/settings file; immutable per-conversation revisions live in `conversations/`, retaining versions referenced by the main/backup manifests. Legacy v2 data migrates with a preserved backup. The program directory contains `lanagent-projects.json` mapping workspaces to data paths. Shared Python libraries, plugins, OAuth credentials and user skills still use `%APPDATA%/LanAgent` unless `--data-dir` is explicit. Desktop and default embedded `DataStore` keep the AppData root. Atomic writes, fsync and checksum read-back protect persistence; rendering must not discover or rewrite data on every frame.
- **Bridge handlers** are registered in `BridgeHandlerRegistrar.RegisterAll()`, called at app startup in `App.axaml.cs`. New bridge methods must be registered there.
- **Built-in skills** (shell_exec, file_read, file_write, file_edit, list_directory) are seeded at startup if not already present. See `App.SeedBuiltinSkills()`.
- **Solution uses `.slnx`** (new .NET 9+ XML format). Standard `dotnet` CLI works, but IDEs may need updating.
- **WebView bridge shim**: The JS bridge (`src/bridge/index.ts`) has mock fallbacks for dev mode when the native host is absent. Tests or standalone browser dev should still work with mock data.

## AI Providers

Agent execution uses `AgentEngine` + `CodeActProvider`. Only `execute_code` is exposed to the LLM. Register typed C# tools with `[AgentTool]` and `[Description]`; the generator emits `IAgentToolSet`. Shell command strings/templates are disabled. `run_shell` requires a host allowlist, PowerShell 7.6, and per-call approval. All I/O stays in C# host tools. Missing Python/PowerShell/microVM support must block execution. See README.md for terminal commands, permissions, migration and library embedding.

Four providers in `src/LanAgent.Core/Services/AI/`:
- `openai` → `OpenAiProvider`
- `anthropic` → `AnthropicProvider`
- `gemini` → `GeminiProvider`
- `ollama` → `OpenAiProvider` (same class, different base URL)

All implement `IAiProvider` with streaming chat via `IAsyncEnumerable<StreamChunk>`.

## Logging

App logs go to `%APPDATA%/LanAgent/lanagent.log`. Both `MainWindow` and `BridgeDispatcher` write here. Look here for bridge call debugging.


