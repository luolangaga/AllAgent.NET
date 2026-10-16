<script setup lang="ts">
import { Timeline, TimelineItem, Tag } from '@pixelium/web-vue/es'
import PixelIcon from '@/components/PixelIcon.vue'

/**
 * WorkflowSection —— 子代理协作链与任务运行中的控制手段。
 */
type AgentRole = {
  key: string
  title: string
  content: string
  footer: string
  theme: 'primary' | 'info' | 'warning' | 'success' | 'sakura' | 'notice' | 'danger'
}

const ROLES: AgentRole[] = [
  {
    key: 'explore',
    title: 'explore · 只读调查',
    content: '在代码库里搜索与定位，返回证据和引用，不修改任何文件。',
    footer: '只读角色：无写权限',
    theme: 'info',
  },
  {
    key: 'general',
    title: 'general · 实现与研究',
    content: '承担实现、检索与资料整理，可写入文件并调用宿主工具。',
    footer: '可写角色：受工作区与权限约束',
    theme: 'primary',
  },
  {
    key: 'investigate',
    title: 'investigate · 定位问题',
    content: '复现线索、排查根因，输出可验证的结论与修改建议。',
    footer: '与 verify 配合形成证据链',
    theme: 'notice',
  },
  {
    key: 'modify',
    title: 'modify · 按范围修改',
    content: '在指定范围内实现修复，保留无关改动，并报告实际变更。',
    footer: '只改指定文件，避免并发冲突',
    theme: 'sakura',
  },
  {
    key: 'review',
    title: 'review · 只报有证据的问题',
    content: '检查正确性、回归与边界条件，逐条给出严重程度。',
    footer: '不猜测、不扩大范围',
    theme: 'warning',
  },
  {
    key: 'verify',
    title: 'verify · 跑起来验证',
    content: '执行构建、测试或复现步骤，报告真实结果与失败原因。',
    footer: '没有真实验证就不算完成',
    theme: 'success',
  },
]

const CONTROLS: { icon: string; label: string; hint: string }[] = [
  { icon: 'arrow', label: '/steer', hint: '任务运行中继续输入，在模型步骤边界作为指导送达' },
  { icon: 'layers', label: '/queue add', hint: '显式排队下一条任务，不与当前任务抢上下文' },
  { icon: 'bolt', label: '双击 Esc', hint: '确认后停止当前任务，等待子代理实际收尾' },
  { icon: 'branch', label: '/diff · /undo · /redo', hint: '查看文件变更、撤回与重做' },
  { icon: 'eye', label: 'Ctrl+E', hint: '展开执行代码与工具调用结果' },
  { icon: 'cpu', label: '/sandbox · /status', hint: '查看当前沙箱后端与实际运行状态' },
]
</script>

<template>
  <section id="workflow" class="section workflow">
    <div class="shell">
      <header class="head">
        <p class="eyebrow">Orchestration</p>
        <h2>一个任务，六个角色</h2>
        <p class="muted intro">
          子代理负责把"调查 → 修改 → 验证"拆开做：可以只派一个，也可以并行多个，或按链式顺序传递上一环的结果。
          子代理不能向用户提问，缺信息会回传主代理。
        </p>
        <div class="chips row">
          <Tag theme="primary" variant="outline" size="small">并行最多 8 个任务</Tag>
          <Tag theme="notice" variant="outline" size="small">6 个并发</Tag>
          <Tag theme="success" variant="outline" size="small">停止会等待实际收尾</Tag>
        </div>
      </header>

      <div class="grid">
        <div v-reveal class="timeline panel">
          <Timeline size="medium" :content-span="76">
            <TimelineItem
              v-for="role in ROLES"
              :key="role.key"
              :theme="role.theme"
              :title="role.title"
              :content="role.content"
              :footer="role.footer"
            />
          </Timeline>
        </div>

        <aside v-reveal="{ delay: 120 }" class="controls panel">
          <h3 class="controls-title">运行中也能插手</h3>
          <ul>
            <li v-for="item in CONTROLS" :key="item.label">
              <PixelIcon :name="item.icon" :size="20" class="controls-icon" />
              <span class="controls-key">{{ item.label }}</span>
              <span class="controls-hint muted">{{ item.hint }}</span>
            </li>
          </ul>
          <p class="controls-note tiny">
            会话支持历史分支、时间线与后台任务；关闭终端会停止该实例并记录遗留任务为中断。
          </p>
        </aside>
      </div>
    </div>
  </section>
</template>

<style scoped>
.head {
  max-width: 760px;
  margin-bottom: var(--grid-3);
}

.head h2 {
  font-size: var(--fs-h2);
  margin: var(--grid) 0;
}

.intro {
  max-width: 64ch;
}

.chips {
  margin-top: var(--grid-2);
}

.grid {
  display: grid;
  grid-template-columns: minmax(0, 1.4fr) minmax(280px, 1fr);
  gap: var(--grid-3);
  align-items: start;
}

.timeline {
  padding: var(--grid-3) var(--grid-3) var(--grid);
}

.controls {
  padding: var(--grid-3);
}

.controls-title {
  font-size: var(--fs-h3);
  margin-bottom: var(--grid-2);
}

.controls ul {
  list-style: none;
  margin: 0;
  padding: 0;
  display: grid;
  gap: var(--grid-2);
}

.controls li {
  display: grid;
  grid-template-columns: auto auto 1fr;
  align-items: baseline;
  gap: 10px;
  padding-bottom: var(--grid-2);
  border-bottom: 2px dashed rgba(34, 49, 60, 0.7);
}

.controls li:last-child {
  border-bottom: 0;
  padding-bottom: 0;
}

.controls-icon {
  color: var(--c-amber);
  align-self: center;
}

.controls-key {
  font-family: var(--font-mono);
  font-size: var(--fs-small);
  color: var(--c-text);
  white-space: nowrap;
}

.controls-hint {
  font-size: var(--fs-small);
}

.controls-note {
  margin-top: var(--grid-3);
  line-height: 1.7;
}

@media (max-width: 992px) {
  .grid {
    grid-template-columns: minmax(0, 1fr);
  }
}
</style>
