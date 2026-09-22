import { hasOrgRole, type OrgRole } from '@/api/organizations'
import { apiFetch } from '@/utils/api'

/** The people in one organization, and what may be done to their membership. */

export interface Member {
  userId: string
  displayName: string
  /** Null when a guest is reading: they see the team without collecting its addresses. */
  email: string | null
  avatarKey: string | null
  isAgent: boolean
  role: OrgRole
  joinedAt: string
  /** Effective: always true for owners and admins, never for guests, a choice for members. */
  canOperateFactory: boolean
}

/** What a write returns - the membership. The person did not change. */
export interface Membership {
  userId: string
  role: OrgRole
  joinedAt: string
  canOperateFactory: boolean
}

export interface Paged<T> {
  items: T[]
  page: number
  pageSize: number
  totalCount: number
  totalPages: number
}

export interface ListMembersQuery {
  page?: number
  pageSize?: number
  search?: string
}

export const orgRoles: OrgRole[] = ['owner', 'admin', 'member', 'guest']

/**
 * The same rule the API enforces, mirrored so the UI can grey out what it knows will be
 * refused. It is a courtesy, never the check: the server decides, and every one of these
 * refusals is also a 403.
 *
 * An administrator may act on people strictly below them and assign roles strictly below
 * them; an owner is the top of the ladder and may do anything, with the last-owner rule
 * left to the database.
 */
export function canAssignRole(actor: OrgRole, target: OrgRole, desired: OrgRole): boolean {
  if (actor === 'owner') return true
  return actor === 'admin' && !hasOrgRole(target, 'admin') && !hasOrgRole(desired, 'admin')
}

/**
 * Whether operating the AI factory is a choice for this person at all. For everyone but a
 * member the role decides: owners and admins always operate, guests never do.
 */
export function factoryFlagIsChoosable(role: OrgRole): boolean {
  return role === 'member'
}

/** Owners and admins decide it, for members. The same rule the API enforces. */
export function canSetFactoryOperator(actor: OrgRole, target: OrgRole): boolean {
  return factoryFlagIsChoosable(target) && hasOrgRole(actor, 'admin')
}

export function canRemoveMember(actor: OrgRole, target: OrgRole): boolean {
  if (actor === 'owner') return true
  return actor === 'admin' && !hasOrgRole(target, 'admin')
}

export const listMembers = (slug: string, query: ListMembersQuery = {}) =>
  apiFetch<Paged<Member>>(`/orgs/${slug}/members`, { query })

export const setMemberRole = (slug: string, userId: string, role: OrgRole) =>
  apiFetch<Membership>(`/orgs/${slug}/members/${userId}`, { method: 'PUT', body: { role } })

/** A member who may not start AI work is a stakeholder: they see the board and add items. */
export const setMemberFactoryOperator = (slug: string, userId: string, canOperateFactory: boolean) =>
  apiFetch<Membership>(`/orgs/${slug}/members/${userId}`, {
    method: 'PUT',
    body: { canOperateFactory },
  })

/** Removing yourself is leaving; the API allows it for any role except the last owner. */
export const removeMember = (slug: string, userId: string) =>
  apiFetch<void>(`/orgs/${slug}/members/${userId}`, { method: 'DELETE' })
