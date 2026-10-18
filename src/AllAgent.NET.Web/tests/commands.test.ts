import { describe, expect, it } from 'vitest'
import { groupCommands, matchCommands } from '@/lib/commandRouter'
import type { CommandInfo } from '@/types'

const catalog: CommandInfo[] = [
  { text: '/help', description: '打开命令面板' },
  { text: '/new', description: '开始新任务，旧会话自动保留', aliases: '/clear' },
  { text: '/ext release_notes', description: '插件命令：把提交记录整理成发布说明', usage: '参数', advanced: true },
  { text: '/timeline', description: '按用户消息定位历史并执行消息操作', usage: '[list|jump]', advanced: true },
  { text: '/file_read', description: '读取工作区内的文本文件，支持行号窗口。', usage: '任务参数', advanced: true, isSkill: true },
  { text: '/release_notes', description: '把提交记录整理成发布说明。', usage: '任务参数', advanced: true, isSkill: true },
]

describe('groupCommands', () => {
  it('splits built-ins, plugin commands and skills like the terminal hint panel', () => {
    const groups = groupCommands(catalog)
    expect(groups.map(group => group.id)).toEqual(['command', 'plugin', 'skill'])
    expect(groups[0].items.map(item => item.text)).toEqual(['/help', '/new', '/timeline'])
    expect(groups[1].items.map(item => item.text)).toEqual(['/ext release_notes'])
    expect(groups[2].items.map(item => item.text)).toEqual(['/file_read', '/release_notes'])
  })

  it('drops empty sections', () => {
    const groups = groupCommands(catalog.filter(item => !item.isSkill))
    expect(groups.map(group => group.id)).toEqual(['command', 'plugin'])
  })
})

describe('matchCommands', () => {
  it('keeps skills in the full list and matches them by prefix', () => {
    expect(matchCommands(catalog, '/').map(item => item.text)).toContain('/release_notes')
    expect(matchCommands(catalog, '/re').map(item => item.text)).toEqual(['/release_notes'])
  })

  it('matches aliases', () => {
    expect(matchCommands(catalog, '/cl').map(item => item.text)).toEqual(['/new'])
  })
})
