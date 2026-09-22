import type { Directive } from 'vue'

/** `v-near-end="fn"`: calls `fn` when the element scrolls within 400px of the viewport - the
 * end-of-list sentinel for lists the server pages (board columns, backlog sections). */
export const vNearEnd: Directive<HTMLElement & { _nearEnd?: IntersectionObserver }, () => void> = {
  mounted(el, binding) {
    el._nearEnd = new IntersectionObserver(
      (entries) => {
        if (entries.some((entry) => entry.isIntersecting)) binding.value()
      },
      { rootMargin: '400px 0px' },
    )
    el._nearEnd.observe(el)
  },
  unmounted(el) {
    el._nearEnd?.disconnect()
  },
}
