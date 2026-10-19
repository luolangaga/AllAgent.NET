import { invoke } from '@/bridge'
import { useNoticesStore } from '@/stores/ui'
import { usePanelsStore } from '@/stores/panels'
import { useWorkspaceBrowserStore } from '@/stores/workspaceBrowser'
import type { WorkspaceFileContent } from '@/types'

// A reference the page cannot serve itself is a local path: web addresses, anchors and
// browser schemes stay with the browser. Workspace files open in the file bubble's preview;
// anything else (directories, outside paths, missing files) goes to the host, which opens it
// in Explorer or the default program instead of the page navigating to a 404.
const WEB_URL = /^[a-z][a-z0-9+.-]*:\/\//i
const BROWSER_SCHEME = /^(?:mailto|tel|data|blob|javascript|about):/i
const FILE_URL = /^file:\/\/\/?/i
const ABSOLUTE_PATH = /^(?:[A-Za-z]:[\\/]|\\\\)/
const RELATIVE_PATH = /^[\w.@()-]+(?:[\\/][\w.@() -]+)*\.[A-Za-z0-9]{1,8}$/

function decoded(target: string): string {
  try { return decodeURIComponent(target) } catch { return target }
}

/** The local path a markdown reference points at; null when the browser should handle it. */
export function referencePath(target: string): string | null {
  const value = decoded(target).trim()
  if (value.length === 0 || value.length > 512) return null
  if (value.startsWith('#') || BROWSER_SCHEME.test(value)) return null
  if (FILE_URL.test(value)) return value.replace(FILE_URL, '')
  return WEB_URL.test(value) ? null : value
}

/** Whether inline code reads as one file path — the shape answers and tool cards print. */
export function isFilePath(text: string): boolean {
  const value = text.trim()
  if (value.length === 0 || value.length > 512 || /[\r\n<>|?*"]/.test(value)) return false
  if (WEB_URL.test(value) || BROWSER_SCHEME.test(value)) return false
  return ABSOLUTE_PATH.test(value) || RELATIVE_PATH.test(value)
}

/** Opens a reference through the file bubble when it is a workspace file, the host otherwise.
 *  False means the reference was left to the browser. */
export function openReference(target: string): boolean {
  const path = referencePath(target)
  if (path == null) return false
  void openFileReference(path)
  return true
}

async function openFileReference(path: string): Promise<void> {
  try {
    const file = await invoke<WorkspaceFileContent>('workspace.readFile', { path })
    const browser = useWorkspaceBrowserStore()
    browser.file = file
    browser.fileError = ''
    browser.fileLoading = false
    usePanelsStore().openPanel('files')
  } catch {
    void invoke<boolean>('shell.openPath', { path }).catch(error => useNoticesStore().report(error))
  }
}
