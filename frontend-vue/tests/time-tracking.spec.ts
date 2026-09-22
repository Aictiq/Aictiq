import { flushPromises, mount } from '@vue/test-utils'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { logTime } from '@/api/time-tracking'
import TimeTrackingPopover from '@/components/common/TimeTrackingPopover.vue'

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

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('the time tracking endpoint', () => {
  it('logs hours against the item with its current version', async () => {
    const fetchMock = stubFetch({ key: 'WEB-42', version: 8 })

    await logTime('acme', 'WEB-42', 1.25, 7)

    const [url, init] = fetchMock.mock.calls[0]!
    expect(String(url)).toBe('/api/v1/orgs/acme/items/WEB-42/log-time')
    expect(init!.method).toBe('POST')
    expect(JSON.parse(String(init!.body))).toEqual({ hours: 1.25, version: 7 })
    expect(new Headers(init!.headers).get('X-Aictiq-Request')).toBe('1')
  })
})

describe('TimeTrackingPopover', () => {
  function render() {
    return mount(TimeTrackingPopover, {
      props: {
        slug: 'acme',
        itemKey: 'WEB-42',
        version: 7,
        remainingHours: 4.5,
        completedHours: 2,
      },
    })
  }

  it('shows current hours and logs a valid decimal amount', async () => {
    const fetchMock = stubFetch({
      key: 'WEB-42',
      estimateHours: 6.5,
      remainingHours: 3.25,
      completedHours: 3.25,
      version: 8,
    })
    const wrapper = render()

    await wrapper.get('button[aria-haspopup="dialog"]').trigger('click')
    expect(wrapper.text()).toContain('Remaining 4.5h')
    expect(wrapper.text()).toContain('Completed 2h')

    await wrapper.get('input[type="number"]').setValue('1.25')
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    expect(JSON.parse(String(fetchMock.mock.calls[0]![1]!.body))).toEqual({ hours: 1.25, version: 7 })
    expect(wrapper.emitted('logged')?.[0]?.[0]).toMatchObject({ remainingHours: 3.25, completedHours: 3.25, version: 8 })
    expect(wrapper.find('form').exists()).toBe(false)
  })

  it('keeps the popover open and explains invalid input without making a request', async () => {
    const fetchMock = stubFetch()
    const wrapper = render()

    await wrapper.get('button[aria-haspopup="dialog"]').trigger('click')
    await wrapper.get('input[type="number"]').setValue('0')
    await wrapper.get('form').trigger('submit')

    expect(wrapper.get('[role="alert"]').text()).toContain('positive number')
    expect(fetchMock).not.toHaveBeenCalled()
  })
})
