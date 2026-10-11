import { describe, expect, it } from 'vitest'
import { RunnerClient, RunnerHttpError } from '../../src/runner/client.js'
import { claimedRun, agentToken, runnerToken } from './helpers.js'

describe('RunnerClient attachments', () => {
  it('lists and downloads attachments with the short-lived agent token', async () => {
    const calls: Array<{ url: URL; init: RequestInit }> = []
    const fetchImpl = (async (input: Parameters<typeof fetch>[0], init?: RequestInit) => {
      const url = new URL(String(input))
      calls.push({ url, init: init ?? {} })
      if (url.pathname.endsWith('/attachments/a1/download')) {
        return new Response(new Uint8Array([1, 2, 3]), { status: 200 })
      }
      return new Response(JSON.stringify([
        { id: 'a1', fileName: 'screenshot.webp', contentType: 'image/webp', sizeBytes: 3, commentId: null },
      ]), { status: 200, headers: { 'Content-Type': 'application/json' } })
    }) as typeof fetch
    const client = new RunnerClient({ baseUrl: 'https://aictiq.example.com/', token: runnerToken, fetch: fetchImpl })

    const attachments = await client.listAttachments(claimedRun())
    const bytes = await client.downloadAttachment(claimedRun(), 'a1')

    expect(attachments).toHaveLength(1)
    expect(bytes).toEqual(new Uint8Array([1, 2, 3]))
    expect(calls[0]?.url.pathname).toBe('/api/v1/orgs/acme/items/ACME-42/attachments')
    expect(calls[0]?.url.searchParams.get('include')).toBe('comments')
    expect(calls[1]?.url.pathname).toBe('/api/v1/orgs/acme/attachments/a1/download')
    expect(calls).toHaveLength(2)
    for (const call of calls) {
      expect(call.init.method).toBe('GET')
      expect((call.init.headers as Record<string, string>).Authorization).toBe(`Bearer ${agentToken}`)
    }
  })
})

describe('RunnerClient fleet mode', () => {
  const enrollmentToken = 'jfe_enroll_0123456789'

  function recording(status: number, body?: unknown) {
    const calls: Array<{ url: URL; init: RequestInit }> = []
    const fetchImpl = (async (input: Parameters<typeof fetch>[0], init?: RequestInit) => {
      calls.push({ url: new URL(String(input)), init: init ?? {} })
      return new Response(body === undefined ? null : JSON.stringify(body), {
        status,
        headers: { 'Content-Type': 'application/json' },
      })
    }) as typeof fetch
    return { calls, fetchImpl }
  }

  it('enrolls with the enrollment token, a name and the tags, and returns the new secret', async () => {
    const { calls, fetchImpl } = recording(200, { runnerId: 'r9', name: 'fleet-1', secret: runnerToken })
    const client = new RunnerClient({ baseUrl: 'https://aictiq.example.com/', token: enrollmentToken, fetch: fetchImpl })

    expect(await client.enroll('fleet-1', ['docker', 'gpu'])).toEqual({ runnerId: 'r9', name: 'fleet-1', secret: runnerToken })
    expect(await client.enroll(null, [])).toBeDefined()

    expect(calls[0]?.url.href).toBe('https://aictiq.example.com/api/v1/runner/enroll')
    expect(calls[0]?.init.method).toBe('POST')
    expect((calls[0]?.init.headers as Record<string, string>).Authorization).toBe(`Bearer ${enrollmentToken}`)
    expect(JSON.parse(String(calls[0]?.init.body))).toEqual({ name: 'fleet-1', tags: ['docker', 'gpu'] })
    expect(JSON.parse(String(calls[1]?.init.body))).toEqual({ name: null, tags: [] })
  })

  it('keeps the problem of a refused enrollment for the command to explain', async () => {
    const problem = { title: 'The request is invalid.', errors: { tags: ['Unknown tag.'] } }
    const { fetchImpl } = recording(400, problem)
    const error = await new RunnerClient({ baseUrl: 'https://aictiq.example.com', token: enrollmentToken, fetch: fetchImpl })
      .enroll(null, ['x'])
      .catch((e: unknown) => e)

    expect(error).toBeInstanceOf(RunnerHttpError)
    expect((error as RunnerHttpError).status).toBe(400)
    expect((error as RunnerHttpError).problem).toEqual(problem)
  })

  it('deregisters with the runner secret', async () => {
    const { calls, fetchImpl } = recording(204)
    await new RunnerClient({ baseUrl: 'https://aictiq.example.com', token: runnerToken, fetch: fetchImpl }).deregister()

    expect(calls[0]?.url.pathname).toBe('/api/v1/runner/deregister')
    expect(calls[0]?.init.method).toBe('POST')
    expect((calls[0]?.init.headers as Record<string, string>).Authorization).toBe(`Bearer ${runnerToken}`)
  })
})
