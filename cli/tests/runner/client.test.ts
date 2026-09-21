import { describe, expect, it } from 'vitest'
import { RunnerClient } from '../../src/runner/client.js'
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
