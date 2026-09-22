import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'

import LabelChip from '@/components/common/LabelChip.vue'

/**
 * The chip is where a label's own colour meets the design tokens: the colour tints a
 * border and lights a dot, but the text never adopts it, so the chip stays legible in
 * either theme whatever a person picked. That trade, the group prefix, and the remove
 * affordance are what is worth asserting here.
 */

describe('LabelChip', () => {
  it('renders the name alone when there is no group', () => {
    const wrapper = mount(LabelChip, { props: { name: 'frontend' } })

    expect(wrapper.text()).toBe('frontend')
  })

  it('reads "group: name", with the group visually subordinate', () => {
    const wrapper = mount(LabelChip, { props: { name: 'frontend', group: 'type' } })

    expect(wrapper.text()).toContain('type:')
    expect(wrapper.text()).toContain('frontend')
    // The group sits on a muted token; the name does not.
    const group = wrapper.find('.text-muted-foreground')
    expect(group.exists()).toBe(true)
    expect(group.text()).toBe('type:')
  })

  it('tints the border and the dot from the label colour, never the text', () => {
    const wrapper = mount(LabelChip, { props: { name: 'frontend', color: '#eda45c' } })

    expect(wrapper.attributes('style')).toContain('border-color: #eda45c66')
    const dot = wrapper.find('span[aria-hidden="true"]')
    expect(dot.attributes('style')).toContain('background-color: #eda45c')
    // The chip's own text colour is a token, never bound to the arbitrary colour.
    expect(wrapper.attributes('style')).not.toMatch(/(?<!border-)color: #eda45c/)
  })

  it('falls back to a design token when the label has no colour', () => {
    const wrapper = mount(LabelChip, { props: { name: 'good first issue', color: null } })

    expect(wrapper.classes()).toContain('border-border')
    expect(wrapper.find('span[aria-hidden="true"]').classes()).toContain('bg-muted-foreground/50')
  })

  it('shows no remove control unless asked for one', () => {
    const wrapper = mount(LabelChip, { props: { name: 'frontend' } })

    expect(wrapper.find('button').exists()).toBe(false)
  })

  it('emits remove and does not bubble the click further', async () => {
    const wrapper = mount(LabelChip, { props: { name: 'frontend', removable: true } })

    await wrapper.find('button').trigger('click')

    expect(wrapper.emitted('remove')).toHaveLength(1)
  })
})
