import { afterEach, describe, expect, it, vi } from 'vitest'

import { editItem, transitionItem, type WorkItem } from '@/api/items'

/** PATCH assigns nullable fields absolutely, so an edit of the title alone must still carry
 * the team and parent - omitting them took the item off its board and out of its epic. */

const story: WorkItem = {
  id: 'i1', key: 'DGO-4', type: 'story', title: 'Old', stateId: 's1', descriptionMarkdown: 'old', descriptionHtml: '', stateCategory: 'proposed',
  priority: 'high', assigneeId: 'u1', teamId: 't1', sprintId: 'sp1', parentId: 'e1', points: 3, estimateHours: null, remainingHours: null,
  completedHours: null, dueDate: '2026-10-01', version: 7, labels: [], updatedAt: '', isWatching: false, watcherCount: 0, claimedBy: null,
  claimedAt: null, claimHeartbeatAt: null, rollup: { totalCount: 0, completedCount: 0, pointsTotal: 0, pointsCompleted: 0, remainingHours: 0 },
}

afterEach(() => vi.unstubAllGlobals())

describe('editing an item', () => {
  it('keeps every field the edit did not change', async () => {
    const fetchMock = vi.fn(async (_input: RequestInfo | URL, _init?: RequestInit) => new Response(JSON.stringify(story), { status: 200, headers: { 'content-type': 'application/json' } }))
    vi.stubGlobal('fetch', fetchMock)

    await editItem('acme', story, { descriptionMarkdown: 'new' })

    const [url, init] = fetchMock.mock.calls[0]!
    expect(String(url)).toContain('/orgs/acme/items/DGO-4')
    expect(JSON.parse(String(init?.body))).toMatchObject({
      descriptionMarkdown: 'new', title: 'Old', teamId: 't1', parentId: 'e1', assigneeId: 'u1', points: 3, dueDate: '2026-10-01', priority: 'high', version: 7,
    })
  })

  it('uses the transition endpoint for a status change', async () => {
    const fetchMock = vi.fn(async (_input: RequestInfo | URL, _init?: RequestInit) => new Response(JSON.stringify(story), { status: 200, headers: { 'content-type': 'application/json' } }))
    vi.stubGlobal('fetch', fetchMock)

    await transitionItem('acme', story.key, { toStateId: 's2', version: story.version })

    const [url, init] = fetchMock.mock.calls[0]!
    expect(String(url)).toContain('/orgs/acme/items/DGO-4/transition')
    expect(init?.method).toBe('POST')
    expect(JSON.parse(String(init?.body))).toEqual({ toStateId: 's2', version: 7 })
  })
})
