import { useRoute, useRouter } from 'vue-router'

import { useOrganizationsStore } from '@/stores/organizations'

/**
 * Moving to another organization: the selection and the page, together.
 *
 * Every scoped URL names its organization (`/o/{slug}/…`), and the router guard treats
 * that name as the authority — so selecting one organization while standing on another's
 * page shows one organization's work beside the other's sidebar, and the next navigation
 * quietly switches the selection back. The people who notice are the ones whose standing
 * differs between the two: an Owner in their own organization and a stakeholder in a
 * customer's would be looking at controls the chip above them does not grant.
 *
 * `/` is the destination because it is the one page every organization has — My work, in
 * whichever one is now selected. A project or a settings tab may simply not exist, or not
 * be visible, on the other side.
 */
export function useOrganizationSwitch() {
  const organizations = useOrganizationsStore()
  const router = useRouter()
  const route = useRoute()

  return function switchTo(slug: string) {
    const leaving = typeof route.params.slug === 'string' && route.params.slug !== slug
    organizations.select(slug)
    if (leaving) void router.push('/')
  }
}
