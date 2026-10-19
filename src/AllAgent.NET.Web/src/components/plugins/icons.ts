import {
  Activity, BarChart3, Blocks, Gauge, Github, Globe, LayoutDashboard, ListTodo, Package, Plug, Puzzle, Rocket,
  Sparkles, SquareTerminal, Star, Wand2, Wrench, type LucideIcon,
} from 'lucide-react'

// Curated icon set plugin manifests may name; anything unknown falls back to the puzzle piece.
const ICONS: Record<string, LucideIcon> = {
  activity: Activity,
  blocks: Blocks,
  chart: BarChart3,
  dashboard: LayoutDashboard,
  gauge: Gauge,
  github: Github,
  globe: Globe,
  list: ListTodo,
  package: Package,
  plug: Plug,
  puzzle: Puzzle,
  rocket: Rocket,
  sparkles: Sparkles,
  star: Star,
  terminal: SquareTerminal,
  wand: Wand2,
  wrench: Wrench,
}

export function pluginIcon(name?: string | null): LucideIcon {
  return (name && ICONS[name.trim().toLowerCase()]) || Puzzle
}
