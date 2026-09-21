import { mount } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, h } from 'vue'

import { __resetShortcutsForTests, formatShortcut, useShortcut } from '@/composables/useShortcuts'

/**
 * The two ways a shortcut system goes wrong in practice: it fires while someone is typing,
 * and a half-entered sequence swallows the next keystroke. Both are covered here.
 */

function harness(binding: string, handler: () => void, options = {}) {
  return defineComponent({
    setup() {
      useShortcut(binding, handler, options)
      return () => h('div', [h('input', { id: 'field' })])
    },
  })
}

function press(key: string, init: Partial<KeyboardEventInit> = {}, target?: Element) {
  const event = new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true, ...init })
  ;(target ?? document.body).dispatchEvent(event)
  return event
}

beforeEach(() => __resetShortcutsForTests())
afterEach(() => vi.useRealTimers())

describe('useShortcut', () => {
  it('fires on a plain key', () => {
    const handler = vi.fn()
    const wrapper = mount(harness('k', handler), { attachTo: document.body })

    press('k')
    expect(handler).toHaveBeenCalledOnce()
    wrapper.unmount()
  })

  it('fires on a modifier combination', () => {
    const handler = vi.fn()
    const wrapper = mount(harness('mod+k', handler), { attachTo: document.body })

    press('k')
    expect(handler).not.toHaveBeenCalled()

    press('k', { ctrlKey: true, metaKey: true })
    expect(handler).toHaveBeenCalledOnce()
    wrapper.unmount()
  })

  it('does not steal a key while someone is typing', () => {
    const handler = vi.fn()
    const wrapper = mount(harness('g', handler), { attachTo: document.body })

    const input = document.getElementById('field')!
    input.focus()
    press('g', {}, input)

    // Typing "g" in a title must not navigate away.
    expect(handler).not.toHaveBeenCalled()
    wrapper.unmount()
  })

  it('still fires in a field when the binding opts in', () => {
    const handler = vi.fn()
    const wrapper = mount(harness('mod+k', handler, { allowInInput: true }), {
      attachTo: document.body,
    })

    const input = document.getElementById('field')!
    press('k', { ctrlKey: true, metaKey: true }, input)

    expect(handler).toHaveBeenCalledOnce()
    wrapper.unmount()
  })

  it('fires a two-key sequence only in order', () => {
    const handler = vi.fn()
    const wrapper = mount(harness('g then i', handler), { attachTo: document.body })

    press('i')
    expect(handler).not.toHaveBeenCalled()

    press('g')
    expect(handler).not.toHaveBeenCalled()

    press('i')
    expect(handler).toHaveBeenCalledOnce()
    wrapper.unmount()
  })

  it('abandons a sequence when a different key intervenes', () => {
    const handler = vi.fn()
    const wrapper = mount(harness('g then i', handler), { attachTo: document.body })

    press('g')
    press('x')
    press('i')

    // A stale prefix must not silently swallow a later keystroke.
    expect(handler).not.toHaveBeenCalled()
    wrapper.unmount()
  })

  it('abandons a sequence after the timeout', () => {
    vi.useFakeTimers()
    const handler = vi.fn()
    const wrapper = mount(harness('g then i', handler), { attachTo: document.body })

    press('g')
    vi.advanceTimersByTime(2000)
    press('i')

    expect(handler).not.toHaveBeenCalled()
    wrapper.unmount()
  })

  it('ignores a modifier pressed on its own so a sequence survives it', () => {
    const handler = vi.fn()
    const wrapper = mount(harness('g then i', handler), { attachTo: document.body })

    press('g')
    press('Shift', { shiftKey: true })
    press('i')

    expect(handler).toHaveBeenCalledOnce()
    wrapper.unmount()
  })

  it('does not fire while `when` is false', () => {
    const handler = vi.fn()
    const wrapper = mount(harness('k', handler, { when: () => false }), {
      attachTo: document.body,
    })

    press('k')
    expect(handler).not.toHaveBeenCalled()
    wrapper.unmount()
  })

  it('stops firing once the component is gone', () => {
    const handler = vi.fn()
    const wrapper = mount(harness('k', handler), { attachTo: document.body })
    wrapper.unmount()

    press('k')
    expect(handler).not.toHaveBeenCalled()
  })

  it('prevents the default so the browser does not also act on it', () => {
    const wrapper = mount(
      harness('mod+k', () => {}, { allowInInput: true }),
      {
        attachTo: document.body,
      },
    )

    const event = press('k', { ctrlKey: true, metaKey: true })
    expect(event.defaultPrevented).toBe(true)
    wrapper.unmount()
  })
})

describe('formatShortcut', () => {
  it('renders a combination and a sequence readably', () => {
    // The exact glyphs depend on the platform; what must hold is that `mod` is replaced
    // and a sequence keeps its two parts.
    expect(formatShortcut('mod+k')).not.toContain('mod')
    expect(formatShortcut('g then i')).toBe('g i')
  })
})
