<template>
  <div
    class="conv-item"
    :class="{ active }"
    @click="emit('select')"
    @dblclick="startRename"
  >
    <svg class="conv-icon" viewBox="0 0 18 18" fill="none">
      <path d="M3 3h12a1 1 0 011 1v8a1 1 0 01-1 1H6l-3 3V4a1 1 0 011-1z" stroke="currentColor" stroke-width="1.2" stroke-linejoin="round"/>
    </svg>

    <template v-if="!collapsed">
      <n-input
        v-if="renaming"
        ref="renameInputRef"
        v-model:value="renameValue"
        size="tiny"
        @blur="commitRename"
        @keydown.enter="commitRename"
        @keydown.escape="renaming = false"
        @click.stop
      />
      <span v-else class="conv-item-title">{{ conversation.title }}</span>

      <div class="conv-item-actions" @click.stop>
        <n-button text size="tiny" title="重命名" @click="startRename">
          <template #icon>
            <svg width="14" height="14" viewBox="0 0 14 14" fill="none">
              <path d="M9.5 2.5l2 2-7 7H2.5v-2l7-7z" stroke="currentColor" stroke-width="1.2" stroke-linejoin="round"/>
            </svg>
          </template>
        </n-button>
        <n-button text size="tiny" type="error" title="删除" @click="emit('delete')">
          <template #icon>
            <svg width="14" height="14" viewBox="0 0 14 14" fill="none">
              <path d="M2 4h10M5 4V2.5h4V4M5.5 6.5v4M8.5 6.5v4M3 4l.8 7.5h6.4L11 4" stroke="currentColor" stroke-width="1.2" stroke-linecap="round" stroke-linejoin="round"/>
            </svg>
          </template>
        </n-button>
      </div>
    </template>
  </div>
</template>

<script setup lang="ts">
import { ref, nextTick } from 'vue'
import { NButton, NInput } from 'naive-ui'
import type { Conversation } from '@/types'

const props = defineProps<{
  conversation: Conversation
  active: boolean
  collapsed: boolean
}>()

const emit = defineEmits<{
  select: []
  rename: [string]
  delete: []
}>()

const renaming = ref(false)
const renameValue = ref('')
const renameInputRef = ref<InstanceType<typeof NInput>>()
async function startRename() {
  if (props.collapsed) return
  renaming.value = true
  renameValue.value = props.conversation.title
  await nextTick()
  renameInputRef.value?.focus()
}

function commitRename() {
  const v = renameValue.value.trim()
  if (v && v !== props.conversation.title) emit('rename', v)
  renaming.value = false
}
</script>

<style scoped>
.conv-icon {
  flex-shrink: 0;
  width: 18px;
  height: 18px;
  color: var(--n-text-color-3);
}
.conv-item-title {
  flex: 1;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  font-size: 14px;
  color: inherit;
  min-width: 0;
}
</style>
