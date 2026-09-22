import { flushPromises, mount } from '@vue/test-utils'
import { afterEach, describe, expect, it, vi } from 'vitest'

import type { Label } from '@/api/labels'
import LabelPicker, { presetLabelColors } from '@/components/common/LabelPicker.vue'

/**
 * The picker's own contract: search narrows the list, a click toggles the id in and out
 * of the v-model, and - only when the caller says a Guest is not looking at it - the
 * typed text can mint a brand new label with a preset colour and select it immediately.
 */

const labels: Label[] = [
  { id: 'l1', name: 'frontend', color: '#eda45c', description: null, group: 'type', itemCount: 2, version: 1 },
  { id: 'l2', name: 'backend', color: '#6aa9d8', description: null, group: 'type', itemCount: 1, version: 1 },
  { id: 'l3', name: 'urgent', color: null, description: null, group: null, itemCount: 0, version: 1 },
]

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

function render(props: Partial<InstanceType<typeof LabelPicker>['$props']> = {}) {
  return mount(LabelPicker, {
    props: { modelValue: [], labels, slug: 'acme', projectKey: 'WEB', ...props },
  })
}

async function open(wrapper: ReturnType<typeof render>) {
  await wrapper.find('[role="button"]').trigger('click')
  return wrapper
}

describe('LabelPicker', () => {
  it('lists every label once opened', async () => {
    const wrapper = await open(render())

    expect(wrapper.findAll('[role="option"]')).toHaveLength(3)
  })

  it('filters by name and by group', async () => {
    const wrapper = await open(render())

    await wrapper.find('input').setValue('back')
    expect(wrapper.findAll('[role="option"]')).toHaveLength(1)
    expect(wrapper.find('[role="option"]').text()).toContain('backend')

    await wrapper.find('input').setValue('type')
    expect(wrapper.findAll('[role="option"]')).toHaveLength(2)
  })

  it('toggles a label into the selection on click', async () => {
    const wrapper = await open(render())

    await wrapper.findAll('[role="option"]')[0]!.trigger('click')

    expect(wrapper.emitted('update:modelValue')).toEqual([[['l1']]])
  })

  it('toggles a selected label back out on a second click', async () => {
    const wrapper = await open(render({ modelValue: ['l1', 'l2'] }))

    await wrapper.findAll('[role="option"]')[0]!.trigger('click')

    expect(wrapper.emitted('update:modelValue')).toEqual([[['l2']]])
  })

  it('shows the current selection as removable chips on the trigger', () => {
    const wrapper = render({ modelValue: ['l1'] })

    expect(wrapper.text()).toContain('frontend')
    expect(wrapper.find('button[aria-label="Remove frontend"]').exists()).toBe(true)
  })

  it('removes a selection when its chip is removed', async () => {
    const wrapper = render({ modelValue: ['l1', 'l2'] })

    await wrapper.find('button[aria-label="Remove frontend"]').trigger('click')

    expect(wrapper.emitted('update:modelValue')).toEqual([[['l2']]])
  })

  it('hides the create affordance when canCreate is false, even with unmatched text', async () => {
    const wrapper = await open(render({ canCreate: false }))
    await wrapper.find('input').setValue('docs')

    expect(wrapper.text()).not.toContain('Create')
  })

  it('offers to create the typed text when canCreate is true and nothing matches', async () => {
    const wrapper = await open(render({ canCreate: true }))
    await wrapper.find('input').setValue('docs')

    expect(wrapper.text()).toContain('Create "docs"')
  })

  it('does not offer to create a label that already exists, case-insensitively', async () => {
    const wrapper = await open(render({ canCreate: true }))
    await wrapper.find('input').setValue('Frontend')

    expect(wrapper.text()).not.toContain('Create')
  })

  it('creates the label, selects it, and lets a caller append it to its own list', async () => {
    const fetchMock = stubFetch({
      id: 'l4',
      name: 'docs',
      color: presetLabelColors[0]!.hex,
      description: null,
      group: null,
      itemCount: 0,
      version: 1,
    })
    const wrapper = await open(render({ canCreate: true }))
    await wrapper.find('input').setValue('docs')

    const createButton = wrapper.findAll('button').find((b) => b.text().includes('Create "docs"'))
    expect(createButton).toBeDefined()
    await createButton!.trigger('click')
    await flushPromises()

    const [url, init] = fetchMock.mock.calls[0]!
    expect(String(url)).toBe('/api/v1/orgs/acme/projects/WEB/labels')
    expect(init!.method).toBe('POST')
    expect(JSON.parse(String(init!.body))).toEqual({ name: 'docs', color: presetLabelColors[0]!.hex })

    expect(wrapper.emitted('created')?.[0]?.[0]).toMatchObject({ id: 'l4', name: 'docs' })
    expect(wrapper.emitted('update:modelValue')).toEqual([[['l4']]])
  })
})
