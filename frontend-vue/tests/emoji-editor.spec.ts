import { EditorContent, type Editor } from '@tiptap/vue-3'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { nextTick } from 'vue'

import EmojiPicker from '@/components/common/EmojiPicker.vue'
import { emojis } from '@/components/common/emojis'
import MarkdownEditor from '@/components/common/MarkdownEditor.vue'
import MarkdownTextarea from '@/components/common/MarkdownTextarea.vue'
import { renderMarkdown } from '@/lib/markdown'

vi.mock('@/composables/useToast', () => ({ useToast: () => ({ error: vi.fn() }) }))

const wrappers: VueWrapper[] = []
afterEach(() => {
  wrappers.splice(0).forEach((wrapper) => wrapper.unmount())
  vi.restoreAllMocks()
  document.body.innerHTML = ''
})

async function rich(markdown: string, disabled = false) {
  const wrapper = mount(MarkdownEditor, {
    attachTo: document.body,
    props: { modelValue: markdown, disabled },
  })
  wrappers.push(wrapper)
  await nextTick()
  const editor = wrapper.findComponent(EditorContent).props('editor') as Editor
  return { wrapper, editor, picker: wrapper.findComponent(EmojiPicker) }
}

async function choose(picker: VueWrapper, emoji: string) {
  picker.vm.$emit('open')
  picker.vm.$emit('select', emoji)
  await nextTick()
}

describe('emoji insertion in the shared Markdown editor', () => {
  it('replaces the saved range, preserves formatting, focuses and undoes independently', async () => {
    const { wrapper, editor, picker } = await rich('Before **replace** after')
    editor.commands.setTextSelection({ from: 8, to: 15 })
    await choose(picker, '🚀')
    expect(editor.getMarkdown()).toBe('Before **🚀** after')
    expect(editor.state.selection.from).toBe(10)
    expect(wrapper.emitted('update:modelValue')?.at(-1)).toEqual(['Before **🚀** after'])
    await new Promise((resolve) => requestAnimationFrame(resolve))
    expect(editor.isFocused).toBe(true)
    editor.commands.undo()
    expect(editor.getMarkdown()).toBe('Before **replace** after')
  })

  it('keeps prior typing when undoing an insertion and can continue typing', async () => {
    const { editor, picker } = await rich('Start ')
    editor.commands.setTextSelection(7)
    editor.commands.insertContent('work')
    await choose(picker, '✅')
    expect(editor.getText()).toBe('Start work✅')
    editor.commands.undo()
    expect(editor.getText()).toBe('Start work')
    await choose(picker, '☐')
    editor.commands.insertContent(' next')
    expect(editor.getText()).toBe('Start work☐ next')
  })

  it('round trips every emoji as text, while task list checkboxes stay interactive', async () => {
    const text = emojis.map((emoji) => emoji.text).join(' ')
    const { wrapper, editor } = await rich(`${text}\n\n- [ ] open\n- [x] done`)
    const saved = editor.getMarkdown()
    const reopened = await rich(saved)
    expect(reopened.editor.getText()).toContain(text)
    expect(renderMarkdown(saved)).toContain(text)
    expect(wrapper.findAll('input[type="checkbox"]')).toHaveLength(2)
    expect(wrapper.get('input[type="checkbox"]').attributes('disabled')).toBeUndefined()
    expect(renderMarkdown(text)).not.toContain('<input')
  })

  it('rejects insertion when disabled, including a picker that was already opened', async () => {
    const { wrapper, editor, picker } = await rich('Keep')
    picker.vm.$emit('open')
    await wrapper.setProps({ disabled: true })
    picker.vm.$emit('select', '🚀')
    expect(editor.getText()).toBe('Keep')
    expect(wrapper.get('[aria-label="Insert emoji"]').attributes('disabled')).toBeDefined()
  })
})

describe('the shared emoji picker', () => {
  it('offers exactly the curated 30 choices and dismisses without editing', async () => {
    const wrapper = mount(EmojiPicker, { attachTo: document.body })
    wrappers.push(wrapper)
    await wrapper.get('button').trigger('click')
    await flushPromises()
    const dialog = document.querySelector('[role="dialog"]')!
    const buttons = [...dialog.querySelectorAll('button')]
    expect(buttons.map((button) => button.textContent?.trim())).toEqual(emojis.map((emoji) => emoji.text))
    expect(buttons).toHaveLength(30)
    expect(new Set(buttons.map((button) => button.getAttribute('aria-label'))).size).toBe(30)
    buttons[0]!.focus()
    buttons[0]!.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true }))
    expect(document.activeElement).toBe(buttons[6])
    buttons[6]!.dispatchEvent(new KeyboardEvent('keydown', { key: 'End', bubbles: true }))
    expect(document.activeElement).toBe(buttons[29])
    document.activeElement!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))
    await flushPromises()
    expect(wrapper.emitted('select')).toBeUndefined()
    expect(wrapper.get('button').attributes('aria-expanded')).toBe('false')
  })
})

describe('raw Markdown textarea emoji insertion', () => {
  it('replaces a saved range without changing raw Markdown syntax', async () => {
    const wrapper = mount(MarkdownTextarea, {
      attachTo: document.body,
      props: {
        modelValue: '**before** replace _after_',
        'onUpdate:modelValue': (value: string) => wrapper.setProps({ modelValue: value }),
      },
    })
    wrappers.push(wrapper)
    const input = wrapper.get('textarea').element
    input.setSelectionRange(11, 18)
    await choose(wrapper.findComponent(EmojiPicker), '☑️')
    expect(input.value).toBe('**before** ☑️ _after_')
    expect(input.selectionStart).toBe(13)
    expect(input.selectionEnd).toBe(13)
    expect(document.activeElement).toBe(input)
  })

  it.each([{ disabled: true }, { readonly: true }])('blocks insertion for %o', async (state) => {
    const wrapper = mount(MarkdownTextarea, { props: { modelValue: 'Keep', ...state } })
    wrappers.push(wrapper)
    await choose(wrapper.findComponent(EmojiPicker), '✅')
    expect(wrapper.emitted('update:modelValue')).toBeUndefined()
    expect(wrapper.get('[aria-label="Insert emoji"]').attributes('disabled')).toBeDefined()
  })
})
