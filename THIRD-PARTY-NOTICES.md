# Third-party notices

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

LanAgent's built-in C# web search is inspired by the multi-engine approach of
[open-webSearch](https://github.com/Aas-ee/open-webSearch), by Aas-ee, licensed
under [Apache License 2.0](https://github.com/Aas-ee/open-webSearch/blob/main/LICENSE).
It is implemented in-process and does not require the upstream Node.js service.

HTML parsing uses [Html Agility Pack](https://github.com/zzzprojects/html-agility-pack)
1.12.4, distributed under the [MIT license](https://github.com/zzzprojects/html-agility-pack/blob/master/LICENSE).
Its managed runtime library is included by .NET publishing.
