import type { WorkItem } from '@/api/items'

export interface BacklogRow { item: WorkItem; depth: number; hasChildren: boolean }

/** Pre-order, stable tree flattening in the order the server returned (rank order).
 * Everything is expanded unless collapsed, so a freshly added child is visible. Orphans are
 * retained at root so a concurrent re-parent cannot make an item disappear from the
 * grooming surface. */
export function flattenBacklog(items: WorkItem[], collapsed: ReadonlySet<string>): BacklogRow[] {
  const children = new Map<string | null, WorkItem[]>()
  const ids = new Set(items.map((item) => item.id))
  for (const item of items) {
    const parent = item.parentId && ids.has(item.parentId) ? item.parentId : null
    const bucket = children.get(parent) ?? []
    bucket.push(item)
    children.set(parent, bucket)
  }
  const rows: BacklogRow[] = []
  const visit = (item: WorkItem, depth: number) => {
    const kids = children.get(item.id) ?? []
    rows.push({ item, depth, hasChildren: kids.length > 0 })
    if (!collapsed.has(item.id)) kids.forEach((child) => visit(child, depth + 1))
  }
  ;(children.get(null) ?? []).forEach((item) => visit(item, 0))
  return rows
}

/** Neighbours are the server's rank grammar. The dragged entries are excluded first,
 * making multi-select drops deterministic and avoiding self-neighbour requests. */
export function rankMoveForDrop(
  ordered: WorkItem[], draggedKeys: ReadonlySet<string>, targetKey: string | null,
): { afterKey?: string; beforeKey?: string } {
  const remaining = ordered.filter((item) => !draggedKeys.has(item.key))
  if (targetKey === null) return { afterKey: remaining.at(-1)?.key }
  const target = remaining.findIndex((item) => item.key === targetKey)
  if (target < 0) return { afterKey: remaining.at(-1)?.key }
  return { afterKey: remaining[target - 1]?.key, beforeKey: remaining[target]?.key }
}
