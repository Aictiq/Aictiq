import { useRouter } from 'vue-router'

import { useOnboardingStore } from '@/stores/onboarding'
import { useOrganizationsStore } from '@/stores/organizations'
import { useSessionStore } from '@/stores/session'

/**
 * Signing out is three steps, and every surface that offers it must do all three: end the
 * server session, forget which organization this browser was in (the next person on it is
 * not them), and leave the authenticated shell. One function so the sidebar menu and the
 * command palette cannot drift apart - a button that logged out without clearing the
 * remembered slug would hand the next sign-in someone else's workspace.
 */
export function useLogout(): () => Promise<void> {
  const session = useSessionStore()
  const organizations = useOrganizationsStore()
  const onboarding = useOnboardingStore()
  const router = useRouter()

  return async () => {
    // A failed request is not a failed sign-out: the store clears itself either way, and
    // leaving someone in an authenticated shell because the network blinked is worse than
    // a cookie the server still thinks is live. So the error is swallowed here rather than
    // surfaced - there is nothing the person could do with it.
    await session.logout().catch(() => undefined)
    organizations.clear()
    onboarding.clear()
    await router.replace('/login')
  }
}
