import { describe, expect, it } from 'vitest'

import { flattenBacklog, rankMoveForDrop } from '@/lib/backlog'
import type { WorkItem } from '@/api/items'
import { allowsParent, childTypes, opensOnCreate, requiresParent } from '@/lib/hierarchy'

function item(id: string, parentId: string | null = null): WorkItem {
  return {
    id, key: id, type: id === 'E' ? 'epic' : 'feature', title: id, stateId: 'state', descriptionMarkdown: '', descriptionHtml: '', stateCategory: 'proposed', priority: 'none', assigneeId: null, teamId: null, sprintId: null, parentId, points: null, estimateHours: null, remainingHours: null, completedHours: null, dueDate: null, claimedBy: null, claimedAt: null, claimHeartbeatAt: null, version: 1, labels: [], updatedAt: '', isWatching: false, watcherCount: 0,
    rollup: { totalCount: 0, completedCount: 0, pointsTotal: 0, pointsCompleted: 0, remainingHours: 0 },
  }
}

describe('backlog tree and ranks', () => {
  it('flattens expanded hierarchy and retains an orphan at root', () => {
    const rows = flattenBacklog([item('E'), item('F', 'E'), item('O', 'missing')], new Set())
    expect(rows.map((row) => [row.item.key, row.depth])).toEqual([['E', 0], ['F', 1], ['O', 0]])
  })

  it('hides the children of a collapsed row', () => {
    const rows = flattenBacklog([item('E'), item('F', 'E')], new Set(['E']))
    expect(rows.map((row) => [row.item.key, row.hasChildren])).toEqual([['E', true]])
  })

  it('keeps the server rank order instead of sorting by key', () => {
    const rows = flattenBacklog([item('B10'), item('B2'), item('B1')], new Set())
    expect(rows.map((row) => row.item.key)).toEqual(['B10', 'B2', 'B1'])
  })

  it('computes neighbours after removing every selected row', () => {
    const move = rankMoveForDrop([item('A'), item('B'), item('C'), item('D')], new Set(['B', 'C']), 'D')
    expect(move).toEqual({ afterKey: 'A', beforeKey: 'D' })
  })
})

describe('item hierarchy', () => {
  it('lets stories and bugs stand alone or sit under an epic', () => {
    expect(requiresParent('story')).toBe(false)
    expect(requiresParent('bug')).toBe(false)
    expect(allowsParent('epic', 'story')).toBe(true)
    expect(allowsParent('epic', 'bug')).toBe(true)
  })

  it('offers only the children the server accepts', () => {
    expect(childTypes('epic')).toEqual(['story', 'bug', 'feature'])
    expect(childTypes('story')).toEqual(['bug', 'task'])
    expect(childTypes('bug')).toEqual(['task'])
    expect(childTypes('task')).toEqual([])
    expect(requiresParent('feature')).toBe(true)
  })

  it('opens only new stories and bugs for their details', () => {
    expect(opensOnCreate('story')).toBe(true)
    expect(opensOnCreate('bug')).toBe(true)
    expect(opensOnCreate('epic')).toBe(false)
    expect(opensOnCreate('feature')).toBe(false)
    expect(opensOnCreate('task')).toBe(false)
  })
})
