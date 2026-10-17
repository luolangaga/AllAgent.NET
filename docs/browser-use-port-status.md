# Browser Use source port status

Reference: https://github.com/browser-use/browser-use/tree/df1e21533e323912e79a109146b711dddde8cf23

The upstream source is the behavioral reference. This is a C# / WebView2 adaptation;
the full project has not yet been ported. Do not report complete parity.

| Source | Current correspondence | Remaining differences |
| --- | --- | --- |
| tools/service.py search | Exact supported engines and query URL rules | Live search site behavior depends on site |
| tools/service.py navigate, wait | new_tab mapping; original bounded wait calculation | Upstream navigation recovery checks remain incomplete |
| tools/service.py search_page, find_elements | CSS scope, literal/regex matching, context, attributes and result bounds | Source DOM search utilities not fully ported |
| tools/service.py dropdown_options, select_dropdown | Native HTML select | ARIA combobox fallback not yet ported |
| browser/watchdogs/default_action_watchdog.py | CDP mouse/key events, occlusion checks, clear/append input | Full framework fallback and coordinate safety algorithm not yet ported |
| browser/session.py | CDP sessions and tabs, dialogs, disconnect without closing external browser | Reconnection, target recovery, frame sessions, lifecycle event synchronization remain incomplete |
| dom/service.py, dom/serializer | Same-origin DOM/open shadow-root observations | Full AX + DOMSnapshot + paint order + cross-origin iframe pipeline remains to port |
| tools/service.py evaluate | JavaScript expressions/Promise results, errors, dialog interruption | Full upstream JS repair/serialization logic remains incomplete |
| tools/service.py upload_file, save_as_pdf | Not implemented | Host path permissions and transfers needed |
| browser/watchdogs/downloads_watchdog.py | Native browser download behavior only | Progress tracking and managed download results needed |
| agent/service.py | Existing AllAgent.NET reasoning loop | Upstream history, recovery, batch action orchestration not ported |

The built-in skill remains the on-demand entry point. Runtime host I/O and user
permissions remain enforced by AllAgent.NET. No Python browser dependency or separate
Chromium download is introduced. Preserve the upstream MIT notice with ports.

Regression coverage includes real Monty skill loading using both keyword and dict
arguments, actual WebView2 JS, and actual Edge CDP page-control verification.
