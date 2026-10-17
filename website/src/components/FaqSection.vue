<script setup lang="ts">
import { Collapse as PxCollapse, CollapseItem as PxCollapseItem } from '@pixelium/web-vue/es'

/** 默认展开第一条：权限档位是最常被问到的问题 */
const DEFAULT_ACTIVE: (string | number | symbol)[] = ['permissions']

/** 折叠节奏贴合 tokens 里的 --t-base，展开过程保持低帧率感 */
const ANIMATION_DURATION = 240
</script>

<template>
  <section id="faq" class="section faq">
    <div class="shell faq-grid">
      <header v-reveal class="faq-intro">
        <p class="eyebrow">FAQ</p>
        <h2 class="faq-title">常见问题</h2>
        <p class="faq-lede muted">
          每条回答都对应仓库里的具体实现：权限档位、沙箱后端、凭据边界、数据落盘位置与平台限制。
          如果实际行为与这里不一致，那是 Bug，请到 Issues 反馈。
        </p>
        <div class="panel faq-note">
          <p class="tiny">先自检一遍</p>
          <p class="faq-note-text">
            <code>allagent doctor</code> 会检查 Python、PowerShell、ripgrep、共享库环境、pip 与
            <code>pip.md</code>，并真的跑一次沙箱执行；检查本身不会安装任何软件。
          </p>
          <a class="faq-note-link" href="#quickstart">去看三步快速开始</a>
        </div>
      </header>

      <div v-reveal="{ delay: 90 }" class="faq-list">
        <PxCollapse
          accordion
          variant="line"
          :default-active="DEFAULT_ACTIVE"
          :animation-duration="ANIMATION_DURATION"
        >
          <PxCollapseItem index="permissions">
            <template #title>
              <span class="faq-no">01</span>
              <span class="faq-q">默认权限是什么？</span>
            </template>
            <template #suffix><span class="faq-tag">权限</span></template>
            <div class="faq-a">
              <p>三档权限互斥，切换只影响两件事：宿主工具要不要逐次审批、能不能访问工作区之外的路径。</p>
              <dl class="faq-terms">
                <div class="faq-term">
                  <dt><code>full</code><span class="faq-badge">默认</span></dt>
                  <dd>35 个宿主工具不再逐次弹审批，工作区外的路径也放行。它不是提权：AllAgent.NET 不会申请管理员权限。</dd>
                </div>
                <div class="faq-term">
                  <dt><code>ask</code></dt>
                  <dd>每次调用单独确认一次，弹窗默认选中「允许这一次」，不容易误点成永久放行。</dd>
                </div>
                <div class="faq-term">
                  <dt><code>plan</code></dt>
                  <dd>只读：读文件、列目录、搜索可以；写盘、改文件、执行命令都不行。</dd>
                </div>
              </dl>
              <p class="faq-foot">
                即使在 <code>full</code> 下，<code>run_shell</code> 也没有「拼接命令字符串」的入口：参数按
                <code>exe + args</code> 字面量序列化后 Base64 交给固定脚本 <code>run_shell.ps1</code>，由
                <code>pwsh -NoLogo -NoProfile -NonInteractive -CommandWithArgs</code> 启动，
                PowerShell 不会二次解析参数，命令字符串与模板被整个禁用。
              </p>
            </div>
          </PxCollapseItem>

          <PxCollapseItem index="sandbox-backends">
            <template #title>
              <span class="faq-no">02</span>
              <span class="faq-q">沙箱为什么有两个后端？</span>
            </template>
            <template #suffix><span class="faq-tag">沙箱</span></template>
            <div class="faq-a">
              <p>因为「最强的隔离」与「一定能启动」不是同一件事，两个后端各负责一头。</p>
              <dl class="faq-terms">
                <div class="faq-term">
                  <dt><code>Hyperlight</code><span class="faq-badge">优先</span></dt>
                  <dd>
                    微虚拟机路径：宿主具备可用虚拟化时用它，拿到硬件级隔离；启动失败会自动兜底，
                    不需要手动改配置。
                  </dd>
                </div>
                <div class="faq-term">
                  <dt><code>Monty</code><span class="faq-badge">兜底</span></dt>
                  <dd>
                    Rust 实现的 Python 子集：不需要虚拟化、Docker 或管理员权限，内存上限 128 MiB；
                    代价是只支持 Python 的一部分语法与标准库。
                  </dd>
                </div>
              </dl>
              <ul class="faq-points">
                <li>每次执行都从干净快照开始，上一次写进沙箱的东西不会带到下一次。</li>
                <li>
                  执行中途失败不会自动换后端重放代码——避免同一段代码在另一条路径上产生第二遍副作用，
                  要不要重跑由你决定。
                </li>
              </ul>
            </div>
          </PxCollapseItem>

          <PxCollapseItem index="docker-admin">
            <template #title>
              <span class="faq-no">03</span>
              <span class="faq-q">需要 Docker 或管理员权限吗？</span>
            </template>
            <template #suffix><span class="faq-tag">环境</span></template>
            <div class="faq-a">
              <p>都不需要。Monty 后端不需要虚拟化、不需要 Docker，也不需要管理员权限，普通用户会话里就能跑。</p>
              <ul class="faq-points">
                <li>
                  只有 Hyperlight 路径需要宿主开启硬件虚拟化（Windows 上是 Hyper-V / Windows Hypervisor
                  Platform）；不满足时自动落到 Monty，不用改参数。
                </li>
                <li>
                  权限档位 <code>full</code> 只表示「宿主工具不逐次审批、允许工作区外路径」，
                  它不会给 AllAgent.NET 或它启动的进程管理员权限。
                </li>
                <li>
                  唯一需要额外装东西的场景是缺失环境：<code>allagent doctor</code> 会如实报出缺什么，
                  <code>doctor install</code> 才经 winget 补装（安装 Python 3.13 / PowerShell 后需要重启）。
                </li>
              </ul>
            </div>
          </PxCollapseItem>

          <PxCollapseItem index="credentials">
            <template #title>
              <span class="faq-no">04</span>
              <span class="faq-q">模型服务的 Key 会被送进沙箱吗？</span>
            </template>
            <template #suffix><span class="faq-tag">凭据</span></template>
            <div class="faq-a">
              <p>
                不会。沙箱 worker 只保留 Windows 系统环境变量，AllAgent.NET 自己拿到的模型服务凭据不会跟着进 worker。
              </p>
              <ul class="faq-points">
                <li>
                  Key 只在宿主侧用于向 OpenAI Chat / Responses、Anthropic、Gemini 或任何兼容 OpenAI 协议的服务
                  （含 Ollama）发起请求。
                </li>
                <li>
                  沙箱里的 Python 能看到的只有 <code>execute_code</code> 的入参，以及宿主工具回传的结果；
                  想读文件、跑命令都得走宿主工具。
                </li>
                <li>
                  不想把 Key 写进项目，就用环境变量启动：<code>ALLAGENT_PROVIDER</code>、
                  <code>ALLAGENT_BASE_URL</code>、<code>ALLAGENT_MODEL</code>、<code>ALLAGENT_API_KEY</code>。
                </li>
              </ul>
            </div>
          </PxCollapseItem>

          <PxCollapseItem index="data-location">
            <template #title>
              <span class="faq-no">05</span>
              <span class="faq-q">数据存在哪里？</span>
            </template>
            <template #suffix><span class="faq-tag">数据</span></template>
            <div class="faq-a">
              <p>
                Terminal 的项目数据默认落在 <code>&lt;workspace&gt;/.allagent/data/</code>，用
                <code>--data-dir</code> 可以换到别处。
              </p>
              <dl class="faq-terms">
                <div class="faq-term">
                  <dt><code>allagent.json</code></dt>
                  <dd>v3 清单与设置，记录当前引用了哪些会话修订。</dd>
                </div>
                <div class="faq-term">
                  <dt><code>conversations/</code></dt>
                  <dd>
                    每次会话的不可变修订按版本保存；主清单与备份清单各自引用需要的版本，
                    回退不会覆盖旧修订。
                  </dd>
                </div>
                <div class="faq-term">
                  <dt>写入保护</dt>
                  <dd>
                    原子写 + <code>fsync</code> + 校验和回读；跨进程事务与单写入者租约；
                    旧版 v2 数据迁移时会保留备份。
                  </dd>
                </div>
              </dl>
              <p class="faq-foot">
                共享 Python 库、插件、OAuth 凭据与用户 Skills 仍然放在 <code>%APPDATA%/AllAgent.NET</code>，
                不跟着工作区走。
              </p>
            </div>
          </PxCollapseItem>

          <PxCollapseItem index="platform">
            <template #title>
              <span class="faq-no">06</span>
              <span class="faq-q">只能在 Windows 上用吗？</span>
            </template>
            <template #suffix><span class="faq-tag">平台</span></template>
            <div class="faq-a">
              <p>Agent 执行目前只支持 Windows x64，其他平台没有实现也没有验证。</p>
              <ul class="faq-points">
                <li>
                  命令执行走 PowerShell 7.6（<code>pwsh -CommandWithArgs</code> + 固定脚本
                  <code>run_shell.ps1</code>），不回退 5.1。
                </li>
                <li>
                  桌面操作由 FlaUI / UIA3 的 12 个 <code>computer_*</code> 工具完成，浏览器走 WebView2 / 外置
                  Chrome、Edge，都是 Windows 侧的能力。
                </li>
                <li>
                  Windows 路径闸门按 Windows 规则拦截：UNC 工作区、<code>CON</code>/<code>PRN</code>/<code>AUX</code>/<code>NUL</code>/<code>COM1</code>
                  这类歧义名、以点或空格结尾的名字、<code>README.md:secret</code> 这样的备用数据流，
                  以及普通模式下的 reparse point。
                </li>
                <li>
                  Node 与 npm 只在你自己构建附带的前端资源（<code>src/AllAgent.NET.Web</code>）时才需要；
                  用 Terminal 本身要的是 .NET 8 SDK、Python 3.10–3.14、PowerShell 7.6 与 ripgrep。
                </li>
              </ul>
            </div>
          </PxCollapseItem>
        </PxCollapse>
      </div>
    </div>
  </section>
</template>

<style scoped>
.faq {
  position: relative;
}

/* 宽屏：左说明 / 右列表；<1024px 自动收成单列 */
.faq-grid {
  display: grid;
  grid-template-columns: minmax(0, 0.82fr) minmax(0, 1.18fr);
  gap: var(--grid-3);
  align-items: start;
}

.faq-intro {
  display: flex;
  flex-direction: column;
  gap: var(--grid-2);
  min-width: 0;
}

.faq-title {
  font-size: var(--fs-h2);
}

.faq-lede {
  font-size: var(--fs-body);
}

.faq-note {
  display: flex;
  flex-direction: column;
  gap: var(--grid);
  padding: var(--grid-2);
}

.faq-note-text {
  font-size: var(--fs-small);
  color: var(--text-dim);
}

.faq-note-link {
  align-self: flex-start;
  font-family: var(--font-pixel);
  font-size: var(--fs-small);
  color: var(--accent);
}

.faq-list {
  min-width: 0;
}

/* ---------- 折叠面板的像素化微调（覆盖库内样式） ---------- */
.faq-list :deep(.px-collapse) {
  color: var(--text);
}

.faq-list :deep(.px-collapse-item-header) {
  min-height: var(--grid-3);
  padding-block: var(--grid-2);
  font-family: var(--font-pixel);
  font-size: var(--fs-h3);
  line-height: 1.45;
  color: var(--text);
  transition: color var(--t-fast) var(--ease-step);
}

.faq-list :deep(.px-collapse-item-header:hover) {
  color: var(--accent);
}

.faq-list :deep(.px-collapse-item__line .px-collapse-item-header) {
  border-bottom: 2px solid var(--line);
}

.faq-list :deep(.px-collapse-item__line:last-child .px-collapse-item-header) {
  border-bottom: 0;
}

.faq-list :deep(.px-collapse-item-arrow) {
  color: var(--accent);
  font-size: var(--fs-small);
}

.faq-list :deep(.px-collapse-item-content-box) {
  padding-block: var(--grid-2) var(--grid-3);
}

/* ---------- 标题行 ---------- */
.faq-no {
  display: inline-block;
  margin-right: var(--grid);
  font-family: var(--font-mono);
  font-size: var(--fs-micro);
  color: var(--text-mute);
  vertical-align: 2px; /* 与像素字标题基线的 2px 补偿 */
}

.faq-q {
  display: inline;
}

.faq-tag {
  padding: 0 var(--bit);
  border: 2px solid var(--line);
  background: var(--c-bg-soft);
  font-family: var(--font-pixel);
  font-size: var(--fs-micro);
  letter-spacing: 0.16em;
  color: var(--c-amber);
}

/* ---------- 回答正文 ---------- */
.faq-a {
  display: flex;
  flex-direction: column;
  gap: var(--grid-2);
  font-size: var(--fs-small);
  line-height: 1.9;
  color: var(--text-dim);
  overflow-wrap: anywhere;
}

.faq-a code {
  padding: 0 var(--bit);
  background: var(--c-surface-2);
  color: var(--c-mint);
  font-size: var(--fs-small);
}

.faq-terms {
  display: flex;
  flex-direction: column;
  gap: var(--grid-2);
  margin: 0;
}

.faq-term {
  display: grid;
  grid-template-columns: minmax(0, 10ch) minmax(0, 1fr);
  gap: var(--grid);
  padding-left: var(--grid-2);
  border-left: 2px solid var(--line-strong);
}

.faq-term dt {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--bit);
  font-family: var(--font-mono);
  font-size: var(--fs-small);
  line-height: 1.6;
  color: var(--c-amber);
}

.faq-term dd {
  margin: 0;
}

.faq-badge {
  padding: 0 var(--bit);
  background: var(--accent);
  font-family: var(--font-pixel);
  font-size: var(--fs-micro);
  line-height: 1.5;
  letter-spacing: 0.16em;
  color: var(--c-void);
}

.faq-points {
  display: flex;
  flex-direction: column;
  gap: var(--grid);
  margin: 0;
  padding: 0;
  list-style: none;
}

.faq-points li {
  position: relative;
  padding-left: var(--grid-2);
}

/* 方块项目符号，替代圆点 */
.faq-points li::before {
  content: '';
  position: absolute;
  top: var(--bit);
  left: 0;
  width: var(--bit);
  height: var(--bit);
  background: var(--accent);
}

.faq-foot {
  padding-top: var(--grid);
  border-top: 2px dashed rgba(34, 49, 60, 0.7);
  color: var(--text-mute);
  font-size: var(--fs-small);
}

@media (max-width: 1023px) {
  .faq-grid {
    grid-template-columns: minmax(0, 1fr);
  }
}

@media (max-width: 767px) {
  .faq-tag {
    display: none;
  }

  .faq-term {
    grid-template-columns: minmax(0, 1fr);
    gap: var(--bit);
    padding-left: var(--grid);
  }
}
</style>

