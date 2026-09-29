import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'

import { PasswordInput } from '@/components/ui/input'

/**
 * The eye exists so a long password can be checked before it is submitted, and the risk
 * it trades against is someone reading the screen. So what matters is that the field is
 * masked until asked, that it goes back, that the button never submits the form it sits
 * in, and that the attributes the browser's password manager reads - id, autocomplete -
 * land on the real input rather than on the wrapper.
 */

function toggle(wrapper: ReturnType<typeof mount>) {
  return wrapper.get('button')
}

describe('PasswordInput', () => {
  it('starts masked', () => {
    const wrapper = mount(PasswordInput)

    expect(wrapper.get('input').attributes('type')).toBe('password')
    expect(toggle(wrapper).attributes('aria-label')).toBe('Show password')
  })

  it('reveals the password and masks it again', async () => {
    const wrapper = mount(PasswordInput)

    await toggle(wrapper).trigger('click')

    expect(wrapper.get('input').attributes('type')).toBe('text')
    expect(toggle(wrapper).attributes('aria-label')).toBe('Hide password')

    await toggle(wrapper).trigger('click')

    expect(wrapper.get('input').attributes('type')).toBe('password')
    expect(toggle(wrapper).attributes('aria-label')).toBe('Show password')
  })

  it('is a plain button, so clicking the eye does not submit the form', () => {
    const wrapper = mount(PasswordInput)

    expect(toggle(wrapper).attributes('type')).toBe('button')
  })

  it('keeps the typed value when the mask comes off', async () => {
    const wrapper = mount(PasswordInput, { props: { modelValue: '' } })

    await wrapper.get('input').setValue('correct horse battery staple')
    await toggle(wrapper).trigger('click')

    expect(wrapper.emitted('update:modelValue')?.at(-1)).toEqual(['correct horse battery staple'])
    expect(wrapper.get('input').element.value).toBe('correct horse battery staple')
  })

  it('passes id, autocomplete and validity state to the input, not the wrapper', () => {
    const wrapper = mount(PasswordInput, {
      attrs: {
        id: 'password',
        autocomplete: 'current-password',
        required: true,
        'aria-invalid': true,
      },
    })

    const input = wrapper.get('input')
    expect(input.attributes('id')).toBe('password')
    expect(input.attributes('autocomplete')).toBe('current-password')
    expect(input.attributes('required')).toBeDefined()
    expect(input.attributes('aria-invalid')).toBe('true')
    expect(wrapper.element.getAttribute('id')).toBeNull()
  })

  it('leaves room for the eye without dropping the caller’s own classes', () => {
    const wrapper = mount(PasswordInput, { props: { class: 'w-40' } })

    expect(wrapper.get('input').classes()).toContain('pr-8')
    expect(wrapper.get('input').classes()).toContain('w-40')
  })
})
