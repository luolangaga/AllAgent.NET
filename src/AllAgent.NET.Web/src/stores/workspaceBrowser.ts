import { defineStore, ref } from '@/lib/store'
import { invoke } from '@/bridge'
import type { WorkspaceFileContent, WorkspaceFileEntry, WorkspaceTreeEntry, WorkspaceTreeResult } from '@/types'

// Common noise the tree hides by default; the toggle reveals it on demand.
const IGNORED = new Set([
  '.git', '.hg', '.svn', '.vs', '.idea', '.vscode', 'node_modules', 'dist', 'build', 'bin', 'obj',
  'out', 'target', '.venv', 'venv', '__pycache__', '.next', '.turbo', 'coverage',
])

export function isIgnoredName(name: string): boolean {
  return IGNORED.has(name.toLowerCase())
}

export const useWorkspaceBrowserStore = defineStore('workspace-browser', () => {
  const loaded = ref(false)
  const hideIgnored = ref(true)
  // One directory level per key; children load lazily when a folder is expanded.
  const trees = ref<Record<string, WorkspaceTreeEntry[]>>({})
  const truncated = ref<Record<string, boolean>>({})
  const errors = ref<Record<string, string>>({})
  const expanded = ref<string[]>(['.'])
  const loadingDirs = ref<string[]>([])
  const query = ref('')
  const matches = ref<WorkspaceFileEntry[]>([])
  const searching = ref(false)
  // Preview state for the right-hand pane of the bubble.
  const file = ref<WorkspaceFileContent | null>(null)
  const fileLoading = ref(false)
  const fileError = ref('')

  async function loadTree(path = '.'): Promise<void> {
    if (loadingDirs.value.includes(path)) return
    loadingDirs.value = [...loadingDirs.value, path]
    try {
      const result = await invoke<WorkspaceTreeResult>('workspace.tree', { path })
      trees.value = { ...trees.value, [path]: result.entries }
      truncated.value = { ...truncated.value, [path]: result.truncated }
      errors.value = { ...errors.value, [path]: '' }
      loaded.value = true
    } catch (error) {
      errors.value = { ...errors.value, [path]: error instanceof Error ? error.message : String(error) }
    } finally {
      loadingDirs.value = loadingDirs.value.filter(item => item !== path)
    }
  }

  async function toggleDirectory(path: string): Promise<void> {
    if (expanded.value.includes(path)) {
      expanded.value = expanded.value.filter(item => item !== path)
      return
    }
    if (!trees.value[path]) await loadTree(path)
    expanded.value = [...expanded.value, path]
  }

  /** Opens the preview pane and asks the host for the file; failures land in fileError. */
  async function openFile(path: string): Promise<void> {
    fileLoading.value = true
    fileError.value = ''
    file.value = null
    try {
      file.value = await invoke<WorkspaceFileContent>('workspace.readFile', { path })
    } catch (error) {
      fileError.value = error instanceof Error ? error.message : String(error)
    } finally {
      fileLoading.value = false
    }
  }

  function closeFile(): void {
    file.value = null
    fileError.value = ''
  }

  /** Non-empty query switches the left pane to flat name matches served by `workspace.files`. */
  async function search(text: string): Promise<void> {
    query.value = text
    if (!text.trim()) { matches.value = []; return }
    searching.value = true
    try {
      matches.value = await invoke<WorkspaceFileEntry[]>('workspace.files', { query: text, limit: 80 })
    } catch {
      matches.value = []
    } finally {
      searching.value = false
    }
  }

  /** Re-reads every expanded level; called when the panel opens and after workspace switches. */
  async function refresh(): Promise<void> {
    const dirs = ['.', ...expanded.value.filter(item => item !== '.')]
    trees.value = {}
    for (const dir of dirs) await loadTree(dir)
  }

  function reset(): void {
    loaded.value = false
    trees.value = {}
    truncated.value = {}
    errors.value = {}
    expanded.value = ['.']
    query.value = ''
    matches.value = []
    file.value = null
    fileError.value = ''
  }

  return {
    loaded, hideIgnored, trees, truncated, errors, expanded, loadingDirs, query, matches, searching,
    file, fileLoading, fileError, loadTree, toggleDirectory, openFile, closeFile, search, refresh, reset,
  }
})
