import { useState } from 'react'
import { Blocks, Braces, Eye, Plus, Trash2, Upload } from 'lucide-react'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/b-button'
import { Card } from '@/components/ui/card'
import { CodeBlock } from '@/components/ui/code-block'
import { Input } from '@/components/ui/input'
import { Switch } from '@/components/ui/b-switch'
import { Confirm, Field, Modal, Picker, TextArea } from '@/components/shared'
import { EmptyState, PageShell } from '@/components/PageShell'
import { useStore } from '@/lib/store'
import { useSkillsStore } from '@/stores/skills'
import { useNoticesStore } from '@/stores/ui'
import type { SkillInfo } from '@/types'

const scopeLabels: Record<string, string> = {
  builtin: '内置',
  user: '用户',
  project: '项目',
  global: '全局',
}

const scopeOptions = [
  { value: 'user', label: '用户技能 · 跟随数据目录' },
  { value: 'project', label: '项目技能 · 写入 <工作区>/.allagent/skills' },
]

export default function Skills() {
  const store = useStore(useSkillsStore)
  const notices = useStore(useNoticesStore)
  const [query, setQuery] = useState('')
  const [remove, setRemove] = useState<SkillInfo | null>(null)
  const [preview, setPreview] = useState<{ name: string; body: string } | null>(null)
  const [creating, setCreating] = useState(false)
  const [importing, setImporting] = useState(false)
  const [busy, setBusy] = useState(false)
  const [draft, setDraft] = useState({ name: '', description: '', instructions: '', scope: 'user' })
  const [importText, setImportText] = useState('')
  const [importScope, setImportScope] = useState('user')

  const filtered = store.skills.filter(skill => `${skill.name} ${skill.description}`.toLowerCase().includes(query.toLowerCase()))

  async function run(action: () => Promise<unknown>, success?: string) {
    setBusy(true)
    try {
      await action()
      if (success) notices.notify(success, undefined, 'success')
      return true
    } catch (error) {
      notices.report(error)
      return false
    } finally {
      setBusy(false)
    }
  }

  async function openPreview(skill: SkillInfo) {
    try {
      const body = await store.body(skill.name)
      setPreview({ name: skill.name, body })
    } catch (error) {
      notices.report(error)
    }
  }

  return <PageShell
    action={<>
      <Button icon={<Upload size={15} />} onClick={() => setImporting(true)} variant="outline">导入 SKILL.md</Button>
      <Button icon={<Plus size={16} />} onClick={() => { setDraft({ name: '', description: '', instructions: '', scope: 'user' }); setCreating(true) }}>新建技能</Button>
    </>}
    description="SKILL.md 指令技能：内置技能随宿主发布，用户技能跟随数据目录，项目技能写入 <工作区>/.allagent/skills。启用的技能会在任务中按需加载。"
    eyebrow="AGENT CAPABILITIES"
    title="为想法，配上行动力。"
  >
    {store.diagnostics.length > 0 ? <Alert
      appearance="warning"
      dismissible={false}
      message={store.diagnostics.join('\n')}
      timeout={0}
      title={`技能目录诊断（${store.diagnostics.length} 条）`}
    /> : null}

    <div className="section-meta">
      <Input
        aria-label="搜索技能"
        onValueChange={setQuery}
        placeholder="搜索技能名称或描述…"
        size="sm"
        startAdornment={<Blocks size={14} />}
        value={query}
      />
      <span>{store.skills.filter(skill => skill.enabled).length} 个已启用 · 共 {store.skills.length} 个</span>
    </div>

    {store.available && store.skills.length > 0 ? <Card className="list-card">
      <div className="divide-list">{filtered.map(skill => <div className="list-row" key={`${skill.scope}:${skill.name}`}>
        <div className="resource-icon"><Blocks size={20} /></div>
        <div className="list-content">
          <div className="resource-title">
            <h3>{skill.name}</h3>
            <Badge size="sm" variant="dot">{scopeLabels[skill.scope] ?? skill.scope}</Badge>
            {!skill.userInvocable ? <Badge color="gray" size="sm" variant="dot">仅模型调用</Badge> : null}
            {!skill.modelInvocable ? <Badge color="gray" size="sm" variant="dot">仅手动调用</Badge> : null}
          </div>
          <p className="list-description">{skill.description || '暂无描述'}</p>
        </div>
        <div className="list-actions">
          <Button icon={<Eye size={14} />} onClick={() => void openPreview(skill)} size="sm" variant="ghost">查看正文</Button>
          <Switch
            aria-label={`启用 ${skill.name}`}
            checked={skill.enabled}
            disabled={busy}
            onCheckedChange={enabled => void run(() => store.setEnabled(skill.name, enabled), enabled ? '技能已启用' : '技能已禁用')}
            size="sm"
          />
          {skill.scope !== 'builtin' ? <Button
            iconLabel={`删除 ${skill.name}`}
            onClick={() => setRemove(skill)}
            size="icon-sm"
            variant="ghost"
          ><Trash2 size={15} /></Button> : null}
        </div>
      </div>)}</div>
    </Card> : null}

    {store.available && store.skills.length === 0 ? <EmptyState title="还没有可用技能">
      宿主会随版本发布内置技能；也可以新建或导入 SKILL.md。
    </EmptyState> : null}
    {store.available && store.skills.length > 0 && filtered.length === 0
      ? <EmptyState title="没有匹配的技能">换个关键词试试。</EmptyState> : null}

    {!store.available && store.legacy.length > 0 ? <Card className="list-card">
      <div className="list-row">
        <div className="resource-icon"><Braces size={20} /></div>
        <div className="list-content">
          <div className="resource-title"><h3>旧版技能记录</h3><Badge color="gray" size="sm" variant="dot">桌面宿主</Badge></div>
          <p className="list-description">这是早期版本随宿主写入的工具型技能记录；终端宿主（/web-ui）使用的是 SKILL.md 技能目录。</p>
        </div>
      </div>
      <div className="divide-list">{store.legacy.map(skill => <div className="list-row" key={skill.id}>
        <div className="list-content">
          <div className="resource-title"><h3 className="mono">{skill.name}</h3><Badge size="sm" variant="dot">{skill.type}</Badge></div>
          <p className="list-description">{skill.description}</p>
        </div>
        <div className="list-actions">
          <Switch
            aria-label={`启用 ${skill.name}`}
            checked={skill.isEnabled}
            disabled={busy}
            onCheckedChange={enabled => void run(() => store.legacyToggle(skill.id, enabled))}
            size="sm"
          />
          <Button iconLabel={`删除 ${skill.name}`} onClick={() => void run(() => store.legacyRemove(skill.id))} size="icon-sm" variant="ghost"><Trash2 size={15} /></Button>
        </div>
      </div>)}</div>
    </Card> : null}

    {!store.available && store.legacy.length === 0 ? <EmptyState title="此宿主暂不支持技能目录">
      技能管理由终端宿主提供：在终端运行 <span className="mono">/web-ui</span> 后在本页面管理 SKILL.md 技能。
    </EmptyState> : null}

    {creating ? <Modal
      description="技能名用小写字母、数字或短横线；指令就是技能的正文。"
      footer={<Button
        disabled={busy || !draft.name.trim() || !draft.description.trim() || !draft.instructions.trim()}
        loading={busy}
        onClick={() => void run(() => store.create(draft.name.trim(), draft.description.trim(), draft.instructions.trim(), draft.scope), '技能已创建').then(ok => { if (ok) setCreating(false) })}
      >创建技能</Button>}
      onClose={() => setCreating(false)}
      size="lg"
      title="新建技能"
    >
      <Field label="技能名（小写字母 / 数字 / 短横线）" onChange={name => setDraft(current => ({ ...current, name }))} placeholder="例如 release-notes" required value={draft.name} />
      <Field label="描述" onChange={description => setDraft(current => ({ ...current, description }))} placeholder="一句话说明这个技能做什么" required value={draft.description} />
      <label className="field"><span>保存位置</span>
        <Picker label="保存位置" onChange={scope => setDraft(current => ({ ...current, scope }))} options={scopeOptions} value={draft.scope} />
      </label>
      <TextArea
        description="技能的完整指令，与任务一起交给模型；支持 $ARGUMENTS 占位符。"
        label="指令正文"
        onChange={instructions => setDraft(current => ({ ...current, instructions }))}
        rows={10}
        value={draft.instructions}
      />
    </Modal> : null}

    {importing ? <Modal
      description="粘贴完整的 SKILL.md（YAML frontmatter + 指令正文）。"
      footer={<Button
        disabled={busy || !importText.trim()}
        loading={busy}
        onClick={() => void run(() => store.importText(importText.trim(), importScope), '技能已导入').then(ok => { if (ok) { setImporting(false); setImportText('') } })}
      >导入技能</Button>}
      onClose={() => setImporting(false)}
      size="lg"
      title="导入 SKILL.md"
    >
      <TextArea label="SKILL.md 内容" onChange={setImportText} rows={12} value={importText} />
      <label className="field"><span>保存位置</span>
        <Picker label="保存位置" onChange={setImportScope} options={scopeOptions} value={importScope} />
      </label>
    </Modal> : null}

    {preview ? <Modal
      description={`${preview.name} · 技能正文`}
      onClose={() => setPreview(null)}
      size="lg"
      title={preview.name}
    >
      <CodeBlock code={preview.body} language="markdown" maxHeight={420} />
    </Modal> : null}

    {remove ? <Confirm
      description="技能目录与其中的支持文件会被删除（内置技能不可删除，只能禁用）。"
      onClose={() => setRemove(null)}
      onConfirm={() => run(() => store.remove(remove.name), '技能已删除')}
      title={`删除技能「${remove.name}」？`}
    /> : null}
  </PageShell>
}
