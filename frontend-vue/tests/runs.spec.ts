import { afterEach, describe, expect, it, vi } from 'vitest'

import {
  cancelRun,
  dispatchRun,
  getRun,
  getRunLog,
  listItemRuns,
  listRuns,
  type Run,
  type RunLogLine,
  type RunLogPage,
} from '@/api/runs'
import {
  applyLogPage,
  canCancelRun,
  emptyLogState,
  formatCost,
  formatTokens,
  hasMoreLogLines,
  isLiveRun,
  lastLogSeq,
  normalizeRunStatus,
  projectKeyOf,
  readRunChoice,
  runDuration,
  runRequesterLabel,
  runSuccessRate,
  runWaitingMessage,
  startRunButton,
  TRUNCATED_SEQ,
  writeRunChoice,
} from '@/lib/runs'

/**
 * Runs are an agent's record of work on an item. The endpoints nest under the
 * organization like every other module's; the lib rules decide what a person is told —
 * who may cancel, when the Hand to agent button sleeps, what a log is missing.
 */

function stubFetch(body: unknown = {}) {
  const fetchMock = vi.fn(
    async (_input: RequestInfo | URL, _init?: RequestInit) =>
      new Response(JSON.stringify(body), {
        status: 200,
        headers: { 'content-type': 'application/json' },
      }),
  )
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

// ofetch hands fetch a URL string, relative or not; a base keeps the parse honest.
const callUrl = (call: [RequestInfo | URL, RequestInit?]) =>
  new URL(String(call[0]), 'http://aictiq.test')

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('the run endpoints', () => {
  it('dispatches to the item with a JSON body', async () => {
    const fetchMock = stubFetch({})

    await dispatchRun('acme', 'PROJ-12', { playbookId: 'p1', agentId: 'a1' })

    const [url, init] = fetchMock.mock.calls[0]!
    expect(String(url)).toBe('/api/v1/orgs/acme/items/PROJ-12/runs')
    expect(init!.method).toBe('POST')
    expect(JSON.parse(String(init!.body))).toEqual({ playbookId: 'p1', agentId: 'a1' })
  })

  it('pages an item’s run history', async () => {
    const fetchMock = stubFetch({ items: [], total: 0 })

    await listItemRuns('acme', 'PROJ-12', 2, 25)

    const url = callUrl(fetchMock.mock.calls[0]!)
    expect(url.pathname).toBe('/api/v1/orgs/acme/items/PROJ-12/runs')
    expect(url.searchParams.get('page')).toBe('2')
    expect(url.searchParams.get('pageSize')).toBe('25')
  })

  it('filters the organization’s runs by whatever was asked', async () => {
    const fetchMock = stubFetch({ items: [], total: 0 })

    await listRuns('acme', {
      project: 'PROJ',
      agent: 'u1',
      status: 'running',
      item: 'PROJ-3',
      page: 1,
      pageSize: 25,
    })

    const url = callUrl(fetchMock.mock.calls[0]!)
    expect(url.pathname).toBe('/api/v1/orgs/acme/runs')
    expect(url.searchParams.get('project')).toBe('PROJ')
    expect(url.searchParams.get('agent')).toBe('u1')
    expect(url.searchParams.get('status')).toBe('running')
    expect(url.searchParams.get('item')).toBe('PROJ-3')
    expect(url.searchParams.get('page')).toBe('1')
    expect(url.searchParams.get('pageSize')).toBe('25')
  })

  it('sends no filter params when nothing was asked', async () => {
    const fetchMock = stubFetch({ items: [], total: 0 })

    await listRuns('acme')

    const url = callUrl(fetchMock.mock.calls[0]!)
    expect(url.pathname).toBe('/api/v1/orgs/acme/runs')
    expect(url.search).toBe('')
  })

  it('reads one run', async () => {
    const fetchMock = stubFetch({})

    await getRun('acme', 'r1')

    expect(String(fetchMock.mock.calls[0]![0])).toBe('/api/v1/orgs/acme/runs/r1')
  })

  it('reads the log after a cursor, one page at a time', async () => {
    const fetchMock = stubFetch({ items: [], truncated: false })

    await getRunLog('acme', 'r1', 41, 500)

    const url = callUrl(fetchMock.mock.calls[0]!)
    expect(url.pathname).toBe('/api/v1/orgs/acme/runs/r1/log')
    expect(url.searchParams.get('after')).toBe('41')
    expect(url.searchParams.get('pageSize')).toBe('500')
  })

  it('asks to cancel with a POST and no body', async () => {
    const fetchMock = stubFetch({})

    await cancelRun('acme', 'r1')

    const [url, init] = fetchMock.mock.calls[0]!
    expect(String(url)).toBe('/api/v1/orgs/acme/runs/r1/cancel')
    expect(init!.method).toBe('POST')
    expect(init!.body).toBeUndefined()
  })
})

describe('lib/runs', () => {
  describe('isLiveRun', () => {
    it('is live before an outcome and settled after one', () => {
      expect(isLiveRun('queued')).toBe(true)
      expect(isLiveRun('assigned')).toBe(true)
      expect(isLiveRun('running')).toBe(true)
      expect(isLiveRun('succeeded')).toBe(false)
      expect(isLiveRun('failed')).toBe(false)
      expect(isLiveRun('cancelled')).toBe(false)
      expect(isLiveRun('timedOut')).toBe(false)
      expect(isLiveRun(undefined)).toBe(false)
    })
  })

  describe('normalizeRunStatus', () => {
    it('accepts the spellings both the REST DTO and the realtime events use', () => {
      expect(normalizeRunStatus('timed_out')).toBe('timedOut')
      expect(normalizeRunStatus('running')).toBe('running')
      expect(normalizeRunStatus('bogus')).toBeNull()
      expect(normalizeRunStatus(null)).toBeNull()
      expect(normalizeRunStatus(undefined)).toBeNull()
    })
  })

  describe('projectKeyOf', () => {
    it('cuts at the last dash, and leaves a bare key alone', () => {
      expect(projectKeyOf('PROJ-12')).toBe('PROJ')
      expect(projectKeyOf('ABC-9-3')).toBe('ABC-9')
      expect(projectKeyOf('PROJ')).toBe('PROJ')
    })
  })

  describe('runDuration', () => {
    const now = new Date('2026-01-01T12:00:00Z')

    it('reads as elapsed time, rounded the way a glance reads', () => {
      expect(runDuration({ startedAt: '2026-01-01T11:58:30Z' }, now)).toBe('1 min')
      expect(runDuration({ startedAt: '2026-01-01T11:59:15Z' }, now)).toBe('45 s')
      expect(runDuration({ startedAt: '2026-01-01T10:25:00Z' }, now)).toBe('1 h 35 m')
    })

    it('stops at the finish, not at now', () => {
      expect(
        runDuration({ startedAt: '2026-01-01T11:58:30Z', finishedAt: '2026-01-01T11:59:00Z' }, now),
      ).toBe('30 s')
    })

    it('says nothing about a run that never started', () => {
      expect(runDuration({ startedAt: null }, now)).toBeNull()
    })
  })

  describe('formatCost and formatTokens', () => {
    it('render money to cents and tokens to a glance', () => {
      expect(formatCost(1.5)).toBe('$1.50')
      expect(formatCost(null)).toBeNull()
      expect(formatTokens(999)).toBe('999')
      expect(formatTokens(1234)).toBe('1.2k')
      expect(formatTokens(2_500_000)).toBe('2.5m')
      expect(formatTokens(null)).toBeNull()
    })
  })

  describe('startRunButton', () => {
    const ctx = {
      canOperateFactory: true,
      projectRole: 'member' as string | null,
      projectArchived: false,
      claimedBy: null,
      hasLiveRun: false,
    }

    it('is hidden from people the factory refuses', () => {
      expect(startRunButton({ ...ctx, canOperateFactory: false }).visible).toBe(false)
      expect(startRunButton({ ...ctx, projectRole: null }).visible).toBe(false)
      expect(startRunButton({ ...ctx, projectRole: 'guest' }).visible).toBe(false)
    })

    it('stays visible and names why it is asleep', () => {
      expect(startRunButton({ ...ctx, hasLiveRun: true })).toEqual({
        visible: true,
        disabledReason: 'An agent is already working on this item.',
      })
      expect(startRunButton({ ...ctx, claimedBy: 'u2', claimedByName: 'Rona' })).toEqual({
        visible: true,
        disabledReason: 'Claimed by Rona.',
      })
      expect(startRunButton({ ...ctx, claimedBy: 'u2' })).toEqual({
        visible: true,
        disabledReason: 'Claimed by someone.',
      })
      expect(startRunButton({ ...ctx, projectArchived: true })).toEqual({
        visible: true,
        disabledReason: 'The project is archived.',
      })
      expect(startRunButton(ctx)).toEqual({ visible: true, disabledReason: null })
    })
  })

  describe('canCancelRun', () => {
    const run = { status: 'running' as const, requestedBy: 'u1', cancelRequested: false }

    it('lets the requester and an org admin stop a live run', () => {
      expect(canCancelRun(run, { userId: 'u1', isProjectAdmin: false })).toBe(true)
      expect(canCancelRun(run, { userId: 'u2', isProjectAdmin: true })).toBe(true)
    })

    it('refuses once a cancel was already asked, or the run is over', () => {
      expect(
        canCancelRun({ ...run, cancelRequested: true }, { userId: 'u1', isProjectAdmin: false }),
      ).toBe(false)
      for (const status of ['succeeded', 'failed', 'cancelled', 'timedOut'] as const) {
        expect(canCancelRun({ ...run, status }, { userId: 'u1', isProjectAdmin: false })).toBe(false)
      }
    })

    it('refuses a bystander', () => {
      expect(canCancelRun(run, { userId: 'u2', isProjectAdmin: false })).toBe(false)
    })

    it('lets only a project Admin cancel a rule-dispatched run, which has no requester', () => {
      const ruleRun = { ...run, requestedBy: null }
      expect(canCancelRun(ruleRun, { userId: 'u1', isProjectAdmin: false })).toBe(false)
      expect(canCancelRun(ruleRun, { userId: null, isProjectAdmin: false })).toBe(false)
      expect(canCancelRun(ruleRun, { userId: 'u1', isProjectAdmin: true })).toBe(true)
    })
  })

  describe('runRequesterLabel', () => {
    it('says nothing for a run a person dispatched', () => {
      expect(
        runRequesterLabel({ requestedBy: 'u1', ruleId: null, ruleName: null }),
      ).toBeNull()
    })

    it('names the rule that dispatched a run with no requester', () => {
      expect(
        runRequesterLabel({ requestedBy: null, ruleId: 'r1', ruleName: 'Start implementation' }),
      ).toBe('Rule: Start implementation')
    })

    it('says the rule is gone when its name did not come back', () => {
      expect(runRequesterLabel({ requestedBy: null, ruleId: 'r1', ruleName: null })).toBe(
        'Rule (deleted)',
      )
    })

    it('says nothing when neither a requester nor a rule is set', () => {
      expect(runRequesterLabel({ requestedBy: null, ruleId: null, ruleName: null })).toBeNull()
    })
  })

  describe('runWaitingMessage', () => {
    it('says what a queued run is waiting for, and nothing once it is not', () => {
      expect(runWaitingMessage({ status: 'queued', harness: 'claude' })).toContain('claude')
      expect(runWaitingMessage({ status: 'running', harness: 'claude' })).toBeNull()
    })
  })

  describe('the log', () => {
    const line = (seq: number, text = `line ${seq}`): RunLogLine => ({
      seq,
      at: '2026-01-01T12:00:00Z',
      stream: 'stdout',
      text,
    })
    const logPage = (items: RunLogLine[], truncated = false): RunLogPage => ({ items, truncated })

    it('merges pages that arrive out of order', () => {
      let state = applyLogPage(emptyLogState, logPage([line(5), line(6)]))
      state = applyLogPage(state, logPage([line(4), line(3)]))
      expect(state.lines.map((l) => l.seq)).toEqual([3, 4, 5, 6])
    })

    it('dedups replays, and the newer text wins a sequence', () => {
      const first = logPage([line(1)])
      let state = applyLogPage(emptyLogState, first)
      state = applyLogPage(state, first)
      expect(state.lines).toHaveLength(1)

      state = applyLogPage(state, logPage([{ ...line(1), text: 'rewritten' }]))
      expect(state.lines).toHaveLength(1)
      expect(state.lines[0]!.text).toBe('rewritten')
    })

    it('turns the truncation marker into the notice, not a line', () => {
      const state = applyLogPage(emptyLogState, logPage([line(TRUNCATED_SEQ)], true))
      expect(state.truncated).toBe(true)
      expect(state.lines).toHaveLength(0)

      // An empty tail fetch leaves the notice standing, and the state untouched.
      expect(applyLogPage(state, logPage([]))).toBe(state)
    })

    it('cursors by the highest sequence seen', () => {
      expect(lastLogSeq(emptyLogState)).toBe(-1)
      let state = applyLogPage(emptyLogState, logPage([line(1), line(2)]))
      state = applyLogPage(state, logPage([line(5)]))
      expect(lastLogSeq(state)).toBe(5)
    })

    it('keeps paging while a page comes back full', () => {
      expect(hasMoreLogLines(logPage([line(1), line(2)]), 2)).toBe(true)
      expect(hasMoreLogLines(logPage([line(1)]), 2)).toBe(false)
    })
  })

  describe('the remembered choice', () => {
    const key = 'aictiq.run.PROJ'

    // happy-dom ships no storage in this setup, so a Map stands in for the browser's.
    const stubStorage = () => {
      const store = new Map<string, string>()
      vi.stubGlobal('localStorage', {
        getItem: (k: string) => store.get(k) ?? null,
        setItem: (k: string, v: string) => void store.set(k, v),
        removeItem: (k: string) => void store.delete(k),
      })
      return store
    }

    it('round-trips the last playbook and agent under the project key', () => {
      stubStorage()
      writeRunChoice('PROJ', { playbookId: 'p1', agentId: 'a1' })
      expect(localStorage.getItem(key)).toBeTruthy()
      expect(readRunChoice('PROJ')).toEqual({ playbookId: 'p1', agentId: 'a1' })
      localStorage.removeItem(key)
      expect(readRunChoice('PROJ')).toBeNull()
    })

    it('reads nothing from an empty or unreadable slot', () => {
      stubStorage()
      expect(readRunChoice('PROJ')).toBeNull()
      localStorage.setItem(key, '{not json')
      expect(readRunChoice('PROJ')).toBeNull()
      localStorage.removeItem(key)
    })
  })

  describe('runSuccessRate', () => {
    const now = new Date('2026-01-01T12:00:00Z')
    const since = new Date('2026-01-01T11:00:00Z')
    const inWindow = '2026-01-01T11:30:00Z'
    const beforeWindow = '2026-01-01T10:00:00Z'
    const runs: Pick<Run, 'status' | 'queuedAt'>[] = [
      { status: 'succeeded', queuedAt: inWindow },
      { status: 'succeeded', queuedAt: inWindow },
      { status: 'failed', queuedAt: inWindow },
      { status: 'running', queuedAt: inWindow },
      { status: 'failed', queuedAt: beforeWindow },
    ]

    it('counts only runs that reached an outcome inside the window', () => {
      expect(runSuccessRate(runs, since, now)).toEqual({ rate: 67, total: 3 })
    })

    it('says nothing when nothing finished', () => {
      expect(runSuccessRate([{ status: 'queued', queuedAt: inWindow }], since, now)).toEqual({
        rate: null,
        total: 0,
      })
    })
  })
})
