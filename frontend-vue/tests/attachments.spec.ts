import { afterEach, describe, expect, it, vi } from 'vitest'

import { referencedAttachmentIds, settleAttachments, uploadAttachment } from '@/api/attachments'

/**
 * Pasted files are uploaded before whatever owns them exists. What matters here: the file
 * goes to the API as multipart (never to the object store), and posting a comment commits
 * exactly the uploads its body still shows and throws away the ones deleted before posting.
 */

const kept = '0198a1b2-0000-7000-8000-000000000001'
const removed = '0198a1b2-0000-7000-8000-000000000002'

function stubFetch() {
  const fetchMock = vi.fn(async (input: RequestInfo | URL, _init?: RequestInit) => {
    if (String(input).endsWith('/projects/WEB/attachments')) return Response.json({ id: kept })
    return new Response(null, { status: 204 })
  })
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

afterEach(() => vi.unstubAllGlobals())

describe('uploading an attachment', () => {
  it('posts the file to the API as multipart with the CSRF header, and nothing else', async () => {
    const fetchMock = stubFetch()

    const id = await uploadAttachment('acme', 'WEB', new File([new Uint8Array([1, 2])], 'shot.png', { type: 'image/png' }))

    expect(id).toBe(kept)
    expect(fetchMock).toHaveBeenCalledTimes(1)
    const [url, init] = fetchMock.mock.calls[0]!
    expect(String(url)).toBe('/api/v1/orgs/acme/projects/WEB/attachments')
    expect(init!.method).toBe('POST')
    expect(new Headers(init!.headers).get('X-Aictiq-Request')).toBe('1')
    const body = init!.body as FormData
    expect((body.get('file') as File).name).toBe('shot.png')
  })
})

describe('settling a comment’s uploads', () => {
  it('commits what the body references and deletes what it no longer does', async () => {
    const fetchMock = stubFetch()
    const body = `Look ![shot](/api/v1/orgs/acme/attachments/${kept}/download)`

    expect(referencedAttachmentIds(body)).toEqual(new Set([kept]))
    await settleAttachments('acme', [kept, removed], body, { commentId: 'c1' })

    const calls = fetchMock.mock.calls.map(([url, init]) => `${init?.method} ${String(url)}`)
    expect(calls).toContain(`POST /api/v1/orgs/acme/attachments/${kept}/commit`)
    expect(calls).toContain(`DELETE /api/v1/orgs/acme/attachments/${removed}`)
    const commit = fetchMock.mock.calls.find(([url]) => String(url).endsWith('/commit'))!
    expect(JSON.parse(String(commit[1]!.body))).toEqual({ commentId: 'c1' })
  })
})
