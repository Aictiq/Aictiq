import type { PlanLimits } from '@/api/billing'
import { apiFetch } from '@/utils/api'

/**
 * The organization endpoints. Enums cross the wire as camelCase names, not numbers - the
 * same API serves the CLI and the MCP server, where `"role": 0` is not something an agent
 * or a person reading a log can act on.
 */

export type OrgRole = 'owner' | 'admin' | 'member' | 'guest'

export type WeekStart =
  | 'sunday'
  | 'monday'
  | 'tuesday'
  | 'wednesday'
  | 'thursday'
  | 'friday'
  | 'saturday'

/** One row of the switcher: what it takes to name an organization and say what I am in it. */
export interface OrganizationSummary {
  id: string
  slug: string
  name: string
  role: OrgRole
  /**
   * Whether *I* may start and watch AI runs here. False for a stakeholder and for
   * every guest. The factory is hidden on this; the API refuses regardless.
   */
  canOperateFactory: boolean
}

export interface Organization extends OrganizationSummary {
  plan: string
  /** IANA identifier. Decides when "today" ends for due dates and sprint boundaries. */
  timeZone: string
  weekStart: WeekStart
  /** Whether ordinary members may create projects, or only admins and owners. */
  membersCanCreateProjects: boolean
  createdAt: string
  /** xmin. Echo it back on every write or the API answers 409. */
  version: number
}

export interface CreateOrganizationBody {
  name: string
  /** Derived from the name when omitted. Permanent once set. */
  slug?: string
  timeZone?: string
  weekStart?: WeekStart
}

/** The host may be invitation-only; it never exposes the seeded organization to non-members. */
export interface OrganizationCreationPolicy {
  canCreateOrganization: boolean
}

export interface UpdateOrganizationBody {
  name?: string
  timeZone?: string
  weekStart?: WeekStart
  membersCanCreateProjects?: boolean
  version: number
}

/** Ranked, so a permission check is a comparison rather than a list of role names. */
const rank: Record<OrgRole, number> = { owner: 0, admin: 1, member: 2, guest: 3 }

export function hasOrgRole(actual: OrgRole | undefined, required: OrgRole): boolean {
  return actual !== undefined && rank[actual] <= rank[required]
}

export const listOrganizations = () => apiFetch<OrganizationSummary[]>('/orgs')

export const getOrganizationCreationPolicy = () =>
  apiFetch<OrganizationCreationPolicy>('/orgs/creation-policy')

export const createOrganization = (body: CreateOrganizationBody) =>
  apiFetch<Organization>('/orgs', { method: 'POST', body })

export const getOrganization = (slug: string) => apiFetch<Organization>(`/orgs/${slug}`)

export const updateOrganization = (slug: string, body: UpdateOrganizationBody) =>
  apiFetch<Organization>(`/orgs/${slug}`, { method: 'PATCH', body })

/**
 * What the organization uses against what its plan allows - the numbers the Plan page
 * draws. The limits are the plan's own `PlanLimits`, not a second shape: the two drifted
 * once already, and a limit this type forgets is a limit the page silently stops showing.
 */
export interface BillingSummary {
  mode: 'self_hosted' | 'saas'
  plan: string
  /** Present while the organization has a hosted evaluation entitlement. */
  evaluation: { startedAt: string; endsAt: string; expired: boolean } | null
  limits: PlanLimits
  usage: { humans: number; agents: number; projects: number; storageBytes: number }
}

export const getBillingSummary = (slug: string) => apiFetch<BillingSummary>(`/orgs/${slug}/billing`)

/**
 * The name is typed out to confirm. It travels in the body rather than the query string:
 * a query parameter would put it in every access log between the browser and the API.
 */
export const deleteOrganization = (slug: string, name: string) =>
  apiFetch<void>(`/orgs/${slug}`, { method: 'DELETE', body: { name } })
