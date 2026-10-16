<script setup lang="ts">
import PixelIcon from '@/components/PixelIcon.vue'
import { Tag as PxTag } from '@pixelium/web-vue/es'

/**
 * CapabilityGrid —— 六张能力卡片（区块 id="capabilities"）。
 *
 * 文案与数字全部取自 SITE-CONTENT.md 的事实清单，未新增功能、数字或链接。
 * 视觉复用全局工具类：.section / .shell / .stack / .panel / .hover-lift /
 * .eyebrow / .muted / .tiny / .glitch-host + .glitch，入场走 v-reveal。
 */

/** 强调色：只映射到设计令牌，避免裸色值 */
type Accent = 'mint' | 'amber' | 'cyan' | 'violet' | 'rose' | 'lime'

/** PxTag 的 theme 取值（对应 @pixelium/web-vue TagProps['theme'] 的子集） */
type TagTheme = 'success' | 'warning' | 'info' | 'sakura' | 'danger' | 'notice'

interface Capability {
  /** PixelIcon 图标名（见 PixelIcon.vue 的 ICONS） */
  icon: string
  /** 卡片右上角的等宽代号 */
  code: string
  title: string
  desc: string
  /** 底部 1~2 个 PxTag 关键词 */
  tags: string[]
  accent: Accent
  theme: TagTheme
}

/** 图标渲染边长：24px 栅格 + 4px 像素点 */
const ICON_SIZE = 28

const CAPABILITIES: Capability[] = [
  {
    icon: 'terminal',
    code: 'run_shell',
    title: '结构化命令执行',
    desc: '只接受 exe + args 字面量：先序列化成 Base64，交给固定可信脚本 run_shell.ps1，以 pwsh -NoLogo -NoProfile -NonInteractive -CommandWithArgs 启动。没有命令字符串，也没有模板拼接，参数不会被 PowerShell 二次解析。',
    tags: ['PowerShell 7.6', 'JSON → Base64'],
    accent: 'mint',
    theme: 'success',
  },
  {
    icon: 'shield',
    code: 'execute_code',
    title: 'CodeAct 单入口',
    desc: '模型侧只暴露 execute_code 一个入口，用 Python 组织步骤，代码在隔离沙箱里跑；真正的文件、命令、桌面与浏览器动作由 35 个 C# 宿主工具执行，其中 12 个是 computer_* 桌面工具。',
    tags: ['35 个宿主工具', '12 个 computer_*'],
    accent: 'amber',
    theme: 'warning',
  },
  {
    icon: 'sandbox',
    code: 'hyperlight / monty',
    title: '双沙箱隔离',
    desc: '优先 Hyperlight 微虚拟机，启动失败自动兜底到 Monty —— Rust 实现的 Python 子集，不需要虚拟化、Docker 或管理员权限。每次执行恢复干净快照，Monty 内存上限 128 MiB，模型服务凭据不进 worker。',
    tags: ['Monty 128 MiB', '凭据不进 worker'],
    accent: 'cyan',
    theme: 'info',
  },
  {
    icon: 'lock',
    code: 'full / ask / plan',
    title: '路径闸门与目录授权',
    desc: '路径先过闸门：拒绝 UNC 工作区、CON/PRN/AUX/NUL 这类歧义名与点/空格结尾、README.md:secret 形式的备用数据流、普通模式下的 reparse point。权限三档决定每一步要不要你点头。',
    tags: ['三档权限', '拒绝 UNC / ADS'],
    accent: 'violet',
    theme: 'sakura',
  },
  {
    icon: 'clipboard',
    code: 'ctrl + v',
    title: '截图直接进对话',
    desc: '复制 Windows 截图后，在输入框按 Ctrl+V 直接提交，原生 PNG 与 DIB/DIBV5 剪贴板都能识别；也可以用 @截图.png 引用路径，图片随消息一起保存进会话。',
    tags: ['原生 PNG / DIB', '@路径引用'],
    accent: 'rose',
    theme: 'danger',
  },
  {
    icon: 'branch',
    code: 'subagents',
    title: '子代理与扩展',
    desc: 'explore / general / investigate / modify / review / verify 六种角色，支持单任务、并行（最多 8 个任务、6 个并发）与串行链；内置 Skills、MCP 服务与 DLL 插件，联网搜索与网页读取不需要搜索 API Key。',
    tags: ['6 种角色', 'Skills / MCP / 插件'],
    accent: 'lime',
    theme: 'notice',
  },
]
</script>

<template>
  <section class="section" id="capabilities">
    <div class="shell">
      <header class="stack cap-intro">
        <p class="eyebrow">Capabilities</p>
        <h2 class="cap-intro__title">模型只有一个入口，能力在宿主侧收口</h2>
        <p class="muted cap-intro__desc">
          模型只能调用 execute_code，用 Python 组织步骤；Python 在 Hyperlight microVM 或 Monty 沙箱里运行，
          文件、命令、桌面与浏览器操作全部经 C# 宿主工具执行，按你选择的权限档位放行。
        </p>
      </header>

      <div class="cap-grid">
        <article
          v-for="(card, i) in CAPABILITIES"
          :key="card.code"
          v-reveal="{ delay: 80 * i }"
          class="panel hover-lift cap-card glitch-host"
          :class="`cap-card--${card.accent}`"
        >
          <span class="cap-card__bar" aria-hidden="true"></span>
          <span class="tiny cap-card__no" aria-hidden="true">{{ String(i + 1).padStart(2, '0') }}</span>

          <div class="cap-card__head">
            <span class="cap-card__icon">
              <PixelIcon :name="card.icon" :size="ICON_SIZE" />
            </span>
            <span class="glitch cap-card__code">{{ card.code }}</span>
          </div>

          <h3 class="cap-card__title">{{ card.title }}</h3>
          <p class="muted cap-card__desc">{{ card.desc }}</p>

          <div class="cap-card__tags">
            <PxTag
              v-for="tag in card.tags"
              :key="tag"
              size="small"
              variant="outline"
              :theme="card.theme"
            >
              {{ tag }}
            </PxTag>
          </div>
        </article>
      </div>
    </div>
  </section>
</template>

<style scoped>
/* ---------- 区块抬头 ---------- */
.cap-intro {
  max-width: 74ch;
}

.cap-intro__title {
  font-size: var(--fs-h2);
  color: var(--text);
}

.cap-intro__desc {
  max-width: 66ch;
  font-size: var(--fs-body);
}

/* ---------- 网格：小屏单列 → 中屏两列 → 大屏三列 ---------- */
.cap-grid {
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  gap: var(--grid-3);
  margin-top: calc(var(--grid-3) * 2);
}

@media (min-width: 720px) {
  .cap-grid {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }
}

@media (min-width: 1120px) {
  .cap-grid {
    grid-template-columns: repeat(3, minmax(0, 1fr));
  }
}

/* ---------- 卡片外壳 ---------- */
.cap-card {
  --cap-accent: var(--c-mint);
  --cap-hair: calc(var(--bit) / 2); /* 2px 描边＝半个像素点 */
  display: flex;
  flex-direction: column;
  gap: var(--grid);
  padding: var(--grid-3);
}

.cap-card--mint {
  --cap-accent: var(--c-mint);
}

.cap-card--amber {
  --cap-accent: var(--c-amber);
}

.cap-card--cyan {
  --cap-accent: var(--c-cyan);
}

.cap-card--violet {
  --cap-accent: var(--c-violet);
}

.cap-card--rose {
  --cap-accent: var(--c-rose);
}

.cap-card--lime {
  --cap-accent: var(--c-lime);
}

/* 悬停时描边换成卡片强调色（位移/硬阴影交给 .hover-lift） */
.cap-card:hover {
  border-color: var(--cap-accent);
}

/* 顶部像素色带：4px 高，8px 断续 */
.cap-card__bar {
  display: block;
  height: var(--bit);
  background-image: repeating-linear-gradient(
    90deg,
    var(--cap-accent) 0 var(--grid),
    transparent var(--grid) var(--grid-2)
  );
}

.cap-card__no {
  position: absolute;
  top: var(--grid);
  right: var(--grid);
  color: var(--c-line-strong);
  font-size: var(--fs-small);
  letter-spacing: 0.12em;
}

/* ---------- 悬停色差错位（steps() 低帧率关键帧） ---------- */
.cap-card::before,
.cap-card::after {
  content: '';
  position: absolute;
  inset: 0;
  pointer-events: none;
  opacity: 0;
  border: 2px solid var(--cap-accent);
  transition: opacity var(--t-fast) var(--ease-step);
}

.cap-card::before {
  border-color: var(--c-cyan);
  transform: translate(calc(var(--bit) * -1), var(--bit));
}

.cap-card::after {
  border-color: var(--c-rose);
  transform: translate(var(--bit), calc(var(--bit) * -1));
}

.cap-card:hover::before {
  opacity: 0.75;
  animation: cap-chroma 520ms steps(4, end) infinite;
}

.cap-card:hover::after {
  opacity: 0.55;
  animation: cap-chroma 520ms steps(4, end) infinite reverse;
}

@keyframes cap-chroma {
  0% {
    transform: translate(calc(var(--bit) * -1), var(--bit));
  }
  100% {
    transform: translate(var(--bit), calc(var(--bit) * -1));
  }
}

/* ---------- 图标 + 代号 ---------- */
.cap-card__head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--grid);
}

.cap-card__icon {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  flex: none;
  width: calc(var(--grid-3) + var(--grid)); /* 32px：28px 图标 + 2×2px 描边，正好贴合 */
  height: calc(var(--grid-3) + var(--grid));
  color: var(--cap-accent);
  background: var(--c-void);
  border: var(--cap-hair) solid var(--cap-accent);
}

.cap-card__code {
  font-family: var(--font-mono);
  font-size: var(--fs-micro);
  letter-spacing: 0.06em;
  line-height: 1.5;
  text-align: right;
  color: var(--cap-accent);
}

/* ---------- 正文 ---------- */
.cap-card__title {
  font-size: var(--fs-h3);
  color: var(--text);
}

.cap-card__desc {
  font-size: var(--fs-small);
  line-height: 1.85;
}

/* ---------- 底部标签 ---------- */
.cap-card__tags {
  display: flex;
  flex-wrap: wrap;
  gap: var(--grid);
  margin-top: auto;
  padding-top: var(--grid);
  border-top: var(--cap-hair) dashed var(--c-line);
}

/* 尊重系统降级：不要留下静态错位描边 */
@media (prefers-reduced-motion: reduce) {
  .cap-card::before,
  .cap-card::after {
    display: none;
  }
}
</style>
