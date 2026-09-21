import { flushPromises, mount, RouterLinkStub } from '@vue/test-utils'
import { afterEach, describe, expect, it, vi } from 'vitest'

import type { Run, RunLogLine } from '@/api/runs'
import RunLog from '@/components/factory/RunLog.vue'

/**
 * The log never trusts a push for its contents: every nudge is a fetch after the highest
 * sequence on screen, so a gap the hub left is filled by the next one — and a nudge that
 * lands while a fetch is already in flight is not lost, because its lines may sit past
 * the cursor that fetch used.
 */
const run = {
  id: 'r-1',
  itemKey: 'PROJ-1',
  status: 'running',
  harness: 'claude',
  cancelRequested: false,
} as Run

const line = (seq: number): RunLogLine => ({
  seq,
  at: '2026-01-01T12:00:00Z',
  stream: 'stdout',
  text: `line ${seq}`,
})

function stubLog(pages: RunLogLine[][]) {
  const afters: number[] = []
  const pending: (() => void)[] = []
  let hold = false
  const fetchMock = vi.fn(async (input: unknown) => {
    const url = new URL(String(input), 'http://localhost')
    afters.push(Number(url.searchParams.get('after')))
    if (hold) await new Promise<void>((resolve) => pending.push(resolve))
    const items = pages.shift() ?? []
    return new Response(JSON.stringify({ items, truncated: false }), {
      status: 200,
      headers: { 'content-type': 'application/json' },
    })
  })
  vi.stubGlobal('fetch', fetchMock)
  return {
    afters,
    holdNext: () => (hold = true),
    release: () => {
      hold = false
      pending.splice(0).forEach((resolve) => resolve())
    },
  }
}

const mountLog = () =>
  mount(RunLog, {
    props: { slug: 'acme', run },
    global: { stubs: { RouterLink: RouterLinkStub } },
  })

afterEach(() => {
  vi.useRealTimers()
  vi.unstubAllGlobals()
})

describe('RunLog', () => {
  it('fetches after the highest sequence it holds, so a skipped event heals', async () => {
    vi.useFakeTimers()
    // The hub announced seq 3 but never seq 2: the tail fetch asks for everything after 1.
    const log = stubLog([[line(0), line(1)], [line(2), line(3)]])
    const wrapper = mountLog()
    await vi.runOnlyPendingTimersAsync()
    await flushPromises()

    ;(wrapper.vm as unknown as { tail: () => void }).tail()
    await vi.advanceTimersByTimeAsync(100)
    await flushPromises()

    expect(log.afters).toEqual([-1, 1])
    expect(wrapper.text()).toContain('4 lines')
  })

  it('fetches again when a nudge arrives during a fetch', async () => {
    vi.useFakeTimers()
    const log = stubLog([[line(0)], [line(1)], [line(2)]])
    const wrapper = mountLog()
    await vi.runOnlyPendingTimersAsync()
    await flushPromises()
    const vm = wrapper.vm as unknown as { tail: () => void }

    log.holdNext()
    vm.tail()
    await vi.advanceTimersByTimeAsync(100)
    // The first tail fetch is in flight; another event arrives.
    vm.tail()
    await vi.advanceTimersByTimeAsync(100)
    log.release()
    await flushPromises()
    await vi.runOnlyPendingTimersAsync()
    await flushPromises()

    expect(log.afters).toEqual([-1, 0, 1])
    expect(wrapper.text()).toContain('3 lines')
  })
})

describe('RunLog across refetches', () => {
  it('keeps its lines when the run is refetched with a new status', async () => {
    vi.useFakeTimers()
    const log = stubLog([[line(0), line(1)], []])
    const wrapper = mountLog()
    await vi.runOnlyPendingTimersAsync()
    await flushPromises()

    // The same run, a new object: what every refetch hands the page.
    await wrapper.setProps({ run: { ...run, status: 'succeeded' } })
    await vi.runOnlyPendingTimersAsync()
    await flushPromises()

    // Catch-up fetches after the last line, never a reload from the start.
    expect(log.afters[0]).toBe(-1)
    expect(log.afters.slice(1)).not.toContain(-1)
    expect(log.afters.length).toBeGreaterThan(1)
    expect(wrapper.text()).toContain('2 lines')
  })
})
