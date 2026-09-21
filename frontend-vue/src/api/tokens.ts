import { apiFetch } from '@/utils/api'

/**
 * Personal access tokens — the credential the CLI, the MCP server and agents present.
 *
 * The secret comes back exactly once, from `createToken`. The API stores only its hash, so
 * there is no endpoint that could return it again and no amount of asking will produce it:
 * the UI has to make that moment count.
 */

export type TokenScope = 'read' | 'write' | 'admin' | 'mcp'

export interface AccessToken {
  id: string
  name: string
  /** `aiq_a1b2c3d4…` — all that survives of the secret, enough to tell two apart. */
  display: string
  /** Empty means unscoped: the token may do whatever its owner may. Scopes only narrow. */
  scopes: TokenScope[]
  /** Set, and the token is refused against every other organization. */
  organizationId: string | null
  createdAt: string
  expiresAt: string | null
  lastUsedAt: string | null
  isExpired: boolean
}

export interface AccessTokenCreated {
  token: AccessToken
  /** Shown once. There is nowhere to fetch it from afterwards. */
  secret: string
}

export interface CreateAccessTokenBody {
  name: string
  scopes: TokenScope[]
  organizationId?: string
  expiresInDays?: number
}

export const tokenScopes: { scope: TokenScope; label: string; description: string }[] = [
  { scope: 'read', label: 'Read', description: 'See everything its owner can see.' },
  { scope: 'write', label: 'Write', description: 'Create and change items, comments and pages.' },
  { scope: 'admin', label: 'Admin', description: 'Manage members, projects and other tokens.' },
  { scope: 'mcp', label: 'MCP', description: 'Reach the MCP endpoint. Give an agent this one.' },
]

export const listTokens = () => apiFetch<AccessToken[]>('/me/tokens')

export const createToken = (body: CreateAccessTokenBody) =>
  apiFetch<AccessTokenCreated>('/me/tokens', { method: 'POST', body })

export const revokeToken = (id: string) =>
  apiFetch<void>(`/me/tokens/${id}`, { method: 'DELETE' })

/**
 * What a token is allowed to do, in one phrase. An empty scope set is *unscoped* rather
 * than powerless, and that is worth saying out loud — it is the surprising half of the
 * rule that scopes only ever narrow.
 */
export function describeScopes(scopes: TokenScope[]): string {
  if (scopes.length === 0) return 'Everything you can do'
  return scopes.map((scope) => tokenScopes.find((s) => s.scope === scope)?.label ?? scope).join(', ')
}
