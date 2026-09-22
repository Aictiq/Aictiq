import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'

import UserSelect, { type UserOption } from '@/components/common/UserSelect.vue'

/**
 * The picker is where a person and an agent are most easily confused: they are the same
 * kind of row in the same roster, and the moment work is handed to one of them is exactly
 * the moment that has to be obvious. So the badge is asserted here, not only in
 * `UserAvatar` - a picker that dropped it would still render perfectly.
 *
 * The rest is the contract every caller relies on: filtering finds people by whichever of
 * their name and address the searcher happens to know, a disabled option cannot be picked
 * by mouse or by keyboard, and multi-select stays open because picking three people one
 * at a time is the normal case.
 */

const options: UserOption[] = [
  { userId: 'u1', displayName: 'Ada Lovelace', email: 'ada@aictiq.local' },
  { userId: 'u2', displayName: 'Grace Hopper', email: 'grace@aictiq.local' },
  { userId: 'u3', displayName: 'Nightly Triage', email: 'agent+triage@aictiq.local', isAgent: true },
  {
    userId: 'u4',
    displayName: 'Alan Turing',
    email: 'alan@aictiq.local',
    disabled: true,
    hint: 'already a member',
  },
]

function render(props: Partial<InstanceType<typeof UserSelect>['$props']> = {}) {
  return mount(UserSelect, {
    props: { modelValue: null, options, ...props },
  })
}

async function open(wrapper: ReturnType<typeof render>) {
  await wrapper.find('button').trigger('click')
  return wrapper
}

describe('UserSelect', () => {
  it('shows the placeholder until something is chosen', () => {
    const wrapper = render({ placeholder: 'Add a member…' })

    expect(wrapper.text()).toContain('Add a member…')
    expect(wrapper.find('[role="listbox"]').exists()).toBe(false)
  })

  it('lists everyone once opened', async () => {
    const wrapper = await open(render())

    expect(wrapper.findAll('[role="option"]')).toHaveLength(4)
    expect(wrapper.text()).toContain('Ada Lovelace')
    expect(wrapper.text()).toContain('ada@aictiq.local')
  })

  it('badges an agent so it is never mistaken for a colleague', async () => {
    const wrapper = await open(render())

    expect(wrapper.find('[aria-label="Nightly Triage is an agent"]').exists()).toBe(true)
    expect(wrapper.find('[aria-label="Ada Lovelace is an agent"]').exists()).toBe(false)
  })

  it('filters by name', async () => {
    const wrapper = await open(render())
    await wrapper.find('input').setValue('grace')

    const rows = wrapper.findAll('[role="option"]')
    expect(rows).toHaveLength(1)
    expect(rows[0]!.text()).toContain('Grace Hopper')
  })

  it('filters by address, because that is often all the searcher has', async () => {
    const wrapper = await open(render())
    await wrapper.find('input').setValue('agent+triage')

    const rows = wrapper.findAll('[role="option"]')
    expect(rows).toHaveLength(1)
    expect(rows[0]!.text()).toContain('Nightly Triage')
  })

  it('says so rather than showing an empty box when nothing matches', async () => {
    const wrapper = await open(render({ emptyText: 'Nobody by that name.' }))
    await wrapper.find('input').setValue('zzz')

    expect(wrapper.findAll('[role="option"]')).toHaveLength(0)
    expect(wrapper.text()).toContain('Nobody by that name.')
  })

  it('emits the id and closes when one person is picked', async () => {
    const wrapper = await open(render())
    await wrapper.findAll('[role="option"]')[1]!.trigger('click')

    expect(wrapper.emitted('update:modelValue')).toEqual([['u2']])
    expect(wrapper.find('[role="listbox"]').exists()).toBe(false)
  })

  it('stays open and emits the whole list when picking several', async () => {
    const wrapper = await open(render({ multiple: true, modelValue: ['u1'] }))
    await wrapper.findAll('[role="option"]')[1]!.trigger('click')

    expect(wrapper.emitted('update:modelValue')).toEqual([[['u1', 'u2']]])
    expect(wrapper.find('[role="listbox"]').exists()).toBe(true)
  })

  it('removes someone already chosen rather than adding them twice', async () => {
    const wrapper = await open(render({ multiple: true, modelValue: ['u1', 'u2'] }))
    await wrapper.findAll('[role="option"]')[0]!.trigger('click')

    expect(wrapper.emitted('update:modelValue')).toEqual([[['u2']]])
  })

  it('groups agents after people, so handing work over is a deliberate choice', async () => {
    const wrapper = await open(render())
    const rows = wrapper.findAll('[role="option"]')

    // Ada, Grace, Alan (a person, if a disabled one), then the agent.
    expect(rows.map((row) => row.text().replace(/\s+/g, ' '))[3]).toContain('Nightly Triage')
    expect(wrapper.text()).toContain('People')
    expect(wrapper.text()).toContain('Agents')
  })

  it('shows why an ineligible person is listed, and refuses to pick them', async () => {
    const wrapper = await open(render())
    const row = wrapper.findAll('[role="option"]')[2]!

    expect(row.text()).toContain('already a member')
    expect(row.attributes('disabled')).toBeDefined()

    await row.trigger('click')
    expect(wrapper.emitted('update:modelValue')).toBeUndefined()
  })

  it('never lands the keyboard on a row that cannot be picked', async () => {
    const wrapper = await open(render())
    const search = wrapper.find('input')

    // Ada, Grace, then Alan - who is disabled, so the third press skips him and lands on
    // the agent at the end of the list.
    await search.trigger('keydown', { key: 'ArrowDown' })
    await search.trigger('keydown', { key: 'ArrowDown' })
    await search.trigger('keydown', { key: 'Enter' })

    expect(wrapper.emitted('update:modelValue')).toEqual([['u3']])
  })

  it('closes on Escape without choosing anything', async () => {
    const wrapper = await open(render())
    await wrapper.find('input').trigger('keydown', { key: 'Escape' })

    expect(wrapper.find('[role="listbox"]').exists()).toBe(false)
    expect(wrapper.emitted('update:modelValue')).toBeUndefined()
  })

  it('clears to null when single and to an empty list when multiple', async () => {
    const single = render({ modelValue: 'u1' })
    await single.find('[aria-label="Clear selection"]').trigger('click')
    expect(single.emitted('update:modelValue')).toEqual([[null]])

    const many = render({ multiple: true, modelValue: ['u1', 'u2'] })
    await many.find('[aria-label="Clear selection"]').trigger('click')
    expect(many.emitted('update:modelValue')).toEqual([[[]]])
  })

  it('does not open when disabled', async () => {
    const wrapper = await open(render({ disabled: true }))

    expect(wrapper.find('[role="listbox"]').exists()).toBe(false)
  })
})
