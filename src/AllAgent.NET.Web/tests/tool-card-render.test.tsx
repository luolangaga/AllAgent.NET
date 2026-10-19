import { describe, expect, it } from 'vitest'
import { renderToStaticMarkup } from 'react-dom/server'
import { ToolTrace } from '../src/components/ToolTrace'
import type { ToolCall } from '../src/types'

describe('tool card rendering', () => {
  it('summarises a file edit while collapsed and shows the diff rows when opened', () => {
    const calls: ToolCall[] = [{
      id: 'e1', name: 'file_edit', status: 'done',
      args: JSON.stringify({ path: 'src/App.tsx', edits: [{ old_string: 'old line', new_string: 'new line' }] }),
      output: JSON.stringify({ ok: true, data: { path: 'src/App.tsx', diff: {
        added: 1, removed: 1, truncated: false,
        lines: [
          { kind: 'context', oldLine: 1, newLine: 1, text: 'const app = 1' },
          { kind: 'removed', oldLine: 2, newLine: null, text: 'old line' },
          { kind: 'added', oldLine: null, newLine: 2, text: 'new line' },
        ],
      } } }),
    }]

    const collapsed = renderToStaticMarkup(<ToolTrace calls={calls} />)
    expect(collapsed).toContain('file_edit')
    expect(collapsed).toContain('src/App.tsx')
    expect(collapsed).toContain('+1')
    expect(collapsed).toContain('−1')
    expect(collapsed).not.toContain('old line')

    const opened = renderToStaticMarkup(<ToolTrace calls={calls} defaultOpen={['e1']} />)
    expect(opened).toContain('old line')
    expect(opened).toContain('new line')
    expect(opened).toContain('tool-diff-row removed')
    expect(opened).toContain('tool-diff-row added')
  })

  it('counts search matches while collapsed and lists them when opened', () => {
    const calls: ToolCall[] = [{
      id: 's1', name: 'file_search', status: 'done',
      args: JSON.stringify({ pattern: 'tool-trace', path: 'src' }),
      output: JSON.stringify({ ok: true, data: { pattern: 'tool-trace', path: 'src', matches: [
        { path: 'src/a.ts', line: 7, text: 'const trace = 1', context: [] },
      ], notice: 'Search complete.' } }),
    }]

    const collapsed = renderToStaticMarkup(<ToolTrace calls={calls} />)
    expect(collapsed).toContain('1 处匹配')
    expect(collapsed).toContain('tool-trace · src')
    expect(collapsed).not.toContain('const trace = 1')

    const opened = renderToStaticMarkup(<ToolTrace calls={calls} defaultOpen={['s1']} />)
    expect(opened).toContain('src/a.ts')
    expect(opened).toContain('const trace = 1')
  })

  it('shows command output and exit code when a shell call is opened', () => {
    const calls: ToolCall[] = [{
      id: 'r1', name: 'run_shell', status: 'error',
      args: JSON.stringify({ exe: 'dotnet', args: ['build'] }),
      output: JSON.stringify({ ok: false, error: 'Executable exited with an error.', data: { exitCode: 1, stdout: 'build started', stderr: 'CS1002: expected ;' } }),
    }]

    const collapsed = renderToStaticMarkup(<ToolTrace calls={calls} />)
    expect(collapsed).toContain('dotnet build')
    expect(collapsed).toContain('exit 1')
    expect(collapsed).not.toContain('CS1002')

    const opened = renderToStaticMarkup(<ToolTrace calls={calls} defaultOpen={['r1']} />)
    expect(opened).toContain('build started')
    expect(opened).toContain('CS1002: expected ;')
    expect(opened).toContain('Executable exited with an error.')
  })

  it('renders a sandboxed plugin frame when the tool has a registered web card', async () => {
    // jsdom has no matchMedia; the settings store and the frame's theme hook rely on it.
    Object.assign(window, { matchMedia: () => ({ matches: false, addEventListener() {}, removeEventListener() {} }) })
    // Seed the contribution snapshot directly: the routing under test is "tool name → frame",
    // not the bridge transport (the frame payload here is a self-contained data: page).
    const { useContributionsStore } = await import('../src/stores/contributions')
    const contributions = useContributionsStore()
    contributions.plugins = [{
      packageId: 'AllAgent.NET.SamplePlugin', pluginId: 'sample', name: '示例插件', version: '1.0.0',
      enabled: true, webEnabled: true, active: true, capabilities: [],
      contributes: { toolCards: [{ tool: 'example_clock', entry: 'data:text/html;charset=utf-8,%3Cp%3Edemo%3C%2Fp%3E' }] },
      devUrl: null, error: null,
    }]
    contributions.assetBaseUrl = null
    expect(contributions.toolCardFor('example_clock')?.entry.startsWith('data:text/html')).toBe(true)

    const calls: ToolCall[] = [{ id: 'c1', name: 'example_clock', status: 'done', args: '{}', output: '{"ok":true}' }]
    const opened = renderToStaticMarkup(<ToolTrace calls={calls} defaultOpen={['c1']} />)
    expect(opened).toContain('plugin-tool-card')
    expect(opened).toContain('data:text/html')
    expect(opened).toContain('sandbox="allow-scripts allow-same-origin allow-forms allow-popups allow-popups-to-escape-sandbox allow-downloads"')

    const generic = renderToStaticMarkup(<ToolTrace calls={[{ ...calls[0], name: 'file_read' }]} defaultOpen={['c1']} />)
    expect(generic).not.toContain('plugin-tool-card')
  })
})
