import type { OrgRole } from '@/api/organizations'
import type { TokenScope } from '@/api/tokens'
import { apiFetch } from '@/utils/api'

/**
 * Agents: bot members of an organization, owned by a person, carrying a token instead of
 * a password.
 *
 * They are ordinary members - the same roster, the same assignee pickers, the same author
 * columns - which is exactly why every surface that renders one has to show that it *is*
 * one. The bot badge on `UserAvatar` is not decoration.
 */

export interface Agent {
  userId: string
  displayName: string
  /** Synthesised and undeliverable: an agent has no inbox. */
  email: string
  ownerUserId: string
  /** The person answerable for it - the first question anyone asks about a surprising action. */
  ownerName: string
  role: OrgRole
  isActive: boolean
  createdAt: string
  /** When one of its tokens was last used, which is what "active" means for an agent. */
  lastActiveAt: string | null
  tokenCount: number
}

export interface AgentToken {
  id: string
  name: string
  display: string
  scopes: TokenScope[]
  createdAt: string
  expiresAt: string | null
  lastUsedAt: string | null
}

export interface AgentTokenIssued {
  token: AgentToken
  /** Shown once. Only its hash is stored. */
  secret: string
}

export interface CreateAgentBody {
  displayName: string
  projectIds?: string[]
}

export const listAgents = (slug: string) => apiFetch<Agent[]>(`/orgs/${slug}/agents`)

export const createAgent = (slug: string, body: CreateAgentBody) =>
  apiFetch<Agent>(`/orgs/${slug}/agents`, { method: 'POST', body })

export const updateAgent = (
  slug: string,
  agentId: string,
  body: { displayName?: string; isActive?: boolean },
) => apiFetch<Agent>(`/orgs/${slug}/agents/${agentId}`, { method: 'PATCH', body })

/** Disables it and revokes every token. Never a delete - its id is in everything it touched. */
export const disableAgent = (slug: string, agentId: string) =>
  apiFetch<void>(`/orgs/${slug}/agents/${agentId}`, { method: 'DELETE' })

export const listAgentTokens = (slug: string, agentId: string) =>
  apiFetch<AgentToken[]>(`/orgs/${slug}/agents/${agentId}/tokens`)

export const createAgentToken = (
  slug: string,
  agentId: string,
  body: { name: string; scopes: TokenScope[]; expiresInDays?: number },
) => apiFetch<AgentTokenIssued>(`/orgs/${slug}/agents/${agentId}/tokens`, { method: 'POST', body })

export const revokeAgentToken = (slug: string, agentId: string, tokenId: string) =>
  apiFetch<void>(`/orgs/${slug}/agents/${agentId}/tokens/${tokenId}`, { method: 'DELETE' })
