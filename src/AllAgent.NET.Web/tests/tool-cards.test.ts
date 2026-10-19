import { describe, expect, it } from 'vitest'
import { describeToolCard, diffTexts } from '../src/lib/toolCard'
import type { ToolCall } from '../src/types'

function call(value: Partial<ToolCall> & Pick<ToolCall, 'id' | 'name'>): ToolCall {
  return { args: '{}', status: 'done', ...value }
}

describe('tool card summaries', () => {
  it('summarises a file edit with the host diff and its line counts', () => {
    const card = describeToolCard(call({
      id: 'e1', name: 'file_edit',
      args: JSON.stringify({ path: 'src/App.tsx', edits: [{ old_string: 'a', new_string: 'b' }] }),
      output: JSON.stringify({ ok: true, data: { path: 'src/App.tsx', replacements: 1, diff: {
        added: 2, removed: 1, truncated: false, replacement: false,
        lines: [
          { kind: 'context', oldLine: 1, newLine: 1, text: 'const x = 1' },
          { kind: 'removed', oldLine: 2, newLine: null, text: 'a' },
          { kind: 'added', oldLine: null, newLine: 2, text: 'b' },
          { kind: 'added', oldLine: null, newLine: 3, text: 'c' },
        ],
      } } }),
    }))
    expect(card.label).toBe('编辑文件')
    expect(card.target).toBe('src/App.tsx')
    expect(card.badges).toEqual([{ text: '+2', tone: 'add' }, { text: '−1', tone: 'del' }])
    expect(card.body.kind).toBe('diff')
    if (card.body.kind !== 'diff') return
    expect(card.body.diff.lines).toHaveLength(4)
    expect(card.body.diff.preview).toBe(false)
    expect(card.body.path).toBe('src/App.tsx')
  })

  it('previews the diff from arguments when a rejected edit has no result', () => {
    const card = describeToolCard(call({
      id: 'e2', name: 'file_edit', status: 'error', rejected: true,
      args: JSON.stringify({ path: 'b.txt', edits: [{ old_string: 'one\ntwo', new_string: 'one\nthree' }] }),
    }))
    expect(card.body.kind).toBe('diff')
    if (card.body.kind !== 'diff') return
    expect(card.body.diff).toMatchObject({ preview: true, added: 1, removed: 1 })
    expect(card.body.diff.lines.map(line => line.kind)).toEqual(['context', 'removed', 'added'])
    expect(card.badges).toContainEqual({ text: '预览', tone: 'muted' })
  })

  it('previews a rejected file_write as an all-added diff', () => {
    const card = describeToolCard(call({
      id: 'w1', name: 'file_write', status: 'error', rejected: true,
      args: JSON.stringify({ path: 'notes/new.md', content: 'one\ntwo' }),
    }))
    expect(card.target).toBe('notes/new.md')
    expect(card.badges).toEqual([{ text: '+2', tone: 'add' }, { text: '−0', tone: 'del' }, { text: '预览', tone: 'muted' }])
    expect(card.body.kind === 'diff' && card.body.diff.lines.every(line => line.kind === 'added')).toBe(true)
  })

  it('counts file_search matches and keeps the pattern in the body', () => {
    const card = describeToolCard(call({
      id: 's1', name: 'file_search', args: JSON.stringify({ pattern: 'tool-trace' }),
      output: JSON.stringify({ ok: true, data: { pattern: 'tool-trace', path: 'src', matches: [
        { path: 'src/a.ts', line: 3, text: 'tool-trace', context: [] },
        { path: 'src/b.ts', line: 9, text: 'tool-trace', context: [{ line: 9, text: 'tool-trace', match: true }] },
      ], nextOffset: 2, notice: 'Search complete.' } }),
    }))
    expect(card.label).toBe('搜索内容')
    expect(card.target).toBe('tool-trace · src')
    expect(card.badges[0]).toEqual({ text: '2 处匹配', tone: 'ok' })
    expect(card.body).toMatchObject({ kind: 'search', pattern: 'tool-trace', more: false })
    if (card.body.kind !== 'search') return
    expect(card.body.matches.map(match => match.line)).toEqual([3, 9])
  })

  it('summarises run_shell with the command and its exit code', () => {
    const card = describeToolCard(call({
      id: 'r1', name: 'run_shell', args: JSON.stringify({ exe: 'dotnet', args: ['build', 'src/App.csproj'] }),
      output: JSON.stringify({ ok: true, data: { exitCode: 0, stdout: 'Build succeeded.', stderr: '' } }),
    }))
    expect(card.target).toBe('dotnet build src/App.csproj')
    expect(card.badges[0]).toEqual({ text: 'exit 0', tone: 'ok' })
    expect(card.body).toMatchObject({ kind: 'command', stdout: 'Build succeeded.' })
  })

  it('marks a failing command in red', () => {
    const card = describeToolCard(call({
      id: 'r2', name: 'run_shell', status: 'error', args: JSON.stringify({ exe: 'npm', args: ['test'] }),
      output: JSON.stringify({ ok: false, error: 'Executable exited with an error.', data: { exitCode: 1, stderr: '2 failed' } }),
    }))
    expect(card.badges[0]).toEqual({ text: 'exit 1', tone: 'error' })
    expect(card.body).toMatchObject({ kind: 'command', stderr: '2 failed', note: 'Executable exited with an error.' })
  })

  it('reports file_read totals and the inspected range', () => {
    const card = describeToolCard(call({
      id: 'f1', name: 'file_read', args: JSON.stringify({ path: 'README.md' }),
      output: JSON.stringify({ ok: true, data: { path: 'README.md', content: 'a\nb', offset: 10, nextOffset: 12, totalLines: 40, outputLines: 2 } }),
    }))
    expect(card.target).toBe('README.md')
    expect(card.badges).toContainEqual({ text: '40 行', tone: 'muted' })
    expect(card.body).toMatchObject({ kind: 'read', range: '10–11 / 40 行', language: 'markdown' })
  })

  it('keeps execute_code source and stdout together', () => {
    const card = describeToolCard(call({
      id: 'c1', name: 'execute_code', args: JSON.stringify({ code: 'print(1)' }),
      output: JSON.stringify({ ok: true, data: { stdout: '1\n', stderr: '', exitCode: 0 } }),
    }))
    expect(card.label).toBe('执行 Python')
    expect(card.badges[0]).toEqual({ text: 'exit 0', tone: 'ok' })
    expect(card.body).toMatchObject({ kind: 'code', language: 'python', code: 'print(1)', stdout: '1\n' })
  })

  it('shows how many host calls one execute_code program folded', () => {
    const card = describeToolCard(call({
      id: 'c2', name: 'execute_code', args: JSON.stringify({ code: 'print(1)' }),
      output: JSON.stringify({ ok: true, data: { stdout: '1\n', stderr: '', exitCode: 0, hostCallSummary: { total: 12, failed: 1 } } }),
    }))
    expect(card.badges.map(badge => badge.text)).toEqual(['exit 0', '12 次宿主调用', '1 失败'])
  })

  it('lists everything_search paths', () => {
    const card = describeToolCard(call({
      id: 'p1', name: 'everything_search', args: JSON.stringify({ query: '*.pdf' }),
      output: JSON.stringify({ ok: true, data: { engine: 'everything', query: '*.pdf', paths: ['C:/a.pdf', 'C:/b.pdf'], hasMore: true } }),
    }))
    expect(card.target).toBe('*.pdf')
    expect(card.badges.map(badge => badge.text)).toEqual(['2 个文件', '已截断'])
    expect(card.body).toMatchObject({ kind: 'paths', more: true })
  })

  it('falls back to raw arguments and result for unknown tools', () => {
    const card = describeToolCard(call({
      id: 'x1', name: 'mcp_abc_lookup', args: JSON.stringify({ url: 'https://example.com' }),
      output: JSON.stringify({ ok: true, data: { value: 1 } }),
    }))
    expect(card.label).toBe('mcp_abc_lookup')
    expect(card.target).toBe('https://example.com')
    expect(card.body.kind).toBe('json')
  })
})

describe('diffTexts', () => {
  it('keeps shared context and counts inserted and deleted lines', () => {
    const diff = diffTexts('a\nb\nc', 'a\nB\nc')
    expect(diff).toMatchObject({ added: 1, removed: 1, preview: false, truncated: false })
    expect(diff.lines.map(line => [line.kind, line.text])).toEqual([
      ['context', 'a'],
      ['removed', 'b'],
      ['added', 'B'],
      ['context', 'c'],
    ])
  })
})
