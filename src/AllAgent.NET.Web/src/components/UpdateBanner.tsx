import { useEffect } from 'react'
import { ArrowUpCircle, ExternalLink, X } from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/b-button'
import { useStore } from '@/lib/store'
import { useT } from '@/i18n'
import { useUpdateStore } from '@/stores/update'
import { useNoticesStore } from '@/stores/ui'

export function UpdateBanner() {
  const tr = useT()
  const update = useStore(useUpdateStore)
  const notices = useStore(useNoticesStore)

  useEffect(() => { void update.load(false) }, [])

  const status = update.status
  if (!status?.updateAvailable || !status.latestVersion || update.dismissedVersion === status.latestVersion) return null

  async function start() {
    try {
      const result = await update.install()
      notices.notify(
        result.started ? tr('更新已开始', 'Update started') : tr('未安装更新', 'Update not installed'),
        result.message,
        result.started ? 'success' : 'warning',
      )
    } catch (error) {
      notices.report(error)
    }
  }

  return <div className="update-banner">
    <ArrowUpCircle size={16} />
    <div className="update-banner-copy">
      <strong>{tr(`发现新版本 v${status.latestVersion}`, `Version ${status.latestVersion} is available`)}</strong>
      <span>
        {tr(`当前 v${status.currentVersion} · 来源 ${status.source} · ${status.installKind}`, `Current v${status.currentVersion} · source ${status.source} · ${status.installKind}`)}
      </span>
    </div>
    <Badge color="blue" size="sm" variant="dot">{status.architecture}</Badge>
    {status.releaseUrl ? <Button
      icon={<ExternalLink size={13} />}
      onClick={() => window.open(status.releaseUrl!, '_blank', 'noopener')}
      size="xs"
      variant="ghost"
    >{tr('发布说明', 'Release notes')}</Button> : null}
    <Button icon={<ArrowUpCircle size={13} />} loading={update.installing} onClick={() => void start()} size="xs">{tr('立即更新', 'Update now')}</Button>
    <Button aria-label={tr('稍后提醒', 'Remind me later')} iconLabel={tr('稍后提醒', 'Remind me later')} onClick={update.dismiss} size="icon-xs" variant="ghost"><X size={13} /></Button>
  </div>
}
