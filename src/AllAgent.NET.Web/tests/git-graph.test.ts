import { describe, expect, it } from 'vitest'
import { buildGraph } from '../src/lib/gitGraph'
import type { GitCommit } from '../src/types'

function commit(hash: string, parents: string[], refs: string[] = []): GitCommit {
  return {
    hash: hash.padEnd(40, '0'),
    short: hash.slice(0, 7).padEnd(7, '0'),
    author: 'Tester',
    authorEmail: 'tester@example.com',
    date: new Date('2026-01-01T00:00:00Z').toISOString(),
    subject: `commit ${hash}`,
    refs,
    parents: parents.map(parent => parent.padEnd(40, '0')),
  }
}

describe('提交图泳道计算', () => {
  it('画一条直线分支：同一泳道串联提交且颜色稳定', () => {
    const rows = buildGraph([commit('c', ['b']), commit('b', ['a']), commit('a', [])])

    expect(rows).toHaveLength(3)
    expect(rows.map(row => row.lane)).toEqual([0, 0, 0])
    expect(new Set(rows.map(row => row.color)).size).toBe(1)
    expect(rows.every(row => row.laneCount === 1)).toBe(true)
    // 中间提交的父提交继续留在同一泳道，画成贯穿线
    expect(rows[0].straight).toEqual([{ lane: 0, color: 0 }])
    expect(rows[0].curves).toEqual([])
    expect(rows[2].straight).toEqual([])
    expect(rows[2].incoming).toEqual([{ lane: 0, color: 0 }])
  })

  it('合并提交开出第二条泳道并画曲线', () => {
    const rows = buildGraph([
      commit('m', ['a', 'b'], ['HEAD → main']),
      commit('b', ['a'], ['feature/panels']),
      commit('a', []),
    ])

    const merge = rows[0]
    expect(merge.lane).toBe(0)
    expect(merge.laneCount).toBe(2)
    expect(merge.straight).toEqual([{ lane: 0, color: 0 }])
    expect(merge.curves).toEqual([{ lane: 1, color: 1 }])
    expect(merge.incoming).toEqual([])

    // feature 分支提交坐在第二条泳道上，并把重复的泳道折回主线
    const feature = rows[1]
    expect(feature.lane).toBe(1)
    expect(feature.incoming).toEqual([{ lane: 1, color: 1 }])
    expect(feature.straight).toEqual([])
    expect(feature.converge).toEqual([{ from: 1, to: 0, color: 1 }])
    expect(feature.through).toEqual([{ lane: 0, color: 0 }])

    const base = rows[2]
    expect(base.lane).toBe(0)
    expect(base.laneCount).toBe(1)
    expect(base.incoming).toEqual([{ lane: 0, color: 0 }])
  })

  it('没有父提交的根提交结束它的泳道', () => {
    const rows = buildGraph([commit('a', [], ['tag: v1.0.0'])])
    expect(rows[0].straight).toEqual([])
    expect(rows[0].curves).toEqual([])
    expect(rows[0].incoming).toEqual([{ lane: 0, color: 0 }])
    expect(rows[0].laneCount).toBe(1)
  })

  it('父提交不在当前页时泳道继续延伸到页尾', () => {
    // 合并提交的第二个父提交没有出现在当前页：它所在的泳道保持绘制，直到页尾
    const rows = buildGraph([commit('m', ['a', 'x']), commit('a', [])])
    expect(rows[0].laneCount).toBe(2)
    expect(rows[0].curves).toEqual([{ lane: 1, color: 1 }])
    expect(rows[1].through).toEqual([{ lane: 1, color: 1 }])
    expect(rows[1].incoming).toEqual([{ lane: 0, color: 0 }])
    expect(rows[1].laneCount).toBe(2)
  })
})
