<script setup lang="ts">
import PixelIcon from '@/components/PixelIcon.vue'

type FooterLink = {
  label: string
  href: string
  /** 站外链接：新标签页打开，并带上 rel=noreferrer */
  external?: boolean
}

type FooterColumn = {
  title: string
  links: FooterLink[]
}

const REPO = 'https://github.com/luolangaga/AllAgent.NET'

const COLUMNS: FooterColumn[] = [
  {
    title: '产品',
    links: [
      { label: '能力', href: '#capabilities' },
      { label: '沙箱', href: '#sandbox' },
      { label: '快速开始', href: '#quickstart' },
      { label: '常见问题', href: '#faq' },
    ],
  },
  {
    title: '文档',
    links: [
      { label: 'CodeAct 执行模型', href: `${REPO}/blob/main/docs/codeact.zh-CN.md`, external: true },
      { label: '沙箱与执行', href: `${REPO}/blob/main/docs/execution.zh-CN.md`, external: true },
      { label: 'Computer Use', href: `${REPO}/blob/main/docs/computer-use.zh-CN.md`, external: true },
      { label: 'Browser Use', href: `${REPO}/blob/main/docs/browser-use.zh-CN.md`, external: true },
      { label: 'NuGet 全局工具', href: `${REPO}/blob/main/docs/nuget-tool.zh-CN.md`, external: true },
      { label: '插件与扩展', href: `${REPO}/blob/main/docs/plugins.zh-CN.md`, external: true },
    ],
  },
  {
    title: '资源',
    links: [
      { label: 'GitHub 仓库', href: REPO, external: true },
      { label: 'Issues', href: `${REPO}/issues`, external: true },
      { label: '第三方声明', href: `${REPO}/blob/main/THIRD-PARTY-NOTICES.md`, external: true },
    ],
  },
  {
    title: '协议',
    links: [
      { label: 'GPL-3.0-only', href: `${REPO}/blob/main/LICENSE`, external: true },
      {
        label: 'Pixelium Design（MIT）',
        href: 'https://shika-works.github.io/pixelium-design/zh/guide/intro',
        external: true,
      },
      { label: 'Fusion Pixel 字体（OFL-1.1）', href: 'https://openfontlicense.org', external: true },
    ],
  },
]
</script>

<template>
  <footer class="footer">
    <div class="shell footer-inner">
      <div v-reveal class="footer-brand">
        <div class="footer-logo pixel-cut">
          <PixelIcon name="terminal" :size="28" />
        </div>
        <p class="footer-wordmark">AllAgent.NET</p>
        <p class="footer-desc muted">
          面向 Windows 本机工作流的终端 AI Agent：模型只用 <code>execute_code</code> 组织步骤，
          Python 在隔离沙箱里运行，文件、命令与桌面操作由 C# 宿主工具受控执行。
        </p>
        <p class="footer-license tiny">GPL-3.0-only · 源码与文档公开在 GitHub</p>
      </div>

      <nav class="footer-cols" aria-label="页脚导航">
        <div v-for="column in COLUMNS" :key="column.title" class="footer-col">
          <h3 class="footer-col-title tiny">{{ column.title }}</h3>
          <ul class="footer-list">
            <li v-for="link in column.links" :key="link.href">
              <a
                class="footer-link"
                :href="link.href"
                :target="link.external ? '_blank' : undefined"
                :rel="link.external ? 'noreferrer' : undefined"
              >
                {{ link.label }}
              </a>
            </li>
          </ul>
        </div>
      </nav>
    </div>

    <div class="shell footer-bottom">
      <div class="rule"></div>
      <p class="tiny footer-copy">© 2026 AllAgent.NET · 用 Vue 3 + Pixelium Design 构建</p>
    </div>
  </footer>
</template>

<style scoped>
.footer {
  position: relative;
  border-top: 2px solid var(--c-line);
  background-color: var(--c-void);
}

/* 宽屏：品牌 + 四列链接；<1024px 收成单列 */
.footer-inner {
  display: grid;
  grid-template-columns: minmax(0, 1.1fr) minmax(0, 2fr);
  gap: var(--grid-3);
  padding-block: var(--grid-3) var(--grid-2);
}

.footer-brand {
  display: flex;
  flex-direction: column;
  gap: var(--grid);
  min-width: 0;
}

.footer-logo {
  display: grid;
  place-items: center;
  width: calc(var(--grid-2) * 2);
  height: calc(var(--grid-2) * 2);
  border: 2px solid var(--c-line-strong);
  background: var(--c-surface-2);
  color: var(--c-mint);
  box-shadow: var(--shadow-hard-sm);
}

.footer-wordmark {
  font-family: var(--font-pixel);
  font-size: var(--fs-h3);
  letter-spacing: 0.08em;
  color: var(--text);
}

.footer-desc {
  max-width: 42ch;
  font-size: var(--fs-small);
}

.footer-desc code {
  padding: 0 var(--bit);
  background: var(--c-surface-2);
  color: var(--c-mint);
  font-size: var(--fs-small);
}

.footer-license {
  color: var(--text-mute);
}

.footer-cols {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  gap: var(--grid-3) var(--grid-2);
  min-width: 0;
}

.footer-col-title {
  margin-bottom: var(--grid);
}

.footer-list {
  display: flex;
  flex-direction: column;
  gap: var(--grid);
  margin: 0;
  padding: 0;
  list-style: none;
}

.footer-link {
  display: inline-block;
  border-bottom: 0;
  font-size: var(--fs-small);
  color: var(--text-dim);
  transition: color var(--t-fast) var(--ease-step);
}

.footer-link:hover {
  color: var(--accent);
}

.footer-bottom {
  padding-block: var(--grid-2) var(--grid-3);
}

.footer-copy {
  margin-top: var(--grid-2);
  color: var(--text-mute);
}

@media (max-width: 1023px) {
  .footer-inner {
    grid-template-columns: minmax(0, 1fr);
  }

  .footer-cols {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }
}

@media (max-width: 560px) {
  .footer-cols {
    grid-template-columns: minmax(0, 1fr);
  }
}
</style>

