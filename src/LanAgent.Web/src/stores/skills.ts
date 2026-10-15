import { defineStore } from '@/lib/store'
import { ref } from '@/lib/store'
import type { Skill } from '@/types'
import { invoke } from '@/bridge'

export const useSkillsStore = defineStore('skills', () => {
  const skills = ref<Skill[]>([])

  async function loadAll() {
    skills.value = await invoke<Skill[]>('skills.list')
  }

  async function toggle(id: string, enabled: boolean) {
    await invoke('skills.toggle', { id, enabled })
    const s = skills.value.find(s => s.id === id)
    if (s) s.isEnabled = enabled
  }

  async function importFromJson(json: string) {
    const skill = await invoke<Skill>('skills.import', { json })
    skills.value.push(skill)
    return skill
  }

  async function remove(id: string) {
    await invoke('skills.delete', { id })
    skills.value = skills.value.filter(s => s.id !== id)
  }

  return { skills, loadAll, toggle, importFromJson, remove }
})
