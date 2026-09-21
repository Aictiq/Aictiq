import { describe, expect, it } from 'vitest'

import { columnForState, destinationIsAtWipLimit, moveBoardCard, unmappedStates } from '@/lib/board'
import type { Board } from '@/api/boards'
import type { WorkItem } from '@/api/items'

const card = (id: string, stateId: string): WorkItem => ({ id, key: `WEB-${id}`, type: 'bug', title: id, stateId, boardColumnId: stateId === 'todo' ? 'todo-column' : 'doing-column', descriptionMarkdown: '', descriptionHtml: '', stateCategory: 'active', priority: 'none', assigneeId: null, teamId: null, sprintId: null, parentId: null, points: null, estimateHours: null, remainingHours: null, completedHours: null, dueDate: null, claimedBy: null, claimedAt: null, claimHeartbeatAt: null, version: 1, labels: [], updatedAt: '', isWatching: false, watcherCount: 0, rollup: { totalCount: 0, completedCount: 0, pointsTotal: 0, pointsCompleted: 0, remainingHours: 0 } })
const board = (): Board => ({ id: 'board', teamId: 'team', kind: 'kanban', swimlane: 'none', cardFields: [], types: ['bug'], version: 1, columns: [{ id: 'todo-column', name: 'Todo', stateIds: ['todo'], generalState: 'new', wipLimit: null, count: 1, wipExceeded: false, cards: [card('1', 'todo')] }, { id: 'doing-column', name: 'Doing', stateIds: ['doing'], generalState: 'doing', wipLimit: 1, count: 0, wipExceeded: false, cards: [] }] })

describe('board column mapping', () => {
  it('maps a state to its configured board column', () => expect(columnForState(board(), 'doing')?.name).toBe('Doing'))
  it('optimistically moves a card without mutating the rollback source', () => {
    const source = board(); const next = moveBoardCard(source, source.columns[0]!.cards[0]!, 'doing', 'doing-column')
    expect(source.columns[0]!.cards).toHaveLength(1); expect(next.columns[0]!.cards).toHaveLength(0)
    expect(next.columns[1]!.cards[0]!.stateId).toBe('doing')
  })
  it('recognizes a destination WIP limit before the write', () => {
    const source = board(); const full = { ...source.columns[1]!, count: 1 }
    expect(destinationIsAtWipLimit(full, source.columns[0]!.cards[0]!)).toBe(true)
  })
  it('reorders a card within its column', () => {
    const source = board()
    source.columns[0]!.cards.push(card('2', 'todo'))
    source.columns[0]!.count = 2
    const next = moveBoardCard(source, source.columns[0]!.cards[1]!, 'todo', 'todo-column', null)
    expect(next.columns[0]!.cards.map((entry) => entry.id)).toEqual(['2', '1'])
  })
})

describe('board settings', () => {
  it('lists workflow states no column shows yet', () => {
    const states = [{ id: 'todo', name: 'Todo' }, { id: 'doing', name: 'Doing' }, { id: 'review', name: 'Review' }]
    expect(unmappedStates(board().columns, states).map((state) => state.name)).toEqual(['Review'])
  })
})
