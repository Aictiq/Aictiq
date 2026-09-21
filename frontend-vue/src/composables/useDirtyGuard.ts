import { onBeforeUnmount } from 'vue'
import { onBeforeRouteLeave } from 'vue-router'

/**
 * Stops a half-finished form disappearing under someone who clicked a tab.
 *
 * Two exits have to be covered and only one of them is ours: the router, where a
 * confirm() can cancel the navigation, and the browser's own close/reload, where all a
 * page may do is ask the browser to ask. The message is ignored by every current browser
 * on the second path — `preventDefault()` is the whole API — so it is written for the
 * first.
 *
 * `isDirty` is a getter rather than a ref so a caller can compare a whole form against
 * what it loaded without maintaining a second piece of state that can go stale.
 */
export function useDirtyGuard(
  isDirty: () => boolean,
  message = 'You have unsaved changes. Leave this page and lose them?',
) {
  onBeforeRouteLeave(() => {
    if (!isDirty()) return true
    return window.confirm(message)
  })

  function warn(event: BeforeUnloadEvent) {
    if (!isDirty()) return
    event.preventDefault()
  }

  window.addEventListener('beforeunload', warn)
  onBeforeUnmount(() => window.removeEventListener('beforeunload', warn))
}
