# Frontend migration

AllAgent.NET.Web now uses React 19, Vite and Tailwind 4. The terminal host and the bridge JSON-RPC method names are unchanged. Vue pages, Pinia, Vue Router and Naive UI have been removed.

Iconiq UI sources are installed from the official https://iconiqui.com/r/ registry into `src/components/ui`. Used components: Button, Input, Select, Switch, Dialog, Alert, Accordion, Badge, Avatar, Skeleton, Tooltip, AI Input and Code Block. See `ICONIQ-LICENSE.txt` for the upstream license. Local source changes use Vite environment flags and support controlled drafts, execution locks and IME-safe Enter submission.

The small observable store adapter in `src/lib/store.ts` retains the existing backend-facing actions while notifying React of nested streaming updates. `src/utils/execution.ts` retains persisted execution-trace reconstruction.

Run from the repository root:

```powershell
npm --prefix src/AllAgent.NET.Web install
npm --prefix src/AllAgent.NET.Web test
npm --prefix src/AllAgent.NET.Web run build
.\dev.ps1
```

Production assets build into `src/AllAgent.NET.Web/dist` and are copied to `web/` next to the terminal host, which serves them for `/web-ui` and for the WPF window (`allagent -guimode`). An explicit `?bridge=ws://127.0.0.1:<port>/bridge` wins over the WebView2 transport, because the backend stays in the terminal process. Browser development previews do not execute Agent actions; a real host is needed for model calls and persistence. Production builds without a bridge fail explicitly.

Validation: TypeScript and Vite build, native-bridge contract tests, terminal host compilation, and browser checks of the chat page and model configuration dialog. Live model execution was not tested.
