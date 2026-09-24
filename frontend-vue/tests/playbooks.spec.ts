import { afterEach, describe, expect, it, vi } from 'vitest'

import {
  createPlaybook,
  createStarterPlaybook,
  deletePlaybook,
  getFactorySettings,
  getPlaybook,
  getPlaybookInstructions,
  listPlaybooks,
  promotePlaybook,
  updateFactorySettings,
  updatePlaybook,
} from '@/api/playbooks'
import { wikiOutline } from '@/lib/wiki'

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

describe('the playbook endpoints', () => {
  it('keeps every operation inside the project named by the URL', async () => {
    const fetchMock = stubFetch([])

    await listPlaybooks('acme', 'ACME')
    await getPlaybook('acme', 'ACME', 'p1')
    await createStarterPlaybook('acme', 'ACME')
    await promotePlaybook('acme', 'ACME', 'p1')
    await deletePlaybook('acme', 'ACME', 'p1')
    await getPlaybookInstructions('acme', 'ACME', 'p1')

    expect(String(fetchMock.mock.calls[0]![0])).toBe('/api/v1/orgs/acme/projects/ACME/playbooks')
    expect(String(fetchMock.mock.calls[1]![0])).toBe('/api/v1/orgs/acme/projects/ACME/playbooks/p1')
    expect(String(fetchMock.mock.calls[2]![0])).toBe(
      '/api/v1/orgs/acme/projects/ACME/playbooks/starter',
    )
    expect(fetchMock.mock.calls[2]![1]!.method).toBe('POST')
    expect(String(fetchMock.mock.calls[3]![0])).toBe(
      '/api/v1/orgs/acme/projects/ACME/playbooks/p1/default',
    )
    expect(fetchMock.mock.calls[3]![1]!.method).toBe('PUT')
    expect(fetchMock.mock.calls[4]![1]!.method).toBe('DELETE')
    expect(String(fetchMock.mock.calls[5]![0])).toBe(
      '/api/v1/orgs/acme/projects/ACME/playbooks/p1/instructions',
    )
  })

  it('creates and patches with the instructions, run outcomes and concurrency version', async () => {
    const fetchMock = stubFetch({ id: 'p1' })
    const body = {
      name: 'Implement',
      instructionsMarkdown: '# Implement\n\nShip it.',
      harness: 'claude' as const,
      onSuccessStateId: 'done',
      onFailureStateId: null,
      maxMinutes: 90,
    }

    await createPlaybook('acme', 'ACME', body)
    await updatePlaybook('acme', 'ACME', 'p1', { ...body, version: 7 })

    expect(JSON.parse(String(fetchMock.mock.calls[0]![1]!.body))).toEqual(body)
    expect(fetchMock.mock.calls[0]![1]!.method).toBe('POST')
    expect(JSON.parse(String(fetchMock.mock.calls[1]![1]!.body))).toEqual({
      ...body,
      version: 7,
    })
    expect(fetchMock.mock.calls[1]![1]!.method).toBe('PATCH')
  })
})

describe('the project factory settings endpoint', () => {
  it('reads and replaces settings at one project-scoped URL', async () => {
    const fetchMock = stubFetch({ projectId: 'project-1' })
    const body = {
      repoSource: 1 as const,
      repoFullName: null,
      defaultBranch: 'main',
      localPathHint: '/srv/aictiq',
      defaultAgentId: 'agent-1',
      version: 0,
    }

    await getFactorySettings('acme', 'ACME')
    await updateFactorySettings('acme', 'ACME', body)

    const url = '/api/v1/orgs/acme/projects/ACME/factory-settings'
    expect(String(fetchMock.mock.calls[0]![0])).toBe(url)
    expect(String(fetchMock.mock.calls[1]![0])).toBe(url)
    expect(fetchMock.mock.calls[1]![1]!.method).toBe('PUT')
    expect(JSON.parse(String(fetchMock.mock.calls[1]![1]!.body))).toEqual(body)
  })
})

describe('wikiOutline', () => {
  it('puts children after their parent regardless of API order', () => {
    const at = (id: string, parentId: string | null, position: number) => ({
      id,
      parentId,
      position,
      slug: id,
      title: id.toUpperCase(),
      updatedAt: '2026-09-17T00:00:00Z',
    })

    expect(
      wikiOutline([at('child', 'root', 0), at('second', null, 1), at('root', null, 0)]).map(
        (row) => [row.page.id, row.depth],
      ),
    ).toEqual([
      ['root', 0],
      ['child', 1],
      ['second', 0],
    ])
  })
})
