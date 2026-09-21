import { computed } from 'vue'
import { useRoute, useRouter } from 'vue-router'

/**
 * Opens an item over whatever list the person is looking at, as `?item=KEY` on the current
 * route. The list stays mounted underneath — its scroll, filters and loaded pages survive —
 * and the URL is still something one can paste: it reopens the same list with the same item.
 */
export function useItemModal() {
  const route = useRoute()
  const router = useRouter()

  const openKey = computed(() => (typeof route.query.item === 'string' ? route.query.item : null))

  // Pushed rather than replaced so the browser's Back closes the dialog.
  function open(key: string) {
    void router.push({ query: { ...route.query, item: key } })
  }

  function close() {
    // Going back when the previous entry is this list without an item keeps the history
    // free of a dead "list?item=X" step; a pasted link has no such entry and is replaced.
    const back = (window.history.state as { back?: unknown } | null)?.back
    if (typeof back === 'string') {
      const previous = router.resolve(back)
      if (previous.path === route.path && previous.query.item === undefined) {
        router.back()
        return
      }
    }
    const query = { ...route.query }
    delete query.item
    void router.replace({ query })
  }

  return { openKey, open, close }
}

/** An item key is its project key, a hyphen, and a number; the project key may contain hyphens. */
export function projectKeyOf(itemKey: string) {
  const index = itemKey.lastIndexOf('-')
  return index < 1 ? '' : itemKey.slice(0, index).toUpperCase()
}
