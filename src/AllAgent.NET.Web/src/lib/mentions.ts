// `@`-reference parsing for the web composer. The host resolves the same syntax
// (see PromptReferences in the core library), so the completion here only has to
// produce exactly the text the terminal editor would produce.

export interface MentionQuery {
  /** Index of the `@` marker inside the draft. */
  start: number
  /** Text typed after the marker, without it. */
  query: string
}

/** Long drafts with a stray `@` must not trigger a filesystem query. */
const MAX_QUERY = 200

/**
 * The `@` token the caret currently sits in, or null when the caret is not
 * inside one. A marker only starts a reference at the beginning of a line or
 * after whitespace, which keeps e-mail addresses out of the picker.
 */
export function findMention(text: string, caret: number): MentionQuery | null {
  if (caret <= 0 || caret > text.length) return null
  const head = text.slice(0, caret)
  const start = head.lastIndexOf('@')
  if (start < 0) return null
  if (start > 0 && !/\s/.test(head[start - 1]!)) return null
  const token = head.slice(start + 1)
  if (token.length > MAX_QUERY) return null
  if (token.startsWith('"')) {
    // A closed quote means the reference was accepted; keep editing without the picker.
    if (token.includes('"', 1)) return null
    return { start, query: token.slice(1) }
  }
  if (/[\s"]/.test(token)) return null
  return { start, query: token }
}

/** Reference syntax for one path: quoted when it contains whitespace. */
export function formatMention(path: string): string {
  return /\s/.test(path) ? `@"${path}"` : `@${path}`
}

/**
 * Replaces the typed token with the chosen entry. Files close the reference with
 * a separator (unless the draft already has one); directories keep the picker
 * open for drilling in.
 */
export function completeMention(
  text: string,
  caret: number,
  mention: MentionQuery,
  entry: { path: string; isDirectory: boolean },
): { text: string; caret: number } {
  const before = text.slice(0, mention.start)
  const after = text.slice(caret)
  const reference = formatMention(entry.isDirectory ? `${entry.path}/` : entry.path)
  const inserted = entry.isDirectory || after.startsWith(' ') ? reference : `${reference} `
  return { text: before + inserted + after, caret: before.length + inserted.length }
}

/** Directory part of a workspace path, for the picker's secondary line. */
export function parentPath(path: string): string {
  const slash = path.lastIndexOf('/')
  return slash < 0 ? '' : path.slice(0, slash)
}
