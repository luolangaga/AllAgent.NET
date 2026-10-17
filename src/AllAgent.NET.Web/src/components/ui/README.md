# Iconiq UI sources

These components were installed from the official registry at `https://iconiqui.com/r/<name>.json` on 2026-10-14. Upstream: https://github.com/edwinvakayil/iconiq, MIT license (see `../../../ICONIQ-LICENSE.txt`).

Entries: `b-button`, `input`, `b-select`, `b-switch`, `b-dialog`, `alert`, `b-accordion`, `badge`, `avatar`, `skeleton`, `b-tooltip`, `ai-input`, `code-block`.

Local adaptations replace Next/Node environment checks with Vite environment checks. AI Input additionally supports a controlled draft, disabled state, and composition-safe Enter handling. Shared Tailwind tokens and `@/lib/utils` are provided by the application.
