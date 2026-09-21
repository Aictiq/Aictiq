import { afterEach, describe, expect, it, vi } from 'vitest'

import {
  createRule,
  deleteRule,
  getRule,
  listOrgRules,
  listRuleFirings,
  listRules,
  ruleSkipReasonText,
  updateRule,
} from '@/api/rules'

/**
 * Automation rules live under the project the same way playbooks and labels
 * do — `/orgs/{slug}/projects/{key}/rules` — while the Rules tab reads every rule the
 * caller administers in one organization-wide call.
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

describe('the rule endpoints', () => {
  it('reads every rule the caller administers, organization-wide', async () => {
    const fetchMock = stubFetch([])
    await listOrgRules('acme')
    expect(String(fetchMock.mock.calls[0]![0])).toBe('/api/v1/orgs/acme/rules')
  })

  it('keeps every project-scoped operation under the project key', async () => {
    const fetchMock = stubFetch([])

    await listRules('acme', 'ACME')
    await getRule('acme', 'ACME', 'r1')
    await deleteRule('acme', 'ACME', 'r1')
    await listRuleFirings('acme', 'ACME', 'r1')

    expect(String(fetchMock.mock.calls[0]![0])).toBe('/api/v1/orgs/acme/projects/ACME/rules')
    expect(String(fetchMock.mock.calls[1]![0])).toBe('/api/v1/orgs/acme/projects/ACME/rules/r1')
    expect(fetchMock.mock.calls[2]![1]!.method).toBe('DELETE')
    expect(String(fetchMock.mock.calls[3]![0])).toBe(
      '/api/v1/orgs/acme/projects/ACME/rules/r1/firings',
    )
  })

  it('creates with the trigger state, optional label, playbook and agent', async () => {
    const fetchMock = stubFetch({ id: 'r1' })
    const body = {
      name: 'Start implementation',
      triggerStateId: 's1',
      requiredLabelId: null,
      playbookId: 'p1',
      agentId: 'a1',
      enabled: true,
    }

    await createRule('acme', 'ACME', body)

    expect(fetchMock.mock.calls[0]![1]!.method).toBe('POST')
    expect(JSON.parse(String(fetchMock.mock.calls[0]![1]!.body))).toEqual(body)
  })

  it('patches with only the changed fields plus the version', async () => {
    const fetchMock = stubFetch({ id: 'r1' })

    await updateRule('acme', 'ACME', 'r1', { enabled: false, version: 3 })

    expect(fetchMock.mock.calls[0]![1]!.method).toBe('PATCH')
    expect(JSON.parse(String(fetchMock.mock.calls[0]![1]!.body))).toEqual({
      enabled: false,
      version: 3,
    })
  })
})

describe('ruleSkipReasonText', () => {
  it('renders known reasons as human text', () => {
    expect(ruleSkipReasonText('item-claimed')).toBe('Skipped: item already claimed')
    expect(ruleSkipReasonText('rule-loop')).toBe('Skipped: its own run moved the item here')
  })

  it('falls back to the raw reason for one this build does not know', () => {
    expect(ruleSkipReasonText('some-new-reason')).toBe('Skipped: some-new-reason')
  })
})
