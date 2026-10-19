import type { GitCommit } from '@/types'

/** Lane palette (VSCode-ish); a lane keeps its colour while it carries the same branch line. */
export const GRAPH_COLORS = [
  '#e06c75', '#61afef', '#98c379', '#c678dd', '#e5c07b',
  '#56b6c2', '#d19a66', '#7c9cff', '#e57fa0', '#6fd18f',
]

export function graphColor(index: number): string {
  return GRAPH_COLORS[((index % GRAPH_COLORS.length) + GRAPH_COLORS.length) % GRAPH_COLORS.length]
}

export interface GraphRow {
  commit: GitCommit
  /** Lane of the commit dot. */
  lane: number
  color: number
  /** Lanes visible on this row; the SVG column uses this width. */
  laneCount: number
  /** Branch lines passing straight through (top edge → bottom edge). */
  through: Array<{ lane: number; color: number }>
  /** Lines arriving from the top edge into the dot (top → middle). */
  incoming: Array<{ lane: number; color: number }>
  /** The dot's lane continuing straight down (top → bottom) to the first parent. */
  straight: Array<{ lane: number; color: number }>
  /** Curves from the dot down into another lane (middle → bottom), one per extra parent. */
  curves: Array<{ lane: number; color: number }>
  /** Duplicate lanes folding into another lane (top → bottom). */
  converge: Array<{ from: number; to: number; color: number }>
}

interface Slot { hash: string; color: number }

/**
 * Lays a topological commit page (children before parents, as `git log --topo-order` yields) into
 * lanes. State per row: which lane each unprocessed parent hash lives in; colours stick to a lane
 * while it carries the same line. Duplicate lanes for one hash fold into the leftmost.
 */
export function buildGraph(commits: GitCommit[]): GraphRow[] {
  const lanes: Array<Slot | null> = []
  let nextColor = 0
  const rows: GraphRow[] = []

  for (const commit of commits) {
    let entry = lanes.findIndex(slot => slot?.hash === commit.hash)
    if (entry < 0) {
      entry = lanes.findIndex(slot => slot === null)
      if (entry < 0) entry = lanes.length
      lanes[entry] = { hash: commit.hash, color: nextColor++ }
    }
    const color = lanes[entry]!.color
    const topSlots = lanes.map(slot => (slot ? { ...slot } : null))

    const parents = commit.parents.filter(hash => hash.length > 0)
    lanes[entry] = parents.length > 0 ? { hash: parents[0], color } : null
    const parentLanes: number[] = parents.length > 0 ? [entry] : []
    for (let index = 1; index < parents.length; index += 1) {
      const existing = lanes.findIndex(slot => slot?.hash === parents[index])
      if (existing >= 0) { parentLanes.push(existing); continue }
      let slot = lanes.findIndex(candidate => candidate === null)
      if (slot < 0) { slot = lanes.length; lanes.push(null) }
      lanes[slot] = { hash: parents[index], color: nextColor++ }
      parentLanes.push(slot)
    }

    const converge: GraphRow['converge'] = []
    for (let index = 0; index < lanes.length; index += 1) {
      const slot = lanes[index]
      if (!slot) continue
      const keep = lanes.findIndex(candidate => candidate?.hash === slot.hash)
      if (keep !== index && keep >= 0) {
        converge.push({ from: index, to: keep, color: topSlots[index]?.color ?? slot.color })
        lanes[index] = null
      }
    }
    while (lanes.length > 0 && lanes[lanes.length - 1] === null) lanes.pop()

    const through: GraphRow['through'] = []
    const incoming: GraphRow['incoming'] = []
    for (let index = 0; index < topSlots.length; index += 1) {
      const top = topSlots[index]
      if (!top) continue
      if (top.hash === commit.hash) { incoming.push({ lane: index, color: top.color }); continue }
      const bottom = lanes[index]
      if (bottom && bottom.hash === top.hash) through.push({ lane: index, color: bottom.color })
      // A lane whose hash vanishes without being this commit cannot happen in a topological page.
    }

    const straight = parents.length > 0 && lanes[entry]?.hash === parents[0] ? [{ lane: entry, color }] : []
    if (straight.length > 0) {
      const index = incoming.findIndex(item => item.lane === entry)
      if (index >= 0) incoming.splice(index, 1)
    }
    const curves = parents
      .map((_, index) => index)
      .filter(index => parentLanes[index] !== entry)
      .map(index => ({ lane: parentLanes[index], color: lanes[parentLanes[index]]?.color ?? color }))

    rows.push({
      commit,
      lane: entry,
      color,
      laneCount: Math.max(topSlots.length, lanes.length, entry + 1, 1),
      through,
      incoming,
      straight,
      curves,
      converge,
    })
  }
  return rows
}
