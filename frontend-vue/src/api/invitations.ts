import type { OrgRole } from '@/api/organizations'
import { apiFetch } from '@/utils/api'

/**
 * Invitations: the only way into an organization other than creating one.
 *
 * Two very different surfaces behind one file. The management calls are scoped to an
 * organization the caller already belongs to; the redemption calls are reached by
 * **token** — no slug, and for the preview no session either — because the token is what
 * decides which organization the visitor is being let into.
 */

/** Owner is missing on purpose: it is handed over from the members list, never mailed. */
export const invitableRoles = ['admin', 'member', 'guest'] as const

export type InvitableRole = (typeof invitableRoles)[number]

export type InvitationStatus = 'pending' | 'accepted' | 'revoked' | 'expired'

export interface Invitation {
  id: string
  email: string
  role: OrgRole
  projectId: string | null
  projectRole: string | null
  invitedByName: string
  createdAt: string
  expiresAt: string
  status: InvitationStatus
  /** The project the invitation also joins, when it names one that still exists. */
  projectKey: string | null
  /** False for a stakeholder, and for every guest. */
  canOperateFactory: boolean
}

/**
 * The link, returned exactly once — when it is created and when it is resent. The API
 * stores only a hash of the token and cannot produce it again, which is also why
 * resending mints a new one and retires the old.
 */
export interface InvitationLink {
  invitation: Invitation
  token: string
  acceptUrl: string
  /** False on an instance with no SMTP relay. Then the link is the whole delivery mechanism. */
  emailSent: boolean
}

/** What an anonymous visitor holding a link is told. The address is masked. */
export interface InvitationPreview {
  organizationName: string
  organizationSlug: string
  invitedByName: string
  maskedEmail: string
  role: OrgRole
  expiresAt: string
  status: InvitationStatus
}

export interface AcceptedInvitation {
  organizationId: string
  organizationSlug: string
  organizationName: string
  role: OrgRole
  /** They were already in this organization; the invitation did not change their role. */
  alreadyMember: boolean
  /** The project the invitation put them on. Where they should land. */
  projectKey: string | null
}

/** Optional parts of an invitation. Omitted fields take the API's defaults. */
export interface InvitationOptions {
  projectId?: string
  projectRole?: 'admin' | 'member' | 'guest'
  canOperateFactory?: boolean
}

export const listInvitations = (slug: string) =>
  apiFetch<Invitation[]>(`/orgs/${slug}/invitations`)

export const createInvitation = (
  slug: string,
  email: string,
  role: InvitableRole,
  options: InvitationOptions = {},
) =>
  apiFetch<InvitationLink>(`/orgs/${slug}/invitations`, {
    method: 'POST',
    body: { email, role, ...options },
  })

/**
 * A stakeholder: someone outside the team, typically a client, who follows one project.
 * They join the organization as a member and the project as a member, so they can see the
 * board, add items and comment, and they may not start AI work.
 */
export const stakeholderInvitation = (projectId: string): InvitationOptions & { role: InvitableRole } => ({
  role: 'member',
  projectId,
  projectRole: 'member',
  canOperateFactory: false,
})

export const resendInvitation = (slug: string, id: string) =>
  apiFetch<InvitationLink>(`/orgs/${slug}/invitations/${id}/resend`, { method: 'POST' })

export const revokeInvitation = (slug: string, id: string) =>
  apiFetch<void>(`/orgs/${slug}/invitations/${id}`, { method: 'DELETE' })

export const previewInvitation = (token: string) =>
  apiFetch<InvitationPreview>(`/invitations/${encodeURIComponent(token)}`)

export const acceptInvitation = (token: string) =>
  apiFetch<AcceptedInvitation>(`/invitations/${encodeURIComponent(token)}/accept`, {
    method: 'POST',
  })

/**
 * Splits a paste into addresses. People paste from a mail client, a spreadsheet or a
 * chat message, so commas, semicolons, newlines and tabs all have to mean the same thing
 * — and `Ada Lovelace <ada@example.com>` is what a mail client actually hands over.
 *
 * Duplicates are collapsed rather than rejected: pasting the same column twice is a
 * common accident and not one worth an error message.
 */
export function parseAddresses(input: string): string[] {
  const seen = new Set<string>()

  for (const chunk of input.split(/[,;\s]+/)) {
    const angled = /<([^>]+)>/.exec(chunk)
    const address = (angled?.[1] ?? chunk).trim().toLowerCase()
    if (address.includes('@')) seen.add(address)
  }

  return [...seen]
}
