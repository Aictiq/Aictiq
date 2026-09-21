import { afterEach, describe, expect, it, vi } from 'vitest'

import {
  createAgent,
  createAgentToken,
  disableAgent,
  listAgentTokens,
  listAgents,
  revokeAgentToken,
  updateAgent,
} from '@/api/agents'

/**
 * Agents are members of an organization, so every route is nested under one — an agent id
 * from elsewhere is a 404 rather than something this organization can reach. That URL
 * shape is what these assert, along with disable being a DELETE that does not delete: the
 * API revokes its tokens and marks it inactive, because its id is written into everything
 * it ever touched.
 */

function stubFetch(body: unknown = {}) {
  const fetchMock = vi.fn(
    async (_input: RequestInfo | URL, _init?: RequestInit) =>
      new Response(JSON.stringify(body), {
        status: 200,
        headers: { 'content-type': 'application/json' },
      }),
  )
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('the agent endpoints', () => {
  it('nests every agent route under the organization that owns it', async () => {
    const fetchMock = stubFetch([])

    await listAgents('acme')
    await listAgentTokens('acme', 'agent-1')

    expect(String(fetchMock.mock.calls[0]![0])).toBe('/api/v1/orgs/acme/agents')
    expect(String(fetchMock.mock.calls[1]![0])).toBe('/api/v1/orgs/acme/agents/agent-1/tokens')
  })

  it('creates with a display name and the CSRF header', async () => {
    const fetchMock = stubFetch({ userId: 'agent-1' })

    await createAgent('acme', { displayName: 'claude-dev' })

    const [url, init] = fetchMock.mock.calls[0]!
    expect(String(url)).toBe('/api/v1/orgs/acme/agents')
    expect(init!.method).toBe('POST')
    expect(JSON.parse(String(init!.body))).toEqual({ displayName: 'claude-dev' })
    expect(new Headers(init!.headers).get('X-Aictiq-Request')).toBe('1')
  })

  it('can put an agent straight into projects', async () => {
    const fetchMock = stubFetch({ userId: 'agent-1' })

    await createAgent('acme', { displayName: 'release-bot', projectIds: ['p1', 'p2'] })

    expect(JSON.parse(String(fetchMock.mock.calls[0]![1]!.body))).toEqual({
      displayName: 'release-bot',
      projectIds: ['p1', 'p2'],
    })
  })

  it('renames and re-enables through the same patch', async () => {
    const fetchMock = stubFetch({ userId: 'agent-1' })

    await updateAgent('acme', 'agent-1', { displayName: 'claude-review' })
    await updateAgent('acme', 'agent-1', { isActive: true })

    expect(fetchMock.mock.calls[0]![1]!.method).toBe('PATCH')
    expect(JSON.parse(String(fetchMock.mock.calls[0]![1]!.body))).toEqual({
      displayName: 'claude-review',
    })
    expect(JSON.parse(String(fetchMock.mock.calls[1]![1]!.body))).toEqual({ isActive: true })
  })

  it('disables with DELETE, which is not a delete', async () => {
    const fetchMock = stubFetch()

    await disableAgent('acme', 'agent-1')

    // The API revokes its tokens and marks it inactive; the row stays, because every item
    // it ever touched names it.
    const [url, init] = fetchMock.mock.calls[0]!
    expect(String(url)).toBe('/api/v1/orgs/acme/agents/agent-1')
    expect(init!.method).toBe('DELETE')
  })

  it('issues and revokes a token against the agent, not the caller', async () => {
    const fetchMock = stubFetch({ token: {}, secret: 'aiq_x' })

    await createAgentToken('acme', 'agent-1', { name: 'CI', scopes: ['read', 'mcp'] })
    await revokeAgentToken('acme', 'agent-1', 'tok-1')

    expect(String(fetchMock.mock.calls[0]![0])).toBe('/api/v1/orgs/acme/agents/agent-1/tokens')
    expect(JSON.parse(String(fetchMock.mock.calls[0]![1]!.body))).toEqual({
      name: 'CI',
      scopes: ['read', 'mcp'],
    })
    // Never /me/tokens: these are the agent's, and the caller is only acting for it.
    expect(String(fetchMock.mock.calls[1]![0])).toBe(
      '/api/v1/orgs/acme/agents/agent-1/tokens/tok-1',
    )
    expect(fetchMock.mock.calls[1]![1]!.method).toBe('DELETE')
  })
})
