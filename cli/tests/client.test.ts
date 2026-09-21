import { describe, expect, it } from 'vitest'
import { AictiqClient } from '../src/api/client.js'
import { CliError, ExitCode } from '../src/errors.js'

function stub(status: number, body: unknown, capture?: (url: URL, init: RequestInit) => void) {
  return (async (input: Parameters<typeof fetch>[0], init?: RequestInit) => {
    capture?.(new URL(String(input)), init ?? {})
    return new Response(body === undefined ? null : JSON.stringify(body), {
      status,
      headers: { 'Content-Type': 'application/problem+json' },
    })
  }) as typeof fetch
}

const client = (fetchImpl: typeof fetch) =>
  new AictiqClient({ baseUrl: 'https://aictiq.example.com/', token: 'aiq_secret', fetch: fetchImpl })

describe('AictiqClient', () => {
  it('sends the bearer token and expands path and query parameters', async () => {
    let seen: { url: URL; init: RequestInit } | undefined
    await client(
      stub(200, { key: 'ACME-1' }, (url, init) => {
        seen = { url, init }
      }),
    ).request<unknown, '/api/v1/orgs/{orgSlug}/projects/{projectKey}/items', 'get'>(
      'get',
      '/api/v1/orgs/{orgSlug}/projects/{projectKey}/items',
      { path: { orgSlug: 'acme', projectKey: 'ACME' }, query: { filter: 'state:active', page: 2 } },
    )

    expect(seen?.url.pathname).toBe('/api/v1/orgs/acme/projects/ACME/items')
    expect(seen?.url.searchParams.get('filter')).toBe('state:active')
    expect(seen?.url.searchParams.get('page')).toBe('2')
    const headers = seen?.init.headers as Record<string, string>
    expect(headers.Authorization).toBe('Bearer aiq_secret')
    // Bearer requests are not subject to the cookie CSRF rule, and must not pretend to be.
    expect(headers['X-Aictiq-Request']).toBeUndefined()
  })

  it.each([
    [400, ExitCode.Validation],
    [401, ExitCode.Auth],
    [403, ExitCode.Auth],
    [404, ExitCode.NotFound],
    [409, ExitCode.Conflict],
    [422, ExitCode.Validation],
    [500, ExitCode.Error],
  ])('maps HTTP %i to exit code %i', async (status, exitCode) => {
    const error = await client(stub(status, { title: 'Nope.' }))
      .request<unknown, '/api/v1/orgs', 'get'>('get', '/api/v1/orgs')
      .catch((e: unknown) => e)

    expect(error).toBeInstanceOf(CliError)
    expect((error as CliError).exitCode).toBe(exitCode)
  })

  it('renders the problem title and every field error', async () => {
    const error = await client(
      stub(422, {
        title: 'The request is invalid.',
        errors: { title: ['Use 200 characters or fewer.'], type: ['Unknown item type.'] },
      }),
    )
      .request<unknown, '/api/v1/orgs', 'get'>('get', '/api/v1/orgs')
      .catch((e: unknown) => e)

    expect((error as CliError).message).toBe(
      [
        'The request is invalid.',
        '  title: Use 200 characters or fewer.',
        '  type: Unknown item type.',
      ].join('\n'),
    )
  })

  it('reports an unreachable instance without a stack trace', async () => {
    const error = await client((() => Promise.reject(new Error('ECONNREFUSED'))) as typeof fetch)
      .request<unknown, '/api/v1/orgs', 'get'>('get', '/api/v1/orgs')
      .catch((e: unknown) => e)

    expect((error as CliError).exitCode).toBe(ExitCode.Error)
    expect((error as CliError).message).toContain('Cannot reach https://aictiq.example.com')
  })

  it('treats 204 as an empty result', async () => {
    const result = await client(stub(204, undefined)).request<unknown, '/api/v1/orgs', 'get'>(
      'get',
      '/api/v1/orgs',
    )
    expect(result).toBeUndefined()
  })
})
