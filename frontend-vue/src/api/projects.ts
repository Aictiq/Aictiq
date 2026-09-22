import type { OrgRole } from '@/api/organizations'
import { apiFetch } from '@/utils/api'

/**
 * Projects: the isolated workspaces inside an organization.
 *
 * Every project route is nested under the organization, because that is what establishes
 * the tenant - and the project is addressed by its **key**, the same short prefix that
 * will start every item id the team quotes.
 */

/** Ranked like the org roles, most privileged first. */
export type ProjectRole = 'admin' | 'member' | 'guest'

export type ProjectVisibility = 'private' | 'organization'

export interface Project {
  id: string
  /** Immutable. It is in every item id, link and agent configuration. */
  key: string
  name: string
  description: string | null
  visibility: ProjectVisibility
  /** A single emoji, or null. */
  icon: string | null
  /** `#rrggbb`, or null for the theme's default. */
  color: string | null
  isArchived: boolean
  createdAt: string
  /**
   * The caller's *effective* role - the better of what their organization role grants
   * implicitly and what an explicit membership grants. What the UI greys controls by.
   */
  role: ProjectRole
  /** xmin. Echo it back on every write or the API answers 409. */
  version: number
}

export interface ProjectMember {
  userId: string
  displayName: string
  email: string | null
  avatarKey: string | null
  isAgent: boolean
  role: ProjectRole
  /**
   * Here by virtue of their organization role or the project's visibility rather than by
   * being named on it. There is no membership to remove, so the menu must not offer to.
   */
  isImplicit: boolean
  addedAt: string | null
}

export interface CreateProjectBody {
  name: string
  /** Suggested from the name when omitted. Permanent once set. */
  key?: string
  description?: string
  visibility?: ProjectVisibility
  icon?: string
  color?: string
}

/**
 * PATCH semantics the API defines: a field left out is unchanged, and an **empty string**
 * clears one. There is no `key` - renaming it would break every reference already written
 * down.
 */
export interface UpdateProjectBody {
  name?: string
  description?: string
  visibility?: ProjectVisibility
  icon?: string
  color?: string
  version: number
}

export const projectRoles: ProjectRole[] = ['admin', 'member', 'guest']

const rank: Record<ProjectRole, number> = { admin: 0, member: 1, guest: 2 }

export function hasProjectRole(actual: ProjectRole | undefined, required: ProjectRole): boolean {
  return actual !== undefined && rank[actual] <= rank[required]
}

/** Mirrors `Project.SuggestKey` closely enough to preview. The API decides and resolves clashes. */
export function suggestKey(name: string): string {
  const words = name
    .normalize('NFD')
    .replace(/[\u0300-\u036f]/g, '')
    .split(/[\s\-_./]+/)
    .map((word) => word.replace(/[^a-zA-Z0-9]/g, '').toUpperCase())
    .filter(Boolean)

  const candidate = words.length === 1 ? words[0]! : words.map((word) => word[0]).join('')
  return candidate.replace(/^[0-9]+/, '').slice(0, 10)
}

export const listProjects = (slug: string, includeArchived = false) =>
  apiFetch<Project[]>(`/orgs/${slug}/projects`, {
    query: includeArchived ? { includeArchived: true } : undefined,
  })

export const createProject = (slug: string, body: CreateProjectBody) =>
  apiFetch<Project>(`/orgs/${slug}/projects`, { method: 'POST', body })

export const getProject = (slug: string, key: string) =>
  apiFetch<Project>(`/orgs/${slug}/projects/${key}`)

export const updateProject = (slug: string, key: string, body: UpdateProjectBody) =>
  apiFetch<Project>(`/orgs/${slug}/projects/${key}`, { method: 'PATCH', body })

export const setProjectArchived = (slug: string, key: string, archived: boolean) =>
  apiFetch<Project>(`/orgs/${slug}/projects/${key}/${archived ? 'archive' : 'unarchive'}`, {
    method: 'POST',
  })

/**
 * Deletes the project for good, with everything in it. `name` is the project's name as the
 * person typed it - the server checks it too, so a scripted mis-call cannot delete by key alone.
 */
export const deleteProject = (slug: string, key: string, name: string) =>
  apiFetch<void>(`/orgs/${slug}/projects/${key}`, { method: 'DELETE', body: { name } })

export const listProjectMembers = (slug: string, key: string) =>
  apiFetch<ProjectMember[]>(`/orgs/${slug}/projects/${key}/members`)

export const setProjectMemberRole = (
  slug: string,
  key: string,
  userId: string,
  role: ProjectRole,
) =>
  apiFetch<ProjectMember>(`/orgs/${slug}/projects/${key}/members/${userId}`, {
    method: 'PUT',
    body: { role },
  })

export const removeProjectMember = (slug: string, key: string, userId: string) =>
  apiFetch<void>(`/orgs/${slug}/projects/${key}/members/${userId}`, { method: 'DELETE' })

/** Who may create a project here - the org's own setting, mirrored so the button can hide. */
export function canCreateProjects(
  orgRole: OrgRole | undefined,
  membersCanCreateProjects: boolean,
): boolean {
  if (orgRole === undefined) return false
  if (orgRole === 'owner' || orgRole === 'admin') return true
  return orgRole === 'member' && membersCanCreateProjects
}
