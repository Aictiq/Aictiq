import { describe, expect, it } from 'vitest'

import type { WorkItem } from '@/api/items'
import type { Workflow } from '@/api/workflows'
import { allowedStates, replaceListedItem } from '@/lib/inline-edits'

function item(id: string): WorkItem {
  return {
    id, key: id, type: 'story', title: id, stateId: 'new', descriptionMarkdown: '', descriptionHtml: '', stateCategory: 'proposed', priority: 'none', assigneeId: null, teamId: null, sprintId: null, parentId: null, points: null, estimateHours: null, remainingHours: null, completedHours: null, dueDate: null, claimedBy: null, claimedAt: null, claimHeartbeatAt: null, version: 1, labels: [], updatedAt: '', isWatching: false, watcherCount: 0,
    rollup: { totalCount: 0, completedCount: 0, pointsTotal: 0, pointsCompleted: 0, remainingHours: 0 },
  }
}

const state = (id: string, position: number) =>
  ({ id, name: id, category: 'active', position, color: null, isInitial: position === 0 }) as const

function workflow(transitions: Workflow['transitions']): Workflow {
  return { id: 'w', name: 'Default', isDefault: true, version: 1, states: [state('done', 2), state('new', 0), state('active', 1)], transitions }
}

describe('allowed states', () => {
  it('offers every state, in workflow order, when the workflow has no transitions', () => {
    expect(allowedStates(workflow([]), 'new').map((s) => s.id)).toEqual(['new', 'active', 'done'])
  })

  it('offers only listed moves from the current state, plus the current state itself', () => {
    const flow = workflow([{ fromStateId: 'new', toStateId: 'active' }, { fromStateId: 'active', toStateId: 'done' }])
    expect(allowedStates(flow, 'new').map((s) => s.id)).toEqual(['new', 'active'])
    expect(allowedStates(flow, 'active').map((s) => s.id)).toEqual(['active', 'done'])
  })

  it('treats a transition without a source as reachable from anywhere', () => {
    const flow = workflow([{ fromStateId: null, toStateId: 'done' }])
    expect(allowedStates(flow, 'new').map((s) => s.id)).toEqual(['new', 'done'])
  })

  it('offers nothing before the workflow has loaded', () => {
    expect(allowedStates(undefined, 'new')).toEqual([])
  })
})

describe('replacing a listed item', () => {
  it('swaps the item in a page and leaves its neighbours untouched', () => {
    const page = { items: [item('A'), item('B')], page: 1, pageSize: 50, totalCount: 2 }
    const next = replaceListedItem(page, 'B', (entry) => ({ ...entry, priority: 'high' }))
    expect(next.items.map((entry) => entry.priority)).toEqual(['none', 'high'])
    expect(next.items[0]).toBe(page.items[0])
    expect(page.items[1]!.priority).toBe('none')
  })

  it('finds the item in whichever backlog section holds it', () => {
    const backlog = { currentSprint: [item('A')], nextSprint: [], backlog: [item('B')], currentSprintCount: 1, nextSprintCount: 0, backlogCount: 1 }
    const next = replaceListedItem(backlog, 'B', (entry) => ({ ...entry, assigneeId: 'u1' }))
    expect(next.backlog[0]!.assigneeId).toBe('u1')
    expect(next.currentSprint).toBe(backlog.currentSprint)
  })

  it('returns data that does not list the item unchanged', () => {
    const page = { items: [item('A')], page: 1, pageSize: 50, totalCount: 1 }
    expect(replaceListedItem(page, 'Z', (entry) => entry)).toBe(page)
    expect(replaceListedItem(undefined, 'A', (entry) => entry)).toBeUndefined()
    const other = { columns: [] }
    expect(replaceListedItem(other, 'A', (entry) => entry)).toBe(other)
  })
})
