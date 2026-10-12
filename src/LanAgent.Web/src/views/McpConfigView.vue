<template>
  <div class="mcp-config-view">
    <div class="mcp-header">
      <h2>MCP 服务器</h2>
      <n-button type="primary" size="small" @click="showAdd = true">添加服务器</n-button>
    </div>

    <div class="mcp-list" v-if="servers.length">
      <div class="mcp-card" v-for="s in servers" :key="s.id">
        <div class="mcp-card-header">
          <div class="mcp-card-info">
            <span class="mcp-name">{{ s.name }}</span>
            <n-tag :type="s.isEnabled ? 'success' : 'default'" size="small">
              {{ s.isEnabled ? '已启用' : '已禁用' }}
            </n-tag>
          </div>
          <div class="mcp-card-actions">
            <n-button size="tiny" :loading="connectingIds.has(s.id)" @click="handleConnect(s)">
              连接
            </n-button>
            <n-button size="tiny" @click="editServer(s)">编辑</n-button>
            <n-button size="tiny" type="error" @click="handleDelete(s)">删除</n-button>
          </div>
        </div>
        <div class="mcp-card-detail">
          <span class="mcp-cmd">{{ s.command }} {{ s.arguments }}</span>
        </div>
      </div>
    </div>

    <div class="mcp-empty" v-else>
      <p>暂无 MCP 服务器配置</p>
      <p class="mcp-hint">MCP 服务器可以为 AI 提供额外的工具能力，如文件操作、代码执行等</p>
    </div>

    <n-modal v-model:show="showAdd" preset="card" :title="editing ? '编辑 MCP 服务器' : '添加 MCP 服务器'" style="max-width:500px">
      <n-form ref="formRef" :model="form" label-placement="left" label-width="80">
        <n-form-item label="名称" path="name">
          <n-input v-model:value="form.name" placeholder="例如：文件服务器" />
        </n-form-item>
        <n-form-item label="命令" path="command">
          <n-input v-model:value="form.command" placeholder="例如：npx 或 http://localhost:3001" />
        </n-form-item>
        <n-form-item label="参数" path="arguments">
          <n-input v-model:value="form.arguments" placeholder="例如：-y @modelcontextprotocol/server-filesystem" />
        </n-form-item>
        <n-form-item label="启用">
          <n-switch v-model:value="form.isEnabled" />
        </n-form-item>
      </n-form>
      <template #action>
        <n-button @click="showAdd = false">取消</n-button>
        <n-button type="primary" @click="handleSave" :loading="saving">保存</n-button>
      </template>
    </n-modal>
  </div>
</template>

<script setup lang="ts">
import { ref, onMounted } from 'vue'
import { NButton, NModal, NForm, NFormItem, NInput, NSwitch, NTag, useMessage } from 'naive-ui'
import { useMcpServersStore } from '@/stores/mcpServers'
import type { McpServerConfig } from '@/types'

const mcpStore = useMcpServersStore()
const message = useMessage()

const servers = mcpStore.servers
const connectingIds = mcpStore.connectingIds

const showAdd = ref(false)
const editing = ref<McpServerConfig | null>(null)
const saving = ref(false)

const form = ref({
  name: '',
  command: '',
  arguments: '',
  isEnabled: true,
})

onMounted(() => mcpStore.loadAll())

function editServer(s: McpServerConfig) {
  editing.value = s
  form.value = {
    name: s.name,
    command: s.command,
    arguments: s.arguments,
    isEnabled: s.isEnabled,
  }
  showAdd.value = true
}

function resetForm() {
  editing.value = null
  form.value = { name: '', command: '', arguments: '', isEnabled: true }
}

async function handleSave() {
  if (!form.value.name || !form.value.command) {
    message.warning('请填写名称和命令')
    return
  }
  saving.value = true
  try {
    await mcpStore.save({
      id: editing.value?.id,
      ...form.value,
    })
    message.success('保存成功')
    showAdd.value = false
    resetForm()
  } catch (e: any) {
    message.error(e.message ?? '保存失败')
  } finally {
    saving.value = false
  }
}

async function handleConnect(s: McpServerConfig) {
  try {
    const ok = await mcpStore.connect(s.id)
    message[ok ? 'success' : 'error'](ok ? '连接成功' : '连接失败')
  } catch (e: any) {
    message.error(e.message ?? '连接失败')
  }
}

async function handleDelete(s: McpServerConfig) {
  await mcpStore.remove(s.id)
  message.success('已删除')
}
</script>

<style scoped>
.mcp-config-view {
  padding: 20px;
  max-width: 700px;
  margin: 0 auto;
}
.mcp-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 20px;
}
.mcp-header h2 {
  margin: 0;
  font-size: 20px;
}
.mcp-list {
  display: flex;
  flex-direction: column;
  gap: 12px;
}
.mcp-card {
  border: 1px solid var(--n-border-color);
  border-radius: 8px;
  padding: 12px 16px;
}
.mcp-card-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
}
.mcp-card-info {
  display: flex;
  align-items: center;
  gap: 8px;
}
.mcp-name {
  font-weight: 600;
  font-size: 15px;
}
.mcp-card-actions {
  display: flex;
  gap: 6px;
}
.mcp-card-detail {
  margin-top: 8px;
  font-size: 13px;
  color: var(--n-text-color-3);
}
.mcp-cmd {
  font-family: monospace;
  background: var(--n-color-target);
  padding: 2px 6px;
  border-radius: 4px;
}
.mcp-empty {
  text-align: center;
  padding: 40px 0;
  color: var(--n-text-color-3);
}
.mcp-hint {
  font-size: 13px;
  margin-top: 8px;
}
</style>