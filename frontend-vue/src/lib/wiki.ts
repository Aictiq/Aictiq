import type { WikiTreePage } from '@/api/wiki'

export interface WikiOutlineRow {
  page: WikiTreePage
  depth: number
}

/** Flatten a page hierarchy into stable reading order while retaining its display depth. */
export function wikiOutline(source: readonly WikiTreePage[]): WikiOutlineRow[] {
  const pages = [...source].sort((left, right) => left.position - right.position)
  const byParent = new Map<string | null, WikiTreePage[]>()
  for (const page of pages) {
    const siblings = byParent.get(page.parentId) ?? []
    siblings.push(page)
    byParent.set(page.parentId, siblings)
  }

  const rows: WikiOutlineRow[] = []
  const visit = (parentId: string | null, depth: number) => {
    for (const page of byParent.get(parentId) ?? []) {
      rows.push({ page, depth })
      visit(page.id, depth + 1)
    }
  }
  visit(null, 0)
  return rows
}
