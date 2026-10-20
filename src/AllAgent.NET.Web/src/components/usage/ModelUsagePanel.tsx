import { useEffect, useId, useMemo, useRef, useState } from 'react'
import { Activity, BarChart3, CalendarDays, RefreshCw, TrendingUp } from 'lucide-react'
import { Alert } from '@/components/ui/alert'
import { Button } from '@/components/ui/b-button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Spinner } from '@/components/ui/spinner'
import { invoke } from '@/bridge'
import { formatTokens, providerLabel } from '@/lib/format'
import { languageTag, useT } from '@/i18n'
import type { UsageDay, UsageModel, UsageStats } from '@/types'

const CELL = 9
const CELL_GAP = 2
const DAY_COUNT = 365

function dayKey(date: Date): string {
  const month = String(date.getMonth() + 1).padStart(2, '0')
  const day = String(date.getDate()).padStart(2, '0')
  return `${date.getFullYear()}-${month}-${day}`
}

function addDays(date: Date, count: number): Date {
  const next = new Date(date)
  next.setDate(next.getDate() + count)
  return next
}

function totalOf(day: UsageDay | undefined): number {
  return day ? day.inputTokens + day.outputTokens : 0
}

function shortDate(date: Date): string {
  return languageTag() === 'zh-CN'
    ? `${date.getMonth() + 1}月${date.getDate()}日`
    : date.toLocaleDateString('en-US', { month: 'short', day: 'numeric' })
}

function niceMax(value: number): number {
  if (!Number.isFinite(value) || value <= 0) return 1
  const magnitude = 10 ** Math.floor(Math.log10(value))
  const normalized = value / magnitude
  const step = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10
  return step * magnitude
}

function smoothPath(points: { x: number; y: number }[]): string {
  if (!points.length) return ''
  if (points.length === 1) return `M ${points[0].x} ${points[0].y}`
  let path = `M ${points[0].x} ${points[0].y}`
  for (let index = 0; index < points.length - 1; index += 1) {
    const previous = points[index - 1] ?? points[index]
    const current = points[index]
    const next = points[index + 1]
    const after = points[index + 2] ?? next
    const firstX = current.x + (next.x - previous.x) / 6
    const firstY = current.y + (next.y - previous.y) / 6
    const secondX = next.x - (after.x - current.x) / 6
    const secondY = next.y - (after.y - current.y) / 6
    path += ` C ${firstX} ${firstY}, ${secondX} ${secondY}, ${next.x} ${next.y}`
  }
  return path
}

export function ModelUsagePanel() {
  const tr = useT()
  const [stats, setStats] = useState<UsageStats | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState('')
  const [revision, setRevision] = useState(0)

  useEffect(() => {
    let cancelled = false
    setLoading(true)
    setError('')
    invoke<UsageStats>('usage.stats')
      .then(value => { if (!cancelled) setStats(value) })
      .catch(cause => {
        if (cancelled) return
        setStats(null)
        setError(cause instanceof Error ? cause.message : String(cause))
      })
      .finally(() => { if (!cancelled) setLoading(false) })
    return () => { cancelled = true }
  }, [revision])

  return <div className="usage-panel">
    <div className="usage-toolbar">
      <div>
        <strong>{tr('累计概览', 'Lifetime overview')}</strong>
        <span>{tr('累计 Token、请求与每日活跃度。', 'Lifetime tokens, requests and daily activity.')}</span>
      </div>
      <Button
        icon={<RefreshCw size={13} />}
        loading={loading}
        onClick={() => setRevision(value => value + 1)}
        size="sm"
        variant="outline"
      >{tr('刷新', 'Refresh')}</Button>
    </div>

    {error ? <Alert
      appearance="destructive"
      dismissible={false}
      message={error}
      timeout={0}
      title={tr('无法读取用量统计', 'Usage statistics unavailable')}
      width="100%"
    /> : null}

    {loading && !stats ? <div className="usage-loading"><Spinner size="sm" />{tr('正在汇总用量…', 'Aggregating usage…')}</div> : null}

    {stats ? <>
      <UsageMetrics stats={stats} />
      <Card className="usage-card">
        <CardHeader>
          <CardTitle>{tr('每日 Token 用量', 'Daily token usage')}</CardTitle>
          <CardDescription>{tr('最近 30 天，输入与输出堆叠展示；悬停查看当天明细。', 'The last 30 days, input and output stacked; hover a point for details.')}</CardDescription>
        </CardHeader>
        <CardContent className="usage-card-body">
          <UsageAreaChart days={stats.days} />
        </CardContent>
      </Card>

      <Card className="usage-card">
        <CardHeader>
          <CardTitle>{tr('各模型用量', 'Usage by model')}</CardTitle>
          <CardDescription>{tr('按累计 Token 排序，条形中浅色为输入、深色为输出。', 'Sorted by lifetime tokens; light segments are input, dark segments are output.')}</CardDescription>
        </CardHeader>
        <CardContent className="usage-card-body">
          <UsageModelBars models={stats.models} />
        </CardContent>
      </Card>

      <Card className="usage-card">
        <CardHeader>
          <CardTitle>{tr('使用活跃图', 'Activity graph')}</CardTitle>
          <CardDescription>{tr('过去一年每天的使用强度。颜色越深，当天消耗的 Token 越多。', 'One year of daily intensity. Darker cells mean more tokens were used that day.')}</CardDescription>
        </CardHeader>
        <CardContent className="usage-card-body">
          <UsageHeatmap days={stats.days} />
        </CardContent>
      </Card>

      <p className="usage-note">
        {tr(
          '每日统计从当前版本开始记录；更早的请求只计入模型累计总量，不会出现在活跃图中。统计仅包含服务商实际返回的用量。',
          'Daily history starts with this version; earlier requests count toward lifetime model totals only and do not appear in the activity graph. Only provider-reported usage is counted.',
        )}
      </p>
    </> : null}
  </div>
}

function UsageMetrics({ stats }: { stats: UsageStats }) {
  const tr = useT()
  const total = stats.totals.inputTokens + stats.totals.outputTokens
  const average = stats.totals.activeDays > 0 ? Math.round(total / stats.totals.activeDays) : 0
  const items = [
    {
      icon: BarChart3,
      label: tr('累计 Token', 'Lifetime tokens'),
      value: formatTokens(total),
      hint: tr(`输入 ${formatTokens(stats.totals.inputTokens)} · 输出 ${formatTokens(stats.totals.outputTokens)}`, `Input ${formatTokens(stats.totals.inputTokens)} · output ${formatTokens(stats.totals.outputTokens)}`),
    },
    {
      icon: Activity,
      label: tr('请求次数', 'Requests'),
      value: formatTokens(stats.totals.requests),
      hint: tr(`${formatTokens(stats.totals.requestsWithUsage)} 次返回了用量`, `${formatTokens(stats.totals.requestsWithUsage)} returned usage`),
    },
    {
      icon: CalendarDays,
      label: tr('活跃天数', 'Active days'),
      value: String(stats.totals.activeDays),
      hint: tr('有请求的本地日期', 'Local days with requests'),
    },
    {
      icon: TrendingUp,
      label: tr('日均 Token', 'Average per day'),
      value: formatTokens(average),
      hint: tr('按活跃天数计算', 'Across active days'),
    },
  ]

  return <div className="usage-metrics">{items.map(item => <div className="usage-metric" key={item.label}>
    <span className="usage-metric-icon"><item.icon size={15} /></span>
    <div>
      <span className="usage-metric-label">{item.label}</span>
      <strong>{item.value}</strong>
      <span className="usage-metric-hint">{item.hint}</span>
    </div>
  </div>)}</div>
}

function UsageAreaChart({ days }: { days: UsageDay[] }) {
  const tr = useT()
  const gradientId = useId().replace(/[^a-zA-Z0-9_-]/g, '')
  const [active, setActive] = useState<number | null>(null)
  const series = useMemo(() => {
    const byDate = new Map(days.map(day => [day.date, day]))
    const today = new Date()
    today.setHours(0, 0, 0, 0)
    return Array.from({ length: 30 }, (_, index) => {
      const date = addDays(today, index - 29)
      const key = dayKey(date)
      const day = byDate.get(key)
      const input = day?.inputTokens ?? 0
      const output = day?.outputTokens ?? 0
      return { date, key, input, output, total: input + output, requests: day?.requests ?? 0 }
    })
  }, [days])

  const width = 760
  const height = 240
  const padding = { top: 18, right: 16, bottom: 30, left: 48 }
  const plotWidth = width - padding.left - padding.right
  const plotHeight = height - padding.top - padding.bottom
  const bottom = padding.top + plotHeight
  const max = niceMax(Math.max(...series.map(point => point.total), 0))
  const pointAt = (index: number) => padding.left + (index / Math.max(1, series.length - 1)) * plotWidth
  const inputPoints = series.map((point, index) => ({
    ...point,
    x: pointAt(index),
    y: bottom - (point.input / max) * plotHeight,
  }))
  const totalPoints = series.map((point, index) => ({
    ...point,
    x: pointAt(index),
    y: bottom - (point.total / max) * plotHeight,
  }))
  const first = totalPoints[0]
  const last = totalPoints[totalPoints.length - 1]
  const inputLine = smoothPath(inputPoints)
  const totalLine = smoothPath(totalPoints)
  const inputArea = `${inputLine} L ${last.x} ${bottom} L ${first.x} ${bottom} Z`
  const totalArea = `${totalLine} L ${last.x} ${bottom} L ${first.x} ${bottom} Z`
  const activePoint = active === null ? null : totalPoints[active]
  const hasData = series.some(point => point.total > 0)
  const ticks = [0, 0.25, 0.5, 0.75, 1].map(ratio => ({ value: max * ratio, y: bottom - plotHeight * ratio }))
  const tickLabel = (value: number) => value > 0 && value < 10 ? value.toFixed(1) : formatTokens(value)

  return <div className="usage-area">
    <div className="usage-legend">
      <span><i className="usage-legend-dot is-input" />{tr('输入', 'Input')}</span>
      <span><i className="usage-legend-dot is-output" />{tr('输出', 'Output')}</span>
    </div>
    <div className="usage-area-canvas">
      <svg aria-label={tr('最近 30 天每日 Token 用量', 'Daily token usage over the last 30 days')} className="usage-area-svg" role="img" viewBox={`0 0 ${width} ${height}`}>
        <defs>
          <linearGradient id={`usage-input-${gradientId}`} x1="0" x2="0" y1="0" y2="1">
            <stop offset="0%" stopColor="var(--chart-input)" stopOpacity="0.28" />
            <stop offset="100%" stopColor="var(--chart-input)" stopOpacity="0.02" />
          </linearGradient>
          <linearGradient id={`usage-output-${gradientId}`} x1="0" x2="0" y1="0" y2="1">
            <stop offset="0%" stopColor="var(--chart-output)" stopOpacity="0.26" />
            <stop offset="100%" stopColor="var(--chart-output)" stopOpacity="0.02" />
          </linearGradient>
        </defs>

        {ticks.map(tick => <g key={tick.value}>
          <line stroke="var(--border)" strokeDasharray="3 5" strokeOpacity="0.75" x1={padding.left} x2={width - padding.right} y1={tick.y} y2={tick.y} />
          <text fill="var(--muted-foreground)" fontSize="10" textAnchor="end" x={padding.left - 9} y={tick.y + 3}>{tickLabel(tick.value)}</text>
        </g>)}

        <path d={totalArea} fill={`url(#usage-output-${gradientId})`} />
        <path d={inputArea} fill={`url(#usage-input-${gradientId})`} />
        <path d={totalLine} fill="none" stroke="var(--chart-output)" strokeLinecap="round" strokeWidth="2" />
        <path d={inputLine} fill="none" stroke="var(--chart-input)" strokeLinecap="round" strokeWidth="2" />

        {[0, 7, 14, 21, 28].map(index => {
          const point = series[index]
          if (!point) return null
          return <text fill="var(--muted-foreground)" fontSize="9.5" key={point.key} textAnchor={index === 0 ? 'start' : index === 28 ? 'end' : 'middle'} x={pointAt(index)} y={height - 9}>{shortDate(point.date)}</text>
        })}

        {activePoint ? <>
          <line stroke="var(--brand)" strokeOpacity="0.5" x1={activePoint.x} x2={activePoint.x} y1={padding.top} y2={bottom} />
          <circle cx={activePoint.x} cy={activePoint.y} fill="var(--surface)" r="4" stroke="var(--chart-output)" strokeWidth="2" />
        </> : null}

        {series.map((point, index) => <rect
          fill="transparent"
          height={plotHeight}
          key={point.key}
          onMouseEnter={() => setActive(index)}
          onMouseLeave={() => setActive(null)}
          width={plotWidth / series.length}
          x={pointAt(index) - plotWidth / (series.length * 2)}
          y={padding.top}
        />)}
      </svg>

      {activePoint ? <div
        className="usage-tooltip"
        style={{ left: `${Math.min(86, Math.max(14, (activePoint.x / width) * 100))}%`, top: `${(activePoint.y / height) * 100}%` }}
      >
        <strong>{shortDate(activePoint.date)}</strong>
        <span>{tr('合计', 'Total')} <b>{formatTokens(activePoint.total)}</b></span>
        <span>{tr('输入', 'Input')} {formatTokens(activePoint.input)} · {tr('输出', 'Output')} {formatTokens(activePoint.output)}</span>
        <span>{tr('请求', 'Requests')} {activePoint.requests}</span>
      </div> : null}

      {!hasData ? <span className="usage-empty-overlay">{tr('还没有每日用量记录', 'No daily usage recorded yet')}</span> : null}
    </div>
  </div>
}

function UsageModelBars({ models }: { models: UsageModel[] }) {
  const tr = useT()
  const sorted = useMemo(() => [...models].sort((left, right) =>
    (right.inputTokens + right.outputTokens) - (left.inputTokens + left.outputTokens)), [models])
  const max = Math.max(...sorted.map(model => model.inputTokens + model.outputTokens), 1)
  const total = sorted.reduce((sum, model) => sum + model.inputTokens + model.outputTokens, 0)
  const [mounted, setMounted] = useState(false)
  useEffect(() => { const frame = requestAnimationFrame(() => setMounted(true)); return () => cancelAnimationFrame(frame) }, [])

  if (!sorted.length) return <div className="usage-empty">
    <BarChart3 size={20} />
    <p>{tr('还没有模型用量记录，发送一条消息后就会出现在这里。', 'No model usage yet. Send a message and it will appear here.')}</p>
  </div>

  return <div className="usage-models">
    <div className="usage-legend">
      <span><i className="usage-legend-dot is-input" />{tr('输入', 'Input')}</span>
      <span><i className="usage-legend-dot is-output" />{tr('输出', 'Output')}</span>
      <span className="usage-legend-total">{tr(`共 ${sorted.length} 个模型`, `${sorted.length} models`)}</span>
    </div>
    {sorted.map(model => {
      const modelTotal = model.inputTokens + model.outputTokens
      const inputShare = modelTotal > 0 ? (model.inputTokens / modelTotal) * 100 : 0
      const width = mounted ? (modelTotal / max) * 100 : 0
      return <div className="usage-model-row" key={`${model.providerId}:${model.modelId}`}>
        <div className="usage-model-name">
          <strong>{model.modelId || tr('未知模型', 'Unknown model')}</strong>
          <span>{providerLabel(model.providerId)} · {tr(`${model.requests} 次请求`, `${model.requests} requests`)}</span>
        </div>
        <div className="usage-model-track">
          <div className="usage-model-bar" style={{ width: `${width}%` }}>
            <span className="is-input" style={{ width: `${inputShare}%` }} />
            <span className="is-output" style={{ width: `${100 - inputShare}%` }} />
          </div>
        </div>
        <div className="usage-model-value">
          <strong>{formatTokens(modelTotal)}</strong>
          <span>{total > 0 ? `${((modelTotal / total) * 100).toFixed(1)}%` : '0%'}</span>
        </div>
      </div>
    })}
  </div>
}

function UsageHeatmap({ days }: { days: UsageDay[] }) {
  const tr = useT()
  const chartRef = useRef<HTMLDivElement>(null)
  const [hover, setHover] = useState<{ key: string; label: string; left: number; top: number } | null>(null)
  const data = useMemo(() => {
    const byDate = new Map(days.map(day => [day.date, day]))
    const today = new Date()
    today.setHours(0, 0, 0, 0)
    const start = addDays(today, -(DAY_COUNT - 1))
    const gridStart = addDays(start, -start.getDay())
    const weeks: { date: Date; key: string; day?: UsageDay; total: number; future: boolean }[][] = []
    for (let weekIndex = 0; weekIndex < 53; weekIndex += 1) {
      const week: { date: Date; key: string; day?: UsageDay; total: number; future: boolean }[] = []
      for (let dayIndex = 0; dayIndex < 7; dayIndex += 1) {
        const date = addDays(gridStart, weekIndex * 7 + dayIndex)
        const key = dayKey(date)
        const day = byDate.get(key)
        week.push({ date, key, day, total: totalOf(day), future: date.getTime() > today.getTime() })
      }
      weeks.push(week)
    }
    const active = weeks.flat().filter(cell => !cell.future && cell.total > 0)
    const activeDays = weeks.flat().filter(cell => !cell.future && (cell.day?.requests ?? 0) > 0).length
    const ordered = active.map(cell => cell.total).sort((left, right) => left - right)
    const threshold = [0.25, 0.5, 0.75].map(ratio => ordered[Math.min(ordered.length - 1, Math.floor(ratio * ordered.length))] ?? 0)
    const monthLabels: { index: number; label: string }[] = []
    let lastMonth = -1
    weeks.forEach((week, index) => {
      const date = week[0].date
      if (date.getMonth() !== lastMonth) {
        lastMonth = date.getMonth()
        monthLabels.push({ index, label: date.toLocaleDateString(languageTag(), { month: 'short' }) })
      }
    })
    return {
      weeks,
      monthLabels,
      total: active.reduce((sum, cell) => sum + cell.total, 0),
      activeDays,
      level: (value: number) => {
        if (value <= 0) return 0
        if (value <= threshold[0]) return 1
        if (value <= threshold[1]) return 2
        if (value <= threshold[2]) return 3
        return 4
      },
    }
  }, [days])

  function show(cell: { date: Date; key: string; day?: UsageDay; total: number }, element: HTMLElement) {
    const base = chartRef.current?.getBoundingClientRect()
    const rect = element.getBoundingClientRect()
    if (!base) return
    setHover({
      key: cell.key,
      label: `${shortDate(cell.date)} · ${formatTokens(cell.total)} ${tr('Token', 'tokens')} · ${cell.day?.requests ?? 0} ${tr('次请求', 'requests')}`,
      left: rect.left - base.left + rect.width / 2,
      top: rect.top - base.top,
    })
  }

  return <div className="usage-heatmap" ref={chartRef}>
    <div className="usage-heatmap-months" style={{ marginLeft: CELL + CELL_GAP }}>
      {data.monthLabels.map(month => <span key={`${month.index}:${month.label}`} style={{ left: month.index * (CELL + CELL_GAP) }}>{month.label}</span>)}
    </div>
    <div className="usage-heatmap-body">
      <div className="usage-heatmap-days">
        <span style={{ gridRow: 2 }}>{tr('一', 'Mon')}</span>
        <span style={{ gridRow: 4 }}>{tr('三', 'Wed')}</span>
        <span style={{ gridRow: 6 }}>{tr('五', 'Fri')}</span>
      </div>
      <div className="usage-heatmap-weeks">
        {data.weeks.map((week, weekIndex) => <div className="usage-heatmap-week" key={weekIndex}>
          {week.map(cell => cell.future
            ? <span className="usage-heatmap-cell is-future" key={cell.key} />
            : <button
              aria-label={`${shortDate(cell.date)} · ${cell.total} tokens`}
              className={`usage-heatmap-cell level-${data.level(cell.total)}`}
              key={cell.key}
              onBlur={() => setHover(null)}
              onFocus={event => show(cell, event.currentTarget)}
              onMouseEnter={event => show(cell, event.currentTarget)}
              onMouseLeave={() => setHover(null)}
              type="button"
            />)}
        </div>)}
      </div>
    </div>
    {hover ? <div className="usage-heatmap-tooltip" style={{ left: hover.left, top: hover.top }}>{hover.label}</div> : null}
    <div className="usage-heatmap-footer">
      <span>{tr(`过去一年 ${formatTokens(data.total)} Token · ${data.activeDays} 天有活动`, `${formatTokens(data.total)} tokens · ${data.activeDays} active days over the last year`)}</span>
      <span className="usage-heatmap-legend">
        {tr('少', 'Less')}
        {[0, 1, 2, 3, 4].map(level => <i className={`usage-heatmap-cell level-${level}`} key={level} />)}
        {tr('多', 'More')}
      </span>
    </div>
  </div>
}
