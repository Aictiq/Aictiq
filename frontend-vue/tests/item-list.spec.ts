import { mount } from '@vue/test-utils'
import { afterEach, describe, expect, it, vi } from 'vitest'

import ItemFilterBar from '@/components/items/ItemFilterBar.vue'
import { parseItemFilter, serializeItemFilter } from '@/lib/item-filters'
import { reduceListKeyboard } from '@/lib/item-list-keyboard'

describe('item filter grammar', () => {
  it('normalizes whitespace and round-trips terms', () => {
    expect(serializeItemFilter(parseItemFilter(' state:active   assignee:@me '))).toBe('state:active assignee:@me')
  })
})

describe('item list keyboard reducer', () => {
  it('moves within bounds and toggles the focused selection', () => {
    const down = reduceListKeyboard({ index: 0, selected: new Set() }, 'down', ['A', 'B'])
    expect(down.index).toBe(1)
    expect(reduceListKeyboard(down, 'toggle', ['A', 'B']).selected).toEqual(new Set(['B']))
    expect(reduceListKeyboard(down, 'down', ['A', 'B']).index).toBe(1)
  })
})

describe('ItemFilterBar', () => {
  afterEach(() => vi.useRealTimers())

  it('applies the filter only on Enter, never per keystroke', async () => {
    const wrapper = mount(ItemFilterBar, { props: { filter: '', search: '' } })
    await wrapper.find('input[aria-label="Advanced filter"]').setValue('state:')
    expect(wrapper.emitted('update:filter')).toBeUndefined()

    await wrapper.find('input[aria-label="Advanced filter"]').setValue('  state:active   type:bug ')
    await wrapper.find('form').trigger('submit')
    expect(wrapper.emitted('update:filter')).toEqual([['state:active type:bug']])
  })

  it('applies search after a pause in typing, once', async () => {
    vi.useFakeTimers()
    const wrapper = mount(ItemFilterBar, { props: { filter: '', search: '' } })
    const input = wrapper.find('input[aria-label="Search items"]')
    for (const text of ['t', 'ti', 'tim']) {
      await input.setValue(text)
      vi.advanceTimersByTime(100)
    }
    expect(wrapper.emitted('update:search')).toBeUndefined()
    vi.advanceTimersByTime(300)
    expect(wrapper.emitted('update:search')).toEqual([['tim']])
  })

  it('shows the server message for a filter it rejected', () => {
    const wrapper = mount(ItemFilterBar, {
      props: { filter: 'state:bogus', search: '', error: 'Unknown state category.' },
    })
    expect(wrapper.find('[role="alert"]').text()).toBe('Unknown state category.')
    expect(wrapper.find('input[aria-label="Advanced filter"]').attributes('aria-invalid')).toBe('true')
  })
})
