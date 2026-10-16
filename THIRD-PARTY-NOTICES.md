# Third-party notices

## Browser Use browser operation ports

Browser operation contracts and selected browser algorithms are ported from
[browser-use/browser-use](https://github.com/browser-use/browser-use), revision
`df1e21533e323912e79a109146b711dddde8cf23` (MIT License, copyright 2024 Gregor Zunic).
The full upstream license is retained in `src/LanAgent.Core/Browser/UPSTREAM-LICENSE.txt`.
The C# / WebView2 adaptation is still incomplete; implemented operations do not imply
equivalence with the full upstream Python agent, DOM pipeline or cloud integrations.

## Pi host tool design

LanAgent's batch file editing, line-oriented reading, output truncation and shell
output accumulation are adapted from Pi's edit.ts, edit-diff.ts, read.ts,
truncate.ts and output-accumulator.ts. Search/discovery follows Pi's ignore-aware
tool design. These are reimplemented in C# with LanAgent's typed host tools,
atomic writes, permissions and checkpoint bounds. Search/discovery both use rg.
Source: https://github.com/earendil-works/pi/tree/main/packages/coding-agent/src/core/tools

MIT License

Copyright (c) 2025 Mario Zechner

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

## FlaUI

Windows Computer Use distributes FlaUI.Core and FlaUI.UIA3 5.0.0.
Source: https://github.com/FlaUI/FlaUI

The MIT License (MIT)

Copyright (c) 2016-2024

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

## omni-computer-use overlay

LanAgent's Computer Use indicator adapts the capsule layout and edge-glow falloff
from omni-computer-use's overlay.py, with native WPF rendering and a blue palette.
Source: https://github.com/Jason26214/omni-computer-use/blob/16e4b7acc416a51bb6583dfe89cefff5249f799b/src/omni_computer_use/overlay.py

MIT License

Copyright (c) 2026 Jason26214

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

## OpenCode

LanAgent's subagent completion behavior is adapted from OpenCode,
https://github.com/anomalyco/opencode, commit
e31136bdbdb43e934d8bbd81e75a2065a2625699.

MIT License

Copyright (c) 2025 opencode

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

## Other dependencies

LanAgent bundles unchanged CPython 3.14.0 standard-library sources (`base64`,
`struct`, and UTF-16 codecs) from https://github.com/python/cpython/tree/v3.14.0/Lib
for imports inside its isolated Python guest. The PSF license and historical
license notices are preserved in `src/LanAgent.Sandbox/Python/stdlib/LICENSE`
and shipped with the sandbox worker.

LanAgent's built-in C# web search is inspired by the multi-engine approach of
[open-webSearch](https://github.com/Aas-ee/open-webSearch), by Aas-ee, licensed
under [Apache License 2.0](https://github.com/Aas-ee/open-webSearch/blob/main/LICENSE).
It is implemented in-process and does not require the upstream Node.js service.

HTML parsing uses [Html Agility Pack](https://github.com/zzzprojects/html-agility-pack)
1.12.4, distributed under the [MIT license](https://github.com/zzzprojects/html-agility-pack/blob/master/LICENSE).
Its managed runtime library is included by .NET publishing.

## Models.dev model metadata

The embedded Services/AI/models-catalog.json is derived from https://models.dev/api.json
(retrieved 2026-10-13), maintained by https://github.com/anomalyco/models.dev.
Model limits are associated with exact provider/model IDs; endpoint metadata takes precedence.

## Design reference and delegation protocol

LanAgent implements its agent loop and host tools in C#. It does not ship or run
`@earendil-works/pi-coding-agent` or a bundled Node.js runtime. Its single,
parallel and sequential delegation protocol follows the subagent extension example
in https://github.com/earendil-works/pi (reference commit
f13c00fb3a4d77ad3faa815057e31d614d42b3f1). Cancellation, roles, permissions and
CodeAct execution are implemented by LanAgent's host. The reference's license is
retained below for the adapted protocol:

MIT License

Copyright (c) 2025 Mario Zechner

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

## Iconiq UI frontend

The desktop frontend uses editable MIT-licensed Iconiq UI registry sources from https://iconiqui.com/r/ (upstream: https://github.com/edwinvakayil/iconiq). The complete license is preserved in `src/LanAgent.Web/ICONIQ-LICENSE.txt`. Local adaptations use Vite environment flags and add controlled input, disabled state, and IME handling to AI Input.

## PowerShell SDK

The bundled LanAgent.PowerShell worker embeds Microsoft.PowerShell.SDK 7.6.7 (MIT), from https://github.com/PowerShell/PowerShell. NuGet runtime assets and their third-party notices are distributed with the worker.
