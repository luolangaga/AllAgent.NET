import { defineStore, ref } from '@/lib/store'

/**
 * The model configuration currently chosen in the chat composer. Shared so auxiliary surfaces
 * (the Git panel's AI commit messages) can default to the same model the user is talking to.
 */
export const useModelSelectionStore = defineStore('model-selection', () => {
  const configId = ref<string | null>(null)

  function select(id: string | null): void {
    configId.value = id || null
  }

  return { configId, select }
})
