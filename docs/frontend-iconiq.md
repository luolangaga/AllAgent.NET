# Frontend migration

LanAgent.Web now uses React 19, Vite and Tailwind 4. The Avalonia host and its JSON-RPC method names are unchanged. Vue pages, Pinia, Vue Router and Naive UI have been removed.

Iconiq UI sources are installed from the official https://iconiqui.com/r/ registry into `src/components/ui`. Used components: Button, Input, Select, Switch, Dialog, Alert, Accordion, Badge, Avatar, Skeleton, Tooltip, AI Input and Code Block. See `ICONIQ-LICENSE.txt` for the upstream license. Local source changes use Vite environment flags and support controlled drafts, execution locks and IME-safe Enter submission.

The small observable store adapter in `src/lib/store.ts` retains the existing backend-facing actions while notifying React of nested streaming updates. `src/utils/execution.ts` retains persisted execution-trace reconstruction.

Run from the repository root:

```powershell
npm --prefix src/LanAgent.Web install
npm --prefix src/LanAgent.Web test
npm --prefix src/LanAgent.Web run build
.\dev.ps1
```

Production assets continue to build into `src/LanAgent.Desktop/Assets/web`. Browser development previews do not execute Agent actions; native desktop hosting is needed for real model calls and persistence. Production builds without a native bridge fail explicitly.

Validation: TypeScript and Vite build, native-bridge contract tests, Avalonia desktop compilation, and browser checks of the chat page and model configuration dialog. Live model execution was not tested.
