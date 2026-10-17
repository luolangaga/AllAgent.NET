import { defineStore, ref } from '@/lib/store'
import type { Skill, SkillCatalogSnapshot, SkillInfo } from '@/types'
import { invoke } from '@/bridge'

// The terminal host serves the instruction-skill catalog (SKILL.md);
// the desktop host still serves the legacy store-based skill list.
export const useSkillsStore = defineStore('skills', () => {
  const skills = ref<SkillInfo[]>([])
  const legacy = ref<Skill[]>([])
  const diagnostics = ref<string[]>([])
  const loaded = ref(false)
  const available = ref(false)

  async function loadAll() {
    try {
      const snapshot = await invoke<SkillCatalogSnapshot | Skill[]>('skills.list')
      if (Array.isArray(snapshot)) {
        legacy.value = snapshot
        skills.value = []
        diagnostics.value = []
        available.value = false
      } else {
        skills.value = snapshot.skills ?? []
        diagnostics.value = snapshot.diagnostics ?? []
        legacy.value = []
        available.value = true
      }
    } finally {
      loaded.value = true
    }
  }

  async function run(action: () => Promise<unknown>) {
    await action()
    await loadAll()
  }

  const setEnabled = (name: string, enabled: boolean) => run(() => invoke('skills.setEnabled', { name, enabled }))
  const remove = (name: string) => run(() => invoke('skills.remove', { name }))
  const create = (name: string, description: string, instructions: string, scope: string) =>
    run(() => invoke('skills.create', { name, description, instructions, scope }))
  const importText = (content: string, scope: string) => run(() => invoke('skills.importText', { content, scope }))
  const body = (name: string) => invoke<string>('skills.body', { name })

  // Legacy host operations (desktop bridge).
  const legacyToggle = (id: string, enabled: boolean) => run(() => invoke('skills.toggle', { id, enabled }))
  const legacyRemove = (id: string) => run(() => invoke('skills.delete', { id }))

  return { skills, legacy, diagnostics, loaded, available, loadAll, setEnabled, remove, create, importText, body, legacyToggle, legacyRemove }
})
