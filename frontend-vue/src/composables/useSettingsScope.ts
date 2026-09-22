import type { InjectionKey, Ref } from 'vue'
import { inject } from 'vue'

import type { Organization } from '@/api/organizations'
import type { Project } from '@/api/projects'

/**
 * The settings hub loads the record its tabs all act on **once**, in the layout, and
 * hands it down. Every tab under `/o/{slug}/settings` is looking at the same
 * organization, and every tab under `.../p/{key}/settings` at the same project - a tab
 * that fetched it again would double the round trips and, worse, could render a different
 * `version` than the tab beside it, which is the one field a write must echo exactly.
 *
 * `set` is how a tab that just saved publishes the record the API handed back (a new
 * `version` among it); `reload` is what a 409 does instead, because after a conflict the
 * only trustworthy state is the server's.
 */

export interface SettingsScope<T> {
  /** Null while loading, and after a load that failed or found nothing. */
  record: Ref<T | null>
  loading: Ref<boolean>
  /** The caller could not see it: the layout renders "not found", never a 403. */
  notFound: Ref<boolean>
  reload: () => Promise<void>
  set: (record: T) => void
}

export interface OrgScope extends SettingsScope<Organization> {
  slug: Ref<string>
}

export interface ProjectScope extends SettingsScope<Project> {
  slug: Ref<string>
  projectKey: Ref<string>
}

export const orgScopeKey = Symbol('aictiq.org-scope') as InjectionKey<OrgScope>
export const projectScopeKey = Symbol('aictiq.project-scope') as InjectionKey<ProjectScope>

/**
 * Throws rather than returning null: a settings tab rendered outside its layout is a
 * routing mistake, and failing at mount says so far more clearly than an empty page.
 */
export function useOrgScope(): OrgScope {
  const scope = inject(orgScopeKey, null)
  if (!scope) throw new Error('useOrgScope() must be used inside the organization settings layout.')
  return scope
}

export function useProjectScope(): ProjectScope {
  const scope = inject(projectScopeKey, null)
  if (!scope) throw new Error('useProjectScope() must be used inside the project settings layout.')
  return scope
}
