import { describe, expect, it } from 'vitest'
import { completeMention, findMention, formatMention, parentPath } from '../src/lib/mentions'

describe('mention parsing', () => {
  it('finds the token the caret sits in', () => {
    expect(findMention('@', 1)).toEqual({ start: 0, query: '' })
    expect(findMention('看下 @src/comp', 12)).toEqual({ start: 3, query: 'src/comp' })
    expect(findMention('@README.md', 10)).toEqual({ start: 0, query: 'README.md' })
  })

  it('ignores markers that cannot start a reference', () => {
    // E-mail addresses and handles inside a word are prose, not references.
    expect(findMention('mail me at luolan@example.com', 27)).toBeNull()
    expect(findMention('@src/App.tsx ', 13)).toBeNull()
    expect(findMention('@src App', 8)).toBeNull()
    expect(findMention('@open "quote', 12)).toBeNull()
    expect(findMention('', 0)).toBeNull()
    expect(findMention('@' + 'x'.repeat(300), 301)).toBeNull()
  })

  it('supports quoted references with spaces', () => {
    expect(findMention('看下 @"my file', 12)).toEqual({ start: 3, query: 'my file' })
    // A closed quote means the reference is complete; the picker stays closed.
    expect(findMention('看下 @"my file"', 13)).toBeNull()
  })

  it('replaces the token and leaves the caret after the reference', () => {
    const typed = '看下 @src/comp 并总结'
    const mention = findMention(typed, 12)!
    const completed = completeMention(typed, 12, mention, { path: 'src/components/App.tsx', isDirectory: false })
    expect(completed.text).toBe('看下 @src/components/App.tsx 并总结')
    // The separator already in the draft is kept instead of doubling it.
    expect(completed.caret).toBe('看下 @src/components/App.tsx'.length)
    expect(completed.text.slice(completed.caret)).toBe(' 并总结')
  })

  it('quotes paths with spaces and keeps the caret inside directories', () => {
    expect(formatMention('my file.png')).toBe('@"my file.png"')
    expect(formatMention('src/App.tsx')).toBe('@src/App.tsx')
    const typed = '看图 @pixa'
    const completed = completeMention(typed, typed.length, findMention(typed, typed.length)!, { path: 'src', isDirectory: true })
    expect(completed.text).toBe('看图 @src/')
    expect(completed.caret).toBe(completed.text.length)
    const quoted = completeMention('@my', 3, findMention('@my', 3)!, { path: 'my file.png', isDirectory: false })
    expect(quoted.text).toBe('@"my file.png" ')
  })

  it('splits the directory part for display', () => {
    expect(parentPath('src/views/Chat.tsx')).toBe('src/views')
    expect(parentPath('README.md')).toBe('')
  })
})
