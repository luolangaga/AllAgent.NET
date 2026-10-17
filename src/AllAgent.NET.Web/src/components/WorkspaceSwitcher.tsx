import { useEffect, useState } from 'react'
import { FolderOpen, HardDrive, Plus } from 'lucide-react'
import { Button } from './ui/b-button'
import { Input } from './ui/input'
import { Modal } from './shared'
import { bridgeEvents } from '@/bridge'
import { useStore } from '@/lib/store'
import { useWorkspaceStore } from '@/stores/workspace'
import { useNoticesStore } from '@/stores/ui'

/**
 * Deliberately minimal: create a workspace by name in 文档\AllAgent.NET, or point
 * at an existing folder through the host's native dialog.
 */
export function WorkspaceSwitcher() {
  const workspace = useStore(useWorkspaceStore)
  const notices = useStore(useNoticesStore)
  const [name, setName] = useState('')
  const [picking, setPicking] = useState(false)

  // The host dialog is asynchronous: the folder arrives as a bridge event.
  useEffect(() => {
    const onPicked = (data: { path: string | null; error?: string | null }) => {
      setPicking(false)
      if (data.error) {
        notices.notify('无法打开文件夹对话框', data.error, 'destructive')
        return
      }
      const path = data.path
      if (!path) return
      void workspace.switchTo(path)
        .then(() => {
          notices.notify('工作区已切换', path, 'success')
          workspace.closeSwitcher()
        })
        .catch(notices.report)
    }
    bridgeEvents.on('workspace.folderPicked', onPicked)
    return () => { bridgeEvents.off('workspace.folderPicked', onPicked) }
  }, [])

  if (!workspace.switcherOpen) return null
  const onClose = () => { setName(''); workspace.closeSwitcher() }
  const root = workspace.workspacesRoot || '文档\\AllAgent.NET'

  async function createWorkspace() {
    const value = name.trim()
    if (!value) return
    try {
      await workspace.create(value)
      notices.notify('工作区已创建', value, 'success')
      onClose()
    } catch (error) {
      notices.report(error)
    }
  }

  async function pickFolder() {
    setPicking(true)
    try {
      await workspace.openFolderDialog()
    } catch (error) {
      setPicking(false)
      notices.report(error)
    }
  }

  return <Modal
    description="每个对话都属于一个工作区；数据保存在该工作区目录的 .allagent/data 下。"
    onClose={onClose}
    title="工作区"
  >
    <section className="workspace-part">
      <div className="workspace-part-head">
        <Plus size={15} />
        <div>
          <strong>新建工作区</strong>
          <p>在 <b className="mono">{root}</b> 下用这个名称创建目录，并立即切换过去。</p>
        </div>
      </div>
      <div className="workspace-part-row">
        <Input
          aria-label="工作区名称"
          onValueChange={setName}
          placeholder="例如 my-app"
          value={name}
        />
        <Button
          disabled={!name.trim()}
          loading={workspace.switching}
          onClick={() => void createWorkspace()}
        >创建</Button>
      </div>
    </section>

    <section className="workspace-part">
      <div className="workspace-part-head">
        <FolderOpen size={15} />
        <div>
          <strong>选择已有目录</strong>
          <p>由本机弹出文件夹对话框，选中的目录直接作为工作区。</p>
        </div>
      </div>
      <Button
        icon={<FolderOpen size={15} />}
        loading={picking}
        onClick={() => void pickFolder()}
        variant="outline"
      >{picking ? '请在弹出的窗口中选择…' : '选择文件夹…'}</Button>
    </section>

    <div className="workspace-current">
      <div className="workspace-line"><HardDrive size={14} /><span>当前工作区<b className="mono">{workspace.workspace || '未知'}</b></span></div>
    </div>
  </Modal>
}