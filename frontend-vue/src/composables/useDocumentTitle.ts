import { computed, onBeforeUnmount, ref, toValue, watchEffect, type MaybeRefOrGetter } from 'vue'
import { useRoute } from 'vue-router'

import { useOrganizationsStore } from '@/stores/organizations'
import { useProjectsStore } from '@/stores/projects'

export const appName = 'Aictiq'

/**
 * The page, the project and the organization the current route is about. The header's
 * breadcrumbs and the browser tab read the same answer, so the two cannot disagree about
 * where someone is.
 */
export function useRouteTitles() {
  const route = useRoute()
  const organizations = useOrganizationsStore()
  const projects = useProjectsStore()

  // The current route's title is both more useful and more compact than an internal route
  // name such as `org-settings-members`, especially in the phone header.
  const pageTitle = computed(() => {
    if (typeof route.meta.title === 'string') return route.meta.title
    if (route.name === 'home') return 'My work'
    return String(route.name ?? '')
      .replace(/^(organization|org|project|team)-/, '')
      .replace(/-settings$/, '')
      .replaceAll('-', ' ')
      .replace(/^./, (letter) => letter.toUpperCase())
  })

  const projectTitle = computed(() => {
    // Scoped pages follow the URL, even while the sidebar's selection is changing.
    // The unscoped board reads its project from the store, just like its content does.
    const projectKey =
      typeof route.params.projectKey === 'string'
        ? route.params.projectKey
        : route.name === 'board'
          ? projects.currentKey
          : null
    if (!projectKey) return null

    // Project keys are only unique within an organization. Never use another tenant's name.
    if (route.params.slug && route.params.slug !== organizations.currentSlug) return projectKey
    return projects.projects.find((project) => project.key === projectKey)?.name ?? projectKey
  })

  // Only for a URL that names the organization, and only when it is the one the shell
  // has selected - a slug the caller is not in has no name they are allowed to see.
  const organizationTitle = computed(() => {
    const slug = typeof route.params.slug === 'string' ? route.params.slug : null
    if (!slug || slug !== organizations.currentSlug) return slug
    return organizations.current?.name ?? slug
  })

  return { pageTitle, projectTitle, organizationTitle }
}

/**
 * Titles a mounted component claims for the tab, newest last. A stack rather than one
 * value so an item opened in a modal over another item's page hands the tab back to the
 * page underneath when it closes.
 */
const claims = ref<{ id: symbol; title: string | null }[]>([])

/**
 * Names the tab after something the route alone cannot - an item's title, say - for as
 * long as the calling component is mounted. `null` defers to the route's own title.
 */
export function usePageTitle(title: MaybeRefOrGetter<string | null | undefined>) {
  const id = Symbol('page-title')
  claims.value.push({ id, title: null })
  watchEffect(() => {
    const claim = claims.value.find((entry) => entry.id === id)
    if (claim) claim.title = toValue(title) || null
  })
  onBeforeUnmount(() => {
    claims.value = claims.value.filter((entry) => entry.id !== id)
  })
}

/** Joins the non-empty parts most specific first, so a narrow tab still shows the page. */
export function formatDocumentTitle(parts: (string | null | undefined)[]): string {
  const seen = new Set<string>()
  const unique = [...parts, appName].filter((part): part is string => {
    if (!part || seen.has(part)) return false
    seen.add(part)
    return true
  })
  return unique.join(' · ')
}

/** Keeps `document.title` in step with the route. Called once, from the app root. */
export function useDocumentTitle() {
  const route = useRoute()
  const { pageTitle, projectTitle, organizationTitle } = useRouteTitles()

  const title = computed(() => {
    const claimed = [...claims.value].reverse().find((entry) => entry.title)?.title
    if (claimed) return formatDocumentTitle([claimed])
    // An item key already names its project.
    if (typeof route.params.itemKey === 'string') return formatDocumentTitle([route.params.itemKey])
    return formatDocumentTitle([pageTitle.value, projectTitle.value ?? organizationTitle.value])
  })

  watchEffect(() => {
    document.title = title.value
  })

  return title
}
