import { nextTick, onBeforeUnmount, watch, type Ref } from 'vue'

/**
 * Keeps keyboard focus inside a non-Reka modal and restores it to the control that
 * opened the modal. Most dialogs use Reka and get this for free; the command palette,
 * shortcut reference and board editor are deliberately lightweight custom overlays.
 */
const focusable = [
  'a[href]',
  'button:not([disabled])',
  'input:not([disabled])',
  'select:not([disabled])',
  'textarea:not([disabled])',
  '[tabindex]:not([tabindex="-1"])',
].join(',')

function focusableChildren(root: HTMLElement): HTMLElement[] {
  return [...root.querySelectorAll<HTMLElement>(focusable)].filter((element) =>
    !element.hasAttribute('hidden') && getComputedStyle(element).visibility !== 'hidden',
  )
}

export function useFocusTrap(root: Ref<HTMLElement | null>, active: Ref<boolean>) {
  let previousFocus: HTMLElement | null = null

  function onKeydown(event: KeyboardEvent) {
    if (event.key !== 'Tab' || !root.value) return

    const elements = focusableChildren(root.value)
    if (elements.length === 0) {
      event.preventDefault()
      root.value.focus()
      return
    }

    const first = elements[0]!
    const last = elements[elements.length - 1]!
    if (event.shiftKey && document.activeElement === first) {
      event.preventDefault()
      last.focus()
    } else if (!event.shiftKey && document.activeElement === last) {
      event.preventDefault()
      first.focus()
    }
  }

  watch(active, async (open, wasOpen) => {
    if (open) {
      previousFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null
      await nextTick()
      const target = root.value?.querySelector<HTMLElement>('[data-autofocus]')
        ?? (root.value ? focusableChildren(root.value)[0] : undefined)
        ?? root.value
      target?.focus()
      return
    }

    if (wasOpen) {
      await nextTick()
      previousFocus?.focus()
      previousFocus = null
    }
  })

  watch(root, (element, previous) => {
    previous?.removeEventListener('keydown', onKeydown)
    if (element && active.value) element.addEventListener('keydown', onKeydown)
  })

  // A v-if root can be created after `active` flips. Keep the listener lifecycle tied
  // to the active state as well as the element itself.
  watch(active, (open) => {
    if (open) root.value?.addEventListener('keydown', onKeydown)
    else root.value?.removeEventListener('keydown', onKeydown)
  })

  onBeforeUnmount(() => root.value?.removeEventListener('keydown', onKeydown))
}
