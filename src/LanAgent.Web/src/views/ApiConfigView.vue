<template>
  <div class="api-config-view page-container">
    <div class="page-header">
      <div>
        <h1>API 配置</h1>
        <p style="color:var(--n-text-color-3);font-size:var(--font-size-200);margin-top:4px">
          配置 AI 服务提供商的 API 端点和密钥
        </p>
      </div>
      <n-button type="primary" @click="openDialog(null)">
        <template #icon>
          <svg width="16" height="16" viewBox="0 0 16 16" fill="none">
            <path d="M8 2v12M2 8h12" stroke="currentColor" stroke-width="1.5" stroke-linecap="round"/>
          </svg>
        </template>
        添加配置
      </n-button>
    </div>

    <!-- Quick-add presets -->
    <div v-if="apiStore.configs.length === 0" style="margin-bottom:24px">
      <p style="color:var(--n-text-color-3);font-size:var(--font-size-200);margin-bottom:12px">快速添加常用提供商：</p>
      <n-space>
        <n-button
          v-for="preset in presets"
          :key="preset.providerId"
          @click="openDialog(null, preset)"
        >
          <span style="margin-right:6px">{{ preset.logo }}</span>
          {{ preset.name }}
        </n-button>
      </n-space>
    </div>

    <div class="provider-grid">
      <ProviderCard
        v-for="cfg in apiStore.configs"
        :key="cfg.id"
        :config="cfg"
        :test-status="apiStore.testStatus[cfg.id] ?? 'idle'"
        @edit="openDialog(cfg)"
        @delete="confirmDelete(cfg)"
        @test="apiStore.testConnection(cfg.id)"
      />
    </div>

    <!-- Add/Edit dialog -->
    <n-modal v-model:show="showDialog" preset="card" :title="editingConfig ? '编辑配置' : '添加 API 配置'"
      style="max-width:560px" :mask-closable="false">
      <n-form label-placement="top">
        <n-form-item label="提供商">
          <n-select v-model:value="form.providerId" :options="providerOptions" />
        </n-form-item>
        <n-form-item label="显示名称">
          <n-input v-model:value="form.displayName" placeholder="例：我的 GPT-4" />
        </n-form-item>
        <n-form-item label="API Base URL">
          <n-input v-model:value="form.baseUrl" :placeholder="baseUrlPlaceholder" />
        </n-form-item>
        <n-form-item label="API Key">
          <n-input v-model:value="form.apiKey" type="password"
            :placeholder="editingConfig ? '留空保持不变' : 'sk-...'" />
        </n-form-item>
        <n-form-item label="默认模型">
          <n-input v-model:value="form.defaultModel" :placeholder="modelPlaceholder" />
        </n-form-item>
        <n-grid :cols="2" :x-gap="16">
          <n-form-item :label="`Temperature (${form.temperature})`">
            <n-slider v-model:value="form.temperature" :min="0" :max="2" :step="0.1" />
          </n-form-item>
          <n-form-item label="Max Tokens">
            <n-input-number v-model:value="form.maxTokens" :min="256" :max="128000" />
          </n-form-item>
        </n-grid>
      </n-form>
      <div v-if="saveError" style="color:var(--n-color-error);font-size:13px;margin-top:12px">
        {{ saveError }}
      </div>
      <template #footer>
        <n-space justify="end">
          <n-button @click="showDialog = false">取消</n-button>
          <n-button type="primary" @click="doSave" :loading="saving">
            {{ editingConfig ? '保存' : '添加' }}
          </n-button>
        </n-space>
      </template>
    </n-modal>

    <!-- Delete confirm dialog -->
    <n-modal v-model:show="showDeleteDialog" preset="card" title="确认删除" style="max-width:400px">
      <p style="color:var(--n-text-color-3)">
        确定要删除 <strong>{{ deletingConfig?.displayName }}</strong> 吗？此操作不可撤销。
      </p>
      <template #footer>
        <n-space justify="end">
          <n-button @click="deletingConfig = null">取消</n-button>
          <n-button type="error" @click="doDelete">删除</n-button>
        </n-space>
      </template>
    </n-modal>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, reactive } from 'vue'
import {
  NButton, NModal, NForm, NFormItem, NInput, NInputNumber,
  NSelect, NSlider, NSpace, NGrid, useMessage
} from 'naive-ui'
import { useApiConfigsStore } from '@/stores/apiConfigs'
import type { ApiConfig } from '@/types'
import ProviderCard from '@/components/ProviderCard.vue'

const apiStore = useApiConfigsStore()
const message = useMessage()

const showDialog = ref(false)
const editingConfig = ref<ApiConfig | null>(null)
const deletingConfig = ref<ApiConfig | null>(null)
const showDeleteDialog = computed({
  get: () => !!deletingConfig.value,
  set: (v) => { if (!v) deletingConfig.value = null },
})
const saving = ref(false)
const saveError = ref('')

const form = reactive({
  providerId: 'openai',
  displayName: '',
  baseUrl: '',
  apiKey: '',
  defaultModel: '',
  temperature: 0.7,
  maxTokens: 4096,
  isEnabled: true,
  extraHeadersJson: '',
})

const presets = [
  { name: 'OpenAI', logo: '🟢', providerId: 'openai', baseUrl: 'https://api.openai.com/v1', defaultModel: 'gpt-4o' },
  { name: 'Anthropic', logo: '🟠', providerId: 'anthropic', baseUrl: 'https://api.anthropic.com', defaultModel: 'claude-sonnet-4-6' },
  { name: 'Gemini', logo: '🔵', providerId: 'gemini', baseUrl: 'https://generativelanguage.googleapis.com', defaultModel: 'gemini-2.0-flash' },
  { name: 'Ollama', logo: '🦙', providerId: 'ollama', baseUrl: 'http://localhost:11434/v1', defaultModel: 'llama3' },
]

const providerOptions = [
  { label: 'OpenAI / 兼容格式', value: 'openai' },
  { label: 'Anthropic (Claude)', value: 'anthropic' },
  { label: 'Google Gemini', value: 'gemini' },
  { label: 'Ollama (本地)', value: 'ollama' },
  { label: '自定义', value: 'custom' },
]

const baseUrlPlaceholder = computed(() => ({
  openai: 'https://api.openai.com/v1',
  anthropic: 'https://api.anthropic.com',
  gemini: 'https://generativelanguage.googleapis.com',
  ollama: 'http://localhost:11434/v1',
  custom: 'https://your-api.example.com/v1',
}[form.providerId] ?? ''))

const modelPlaceholder = computed(() => ({
  openai: 'gpt-4o',
  anthropic: 'claude-sonnet-4-6',
  gemini: 'gemini-2.0-flash',
  ollama: 'llama3',
  custom: 'model-name',
}[form.providerId] ?? ''))

function openDialog(config: ApiConfig | null, preset?: typeof presets[0]) {
  editingConfig.value = config
  saveError.value = ''
  if (config) {
    Object.assign(form, {
      providerId: config.providerId,
      displayName: config.displayName,
      baseUrl: config.baseUrl,
      apiKey: '',
      defaultModel: config.defaultModel,
      temperature: config.temperature,
      maxTokens: config.maxTokens,
      isEnabled: config.isEnabled,
      extraHeadersJson: config.extraHeadersJson ?? '',
    })
  } else if (preset) {
    Object.assign(form, {
      providerId: preset.providerId,
      displayName: preset.name,
      baseUrl: preset.baseUrl,
      apiKey: '',
      defaultModel: preset.defaultModel,
      temperature: 0.7,
      maxTokens: 4096,
      isEnabled: true,
      extraHeadersJson: '',
    })
  } else {
    Object.assign(form, {
      providerId: 'openai',
      displayName: '',
      baseUrl: '',
      apiKey: '',
      defaultModel: '',
      temperature: 0.7,
      maxTokens: 4096,
      isEnabled: true,
      extraHeadersJson: '',
    })
  }
  showDialog.value = true
}

async function doSave() {
  if (!form.displayName.trim()) { saveError.value = '请填写显示名称'; return }
  if (!form.baseUrl.trim()) { saveError.value = '请填写 API Base URL'; return }
  if (!form.defaultModel.trim()) { saveError.value = '请填写默认模型'; return }
  saving.value = true
  saveError.value = ''
  try {
    await apiStore.save({
      id: editingConfig.value?.id,
      ...form,
    })
    showDialog.value = false
    message.success(editingConfig.value ? '配置已更新' : '配置已添加')
  } catch (e: any) {
    saveError.value = e.message ?? '保存失败'
  } finally {
    saving.value = false
  }
}

function confirmDelete(cfg: ApiConfig) {
  deletingConfig.value = cfg
}

async function doDelete() {
  if (!deletingConfig.value) return
  try {
    await apiStore.remove(deletingConfig.value.id)
    message.success('已删除')
  } catch (e: any) {
    message.error(e.message ?? '删除失败')
  }
  deletingConfig.value = null
}
</script>