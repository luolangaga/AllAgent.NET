import { defineStore, ref } from '@/lib/store'
import type { CommandInfo, SkillInvokeResult } from '@/types'
import { invoke } from '@/bridge'

// Slash-command catalog served by the terminal host: built-ins, plugin commands and skills.
export const useCommandsStore = defineStore('commands', () => {
  const commands = ref<CommandInfo[]>([])
  const loading = ref(false)
  const loaded = ref(false)
  const available = ref(false)
  // One-shot handoff used when the command palette picks a skill for the composer.
  const pendingDraft = ref<string | null>(null)

  async function loadAll() {
    if (loading.value) return
    loading.value = true
    try {
      commands.value = await invoke<CommandInfo[]>('commands.list')
      available.value = true
    } catch {
      commands.value = []
      available.value = false
    } finally {
      loading.value = false
      loaded.value = true
    }
  }

  /** Runs a user-invocable skill: the host expands the skill instructions into the task. */
  async function invokeSkill(name: string, args: string): Promise<SkillInvokeResult> {
    return invoke<SkillInvokeResult>('skills.invoke', { name, arguments: args })
  }

  function queueDraft(text: string) { pendingDraft.value = text }
  function clearDraft() { pendingDraft.value = null }

  return { commands, loading, loaded, available, pendingDraft, loadAll, invokeSkill, queueDraft, clearDraft }
})
