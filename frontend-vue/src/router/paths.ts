/**
 * The settings URLs, built in one place.
 *
 * They name the organization and the project rather than acting on whatever happens to be
 * selected, because settings are the pages people link each other to — "look at the role
 * you gave me" is useless if it opens in the reader's own organization. Every one of these
 * paths is safe to paste: the API answers 404 to a slug or key the caller cannot see, so a
 * link that crosses a boundary fails closed rather than leaking a name.
 */

export type OrgSettingsTab =
  | 'general'
  | 'members'
  | 'agents'
  | 'invitations'
  | 'billing'
  | 'integrations'
  | 'audit'

export type ProjectSettingsTab =
  | 'general'
  | 'factory'
  | 'members'
  | 'teams'
  | 'workflow'
  | 'labels'
  | 'templates'
  | 'integrations'

export const orgSettingsPath = (slug: string, tab: OrgSettingsTab = 'general') =>
  `/o/${slug}/settings/${tab}`

export const projectSettingsPath = (
  slug: string,
  projectKey: string,
  tab: ProjectSettingsTab = 'general',
) => `/o/${slug}/p/${projectKey}/settings/${tab}`

export const teamSettingsPath = (slug: string, projectKey: string, teamId: string) =>
  `/o/${slug}/p/${projectKey}/settings/teams/${teamId}`

/** The AI software factory's tabs (phase 10). */
export type FactoryTab = 'runs' | 'runners' | 'playbooks' | 'rules'

/**
 * The Factory area: an organization's runners, runs and playbooks. Named by slug like the
 * settings hubs, because "look at this run" has to open in the organization it happened in.
 */
export const factoryPath = (slug: string, tab: FactoryTab = 'runners') => `/o/${slug}/factory/${tab}`

export function factoryLinks(slug: string): SettingsLink[] {
  return [
    { to: factoryPath(slug, 'runs'), label: 'Runs' },
    { to: factoryPath(slug, 'rules'), label: 'Rules' },
    { to: factoryPath(slug, 'runners'), label: 'Runners', tour: 'factory-runners' },
    { to: factoryPath(slug, 'playbooks'), label: 'Playbooks' },
  ]
}

/** One run's page — where "open log" goes and what a run's link pasted in chat opens. */
export const factoryRunPath = (slug: string, runId: string) => `/o/${slug}/factory/runs/${runId}`

/** One rule's page — its own record and its last 50 firings. */
export const factoryRulePath = (slug: string, ruleId: string) => `/o/${slug}/factory/rules/${ruleId}`

/** The normal working destinations for the project currently named by the rail. */
export const projectDashboardPath = (slug: string, projectKey: string) =>
  `/o/${slug}/p/${projectKey}/dashboard`

export const projectItemsPath = (slug: string, projectKey: string) =>
  `/o/${slug}/p/${projectKey}/items`

export const wikiPath = (slug: string, projectKey: string, pageId?: string, pageSlug?: string) =>
  `/o/${slug}/p/${projectKey}/wiki${pageId ? `/${pageId}/${pageSlug ?? ''}` : ''}`

/** One tab of a settings hub. `to` is absolute, so a link never depends on where it is rendered. */
export interface SettingsLink {
  to: string
  label: string
  /** Optional product-tour anchor (`data-tour`) carried by exactly this tab's link. */
  tour?: string
}

export function orgSettingsLinks(slug: string): SettingsLink[] {
  return [
    { to: orgSettingsPath(slug, 'general'), label: 'General' },
    { to: orgSettingsPath(slug, 'members'), label: 'Members' },
    { to: orgSettingsPath(slug, 'invitations'), label: 'Invitations' },
    { to: orgSettingsPath(slug, 'agents'), label: 'Agents' },
    { to: orgSettingsPath(slug, 'billing'), label: 'Billing' },
    { to: orgSettingsPath(slug, 'integrations'), label: 'Integrations' },
    { to: orgSettingsPath(slug, 'audit'), label: 'Audit log' },
  ]
}

export function projectSettingsLinks(slug: string, projectKey: string): SettingsLink[] {
  return [
    { to: projectSettingsPath(slug, projectKey, 'general'), label: 'General' },
    { to: projectSettingsPath(slug, projectKey, 'factory'), label: 'Factory' },
    { to: projectSettingsPath(slug, projectKey, 'members'), label: 'Members' },
    { to: projectSettingsPath(slug, projectKey, 'teams'), label: 'Teams' },
    { to: projectSettingsPath(slug, projectKey, 'workflow'), label: 'Workflow' },
    { to: projectSettingsPath(slug, projectKey, 'labels'), label: 'Labels' },
    { to: projectSettingsPath(slug, projectKey, 'templates'), label: 'Templates' },
    {
      to: projectSettingsPath(slug, projectKey, 'integrations'),
      label: 'Integrations',
    },
  ]
}

/** Personal settings are not organization-scoped: they follow the account, not the tenant. */
export const personalSettingsLinks: SettingsLink[] = [
  { to: '/settings/profile', label: 'Profile' },
  { to: '/settings/security', label: 'Security' },
  { to: '/settings/tokens', label: 'Access tokens' },
  { to: '/settings/notifications', label: 'Notifications' },
]
