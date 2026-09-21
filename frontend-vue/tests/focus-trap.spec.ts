import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import { defineComponent, nextTick, ref } from 'vue'

import { useFocusTrap } from '@/composables/useFocusTrap'

const Harness = defineComponent({
  setup() {
    const open = ref(false)
    const dialog = ref<HTMLElement | null>(null)
    useFocusTrap(dialog, open)
    return { open, dialog }
  },
  template: `
    <button id="trigger" @click="open = true">Open</button>
    <section v-if="open" ref="dialog" role="dialog" tabindex="-1">
      <button id="first" data-autofocus>First</button>
      <button id="last">Last</button>
      <button id="close" @click="open = false">Close</button>
    </section>
  `,
})

describe('useFocusTrap', () => {
  it('focuses the marked control, traps Tab, and returns focus to its opener', async () => {
    const wrapper = mount(Harness, { attachTo: document.body })
    const trigger = wrapper.get('#trigger')
    ;(trigger.element as HTMLButtonElement).focus()

    await trigger.trigger('click')
    await nextTick()
    await nextTick()
    expect(document.activeElement).toBe(document.getElementById('first'))

    const close = document.getElementById('close') as HTMLButtonElement
    close.focus()
    close.dispatchEvent(new KeyboardEvent('keydown', { key: 'Tab', bubbles: true, cancelable: true }))
    expect(document.activeElement).toBe(document.getElementById('first'))

    await wrapper.get('#close').trigger('click')
    await nextTick()
    expect(document.activeElement).toBe(trigger.element)
    wrapper.unmount()
  })
})
