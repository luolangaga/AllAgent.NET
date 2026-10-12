<template>
  <div class="skills-view page-container">
    <div class="page-header">
      <div>
        <h1>工具管理</h1>
        <p style="color:var(--n-text-color-3);font-size:var(--font-size-200);margin-top:4px">
          开启或关闭 AI 可调用的工具，也可以导入新技能
        </p>
      </div>
      <n-space>
        <n-button @click="showImportDialog = true">
          <template #icon>
            <svg width="16" height="16" viewBox="0 0 16 16" fill="none">
              <path d="M8 2v9M4 7l4 4 4-4M2 13h12" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"/>
            </svg>
          </template>
          导入技能
        </n-button>
      </n-space>
    </div>

    <!-- Builtin skills section -->
    <div v-if="builtinSkills.length > 0" class="tool-section">
      <div class="section-label">
        <svg width="16" height="16" viewBox="0 0 16 16" fill="none"><path d="M8 1l2 5h5l-4 3.5 1.5 5L8 11.5 3.5 14.5 5 9.5 1 6h5z" fill="currentColor"/></svg>
        内置工具
      </div>
      <div class="tool-list">
        <div
          v-for="skill in builtinSkills"
          :key="skill.id"
          class="tool-item"
          :class="{ disabled: !skill.isEnabled }"
        >
          <div class="tool-item-left">
            <span class="tool-icon-lg">{{ builtinIcon(skill.name) }}</span>
            <div class="tool-info">
              <div class="tool-name-row">
                <span class="tool-name">{{ friendlyName(skill.name) }}</span>
                <n-tag size="small" :type="skill.isEnabled ? 'success' : 'default'">
                  {{ skill.isEnabled ? '已启用' : '已禁用' }}
                </n-tag>
              </div>
              <div class="tool-desc">{{ skill.description }}</div>
              <div class="tool-meta">
                <span v-for="p in paramNames(skill)" :key="p" class="tool-param-tag">{{ p }}</span>
              </div>
            </div>
          </div>
          <n-switch :value="skill.isEnabled" @update:value="toggleSkill(skill.id, $event)" />
        </div>
      </div>
    </div>

    <!-- Custom skills section -->
    <div v-if="customSkills.length > 0" class="tool-section">
      <div class="section-label">
        <svg width="16" height="16" viewBox="0 0 16 16" fill="none"><path d="M3.5 2.5l9 4.5-9 4.5V2.5z" stroke="currentColor" stroke-width="1.2" stroke-linejoin="round"/></svg>
        自定义技能
      </div>
      <div class="tool-list">
        <div
          v-for="skill in customSkills"
          :key="skill.id"
          class="tool-item"
          :class="{ disabled: !skill.isEnabled }"
        >
          <div class="tool-item-left">
            <span class="tool-icon-lg">{{ skill.type === 'Shell' ? '💻' : '🧩' }}</span>
            <div class="tool-info">
              <div class="tool-name-row">
                <span class="tool-name">{{ friendlyName(skill.name) }}</span>
                <n-tag size="small" :type="skill.isEnabled ? 'success' : 'default'">
                  {{ skill.isEnabled ? '已启用' : '已禁用' }}
                </n-tag>
                <n-tag size="small" :type="skill.type === 'Shell' ? 'warning' : 'info'">
                  {{ skill.type === 'Shell' ? '命令行' : '自定义' }}
                </n-tag>
              </div>
              <div class="tool-desc">{{ skill.description || '暂无描述' }}</div>
              <div v-if="skill.commandTemplate" class="tool-meta">
                <code class="tool-cmd">{{ skill.commandTemplate }}</code>
              </div>
              <div class="tool-meta">
                <span v-for="p in paramNames(skill)" :key="p" class="tool-param-tag">{{ p }}</span>
              </div>
            </div>
          </div>
          <n-space vertical align="center" :size="8">
            <n-switch :value="skill.isEnabled" @update:value="toggleSkill(skill.id, $event)" />
            <n-button text size="tiny" type="error" @click="deleteSkill(skill)">
              <template #icon>
                <svg width="12" height="12" viewBox="0 0 12 12" fill="none">
                  <path d="M2 3h8M5 3V2h2v1M4.5 5v4M7.5 5v4M3 3l.7 7h4.6L9 3" stroke="currentColor" stroke-width="1" stroke-linecap="round" stroke-linejoin="round"/>
                </svg>
              </template>
              删除
            </n-button>
          </n-space>
        </div>
      </div>
    </div>

    <!-- Empty state -->
    <n-empty
      v-if="skillsStore.skills.length === 0"
      description="暂无技能"
      style="margin-top:80px"
    >
      <template #extra>
        <n-button @click="showImportDialog = true">导入技能</n-button>
      </template>
    </n-empty>

    <!-- Import dialog -->
    <n-modal v-model:show="showImportDialog" title="导入技能" preset="card" style="max-width:560px">
      <p style="color:var(--n-text-color-3);font-size:13px;margin-bottom:16px">
        粘贴 OpenAI function-calling 格式的 JSON，可包含 <code>command</code> 字段用于执行本地命令
      </p>
      <n-input
        v-model:value="importJson"
        type="textarea"
        :autosize="{ minRows: 8, maxRows: 16 }"
        placeholder='{"name":"my_skill","description":"...","parameters":{"type":"object","properties":{}}}'
      />
      <div v-if="importError" style="color:var(--n-color-error);font-size:13px;margin-top:12px">
        {{ importError }}
      </div>
      <template #footer>
        <n-space justify="end">
          <n-button @click="showImportDialog = false">取消</n-button>
          <n-button type="primary" @click="doImport" :disabled="!importJson.trim()">导入</n-button>
        </n-space>
      </template>
    </n-modal>
  </div>
</template>

<script setup lang="ts">
import { ref, computed } from 'vue'
import { NButton, NModal, NInput, NSpace, NEmpty, NSwitch, NTag, useMessage } from 'naive-ui'
import { useSkillsStore } from '@/stores/skills'
import type { Skill } from '@/types'

const skillsStore = useSkillsStore()
const message = useMessage()
const showImportDialog = ref(false)
const importJson = ref('')
const importError = ref('')

const builtinSkills = computed(() => skillsStore.skills.filter(s => s.type === 'Builtin'))
const customSkills = computed(() => skillsStore.skills.filter(s => s.type !== 'Builtin'))

const builtinIcons: Record<string, string> = {
  shell_exec: '⚡',
  file_read: '📖',
  file_write: '✏️',
  file_edit: '🔧',
  list_directory: '📁',
}

const builtinNames: Record<string, string> = {
  shell_exec: '执行命令',
  file_read: '读取文件',
  file_write: '写入文件',
  file_edit: '编辑文件',
  list_directory: '浏览目录',
}

function builtinIcon(name: string) {
  return builtinIcons[name] ?? '🔩'
}

function friendlyName(name: string) {
  return builtinNames[name] ?? name
}

function paramNames(skill: Skill) {
  try {
    const p = JSON.parse(skill.parametersJson)
    return Object.keys(p.properties ?? {})
  } catch { return [] }
}

async function toggleSkill(id: string, enabled: boolean) {
  try {
    await skillsStore.toggle(id, enabled)
    message.success(enabled ? '已启用' : '已禁用')
  } catch (e: any) {
    message.error(e.message ?? '操作失败')
  }
}

async function deleteSkill(skill: Skill) {
  try {
    await skillsStore.remove(skill.id)
    message.success(`已删除 ${skill.name}`)
  } catch (e: any) {
    message.error(e.message ?? '删除失败')
  }
}

async function doImport() {
  importError.value = ''
  try {
    JSON.parse(importJson.value)
    await skillsStore.importFromJson(importJson.value)
    importJson.value = ''
    showImportDialog.value = false
    message.success('导入成功')
  } catch (e: any) {
    importError.value = e.message ?? '无效的 JSON 格式'
  }
}
</script>

<style scoped>
.tool-section { margin-bottom: var(--space-8); }
.section-label {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: var(--font-size-200);
  font-weight: var(--font-weight-semibold);
  color: var(--n-text-color-3);
  text-transform: uppercase;
  letter-spacing: 0.06em;
  margin-bottom: var(--space-3);
}
.tool-list {
  display: flex;
  flex-direction: column;
  gap: var(--space-3);
}
.tool-item {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: var(--space-4);
  padding: var(--space-4);
  border: 1px solid var(--n-border-color);
  border-radius: var(--radius-8);
  background: var(--n-color);
  transition: all var(--motion-duration-fast);
}
.tool-item:hover { border-color: var(--n-border-color-hover, var(--n-primary-color)); }
.tool-item.disabled { opacity: 0.55; }
.tool-item-left {
  display: flex;
  gap: var(--space-3);
  flex: 1;
  min-width: 0;
}
.tool-icon-lg { font-size: 24px; flex-shrink: 0; margin-top: 2px; }
.tool-info { flex: 1; min-width: 0; }
.tool-name-row {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 4px;
}
.tool-name {
  font-weight: var(--font-weight-semibold);
  font-size: var(--font-size-400);
  color: var(--n-text-color);
}
.tool-desc {
  font-size: var(--font-size-200);
  color: var(--n-text-color-3);
  line-height: 1.5;
  margin-bottom: 6px;
}
.tool-meta {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
}
.tool-param-tag {
  font-size: 11px;
  padding: 1px 6px;
  background: var(--n-color-hover, #f0f0f0);
  border-radius: var(--radius-4);
  color: var(--n-text-color-3);
  font-family: var(--font-mono, monospace);
}
.tool-cmd {
  font-size: var(--font-size-200);
  padding: 2px 6px;
  background: var(--n-color-hover, #f0f0f0);
  border-radius: var(--radius-4);
  color: var(--n-text-color-2);
  font-family: var(--font-mono, monospace);
}
</style>