import { afterEach, describe, expect, it, vi } from 'vitest'

import {
  addItemLabel,
  createLabel,
  deleteLabel,
  listLabels,
  removeItemLabel,
  updateLabel,
} from '@/api/labels'

/**
 * Labels are project-scoped for their own CRUD, but item labelling is addressed through
 * the organization and the item's permanent key — the same shape as every other
 * item-scoped write. That URL split is most of what is worth asserting here.
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

describe('the label endpoints', () => {
  it('addresses the project labels through the project that owns them', async () => {
    const fetchMock = stubFetch([])

    await listLabels('acme', 'WEB')

    expect(String(fetchMock.mock.calls[0]![0])).toBe('/api/v1/orgs/acme/projects/WEB/labels')
    expect(fetchMock.mock.calls[0]![1]?.method ?? 'GET').toBe('GET')
  })

  it('creates with the CSRF header and only what was filled in', async () => {
    const fetchMock = stubFetch({ id: 'l1' })

    await createLabel('acme', 'WEB', { name: 'frontend', color: '#eda45c' })

    const [url, init] = fetchMock.mock.calls[0]!
    expect(String(url)).toBe('/api/v1/orgs/acme/projects/WEB/labels')
    expect(init!.method).toBe('POST')
    expect(JSON.parse(String(init!.body))).toEqual({ name: 'frontend', color: '#eda45c' })
    expect(new Headers(init!.headers).get('X-Aictiq-Request')).toBe('1')
  })

  it('patches a label with the version echoed back', async () => {
    const fetchMock = stubFetch({ id: 'l1' })

    await updateLabel('acme', 'WEB', 'l1', {
      name: 'backend',
      group: 'type',
      version: 3,
    })

    const [url, init] = fetchMock.mock.calls[0]!
    expect(String(url)).toBe('/api/v1/orgs/acme/projects/WEB/labels/l1')
    expect(init!.method).toBe('PATCH')
    expect(JSON.parse(String(init!.body))).toEqual({
      name: 'backend',
      group: 'type',
      version: 3,
    })
  })

  it('deletes a label by id, scoped to the project', async () => {
    const fetchMock = stubFetch()

    await deleteLabel('acme', 'WEB', 'l1')

    expect(String(fetchMock.mock.calls[0]![0])).toBe('/api/v1/orgs/acme/projects/WEB/labels/l1')
    expect(fetchMock.mock.calls[0]![1]!.method).toBe('DELETE')
  })

  it('applies and removes a label on an item through the organization and the item key', async () => {
    const fetchMock = stubFetch()

    await addItemLabel('acme', 'WEB-42', 'l1')
    await removeItemLabel('acme', 'WEB-42', 'l1')

    const path = '/api/v1/orgs/acme/items/WEB-42/labels/l1'
    expect(String(fetchMock.mock.calls[0]![0])).toBe(path)
    expect(fetchMock.mock.calls[0]![1]!.method).toBe('PUT')
    expect(String(fetchMock.mock.calls[1]![0])).toBe(path)
    expect(fetchMock.mock.calls[1]![1]!.method).toBe('DELETE')
  })
})
