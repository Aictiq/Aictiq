import { flushPromises, mount, RouterLinkStub } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { VueQueryPlugin } from '@tanstack/vue-query'
import { createMemoryHistory, createRouter } from 'vue-router'
import { describe, expect, it, vi, afterEach, beforeAll } from 'vitest'

import ItemDetail from '@/components/items/ItemDetail.vue'
import { useOrganizationsStore } from '@/stores/organizations'
import { useSessionStore } from '@/stores/session'

/**
 * The line drawn on the item itself: anyone who can see the item can see its
 * runs - status, agent, pull request - but the Hand to agent button, the log link and the
 * Factory area belong to factory operators. The API holds that line regardless; this is
 * about the screen not offering what the server would refuse.
 */
const item = {
  id: 'i1',
  key: 'PROJ-1',
  type: 'story',
  title: 'Crash on save',
  stateId: 's1',
  descriptionMarkdown: '',
  descriptionHtml: '',
  stateCategory: 'active',
  priority: 'medium',
  assigneeId: null,
  teamId: null,
  sprintId: null,
  parentId: null,
  points: null,
  estimateHours: null,
  remainingHours: null,
  completedHours: null,
  dueDate: null,
  version: 4,
  labels: [],
  updatedAt: '2026-09-01T00:00:00Z',
  isWatching: false,
  watcherCount: 0,
  claimedBy: null,
  claimedAt: null,
  claimHeartbeatAt: null,
  rollup: { totalCount: 0, completedCount: 0, pointsTotal: 0, pointsCompleted: 0, remainingHours: 0 },
}

const project = {
  id: 'pr1',
  key: 'PROJ',
  name: 'Prototype',
  description: null,
  visibility: 'organization',
  icon: null,
  color: null,
  isArchived: false,
  createdAt: '2026-01-01T00:00:00Z',
  role: 'member',
  version: 1,
}

const runs = {
  items: [
    {
      id: 'r-1',
      projectId: 'pr1',
      itemId: 'i1',
      itemKey: 'PROJ-1',
      playbookId: 'p1',
      playbookName: 'Fix the bug',
      agentId: 'a1',
      agentName: 'claude-dev',
      requestedBy: 'u1',
      ruleId: null as string | null,
      ruleName: null as string | null,
      runnerId: null,
      runnerName: null,
      status: 'succeeded',
      harness: 'claude',
      playbookRevisionId: null,
      maxMinutes: 60,
      queuedAt: '2026-09-01T00:00:00Z',
      assignedAt: null,
      startedAt: '2026-09-01T00:01:00Z',
      finishedAt: '2026-09-01T00:09:00Z',
      lastHeartbeatAt: null,
      cancelRequested: false,
      outcomeSummary: 'Opened a PR',
      pullRequestUrl: 'https://github.com/acme/repo/pull/9',
      exitCode: 0,
      costUsd: 0.42,
      inputTokens: 1000,
      outputTokens: 500,
      failureReason: null,
      promptSnapshot: null,
      version: 9,
    },
  ],
  page: 1,
  pageSize: 25,
  totalCount: 1,
}

function stubFetch() {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: unknown) => {
      const url = String(input).split('?')[0] ?? ''
      const table: [RegExp, unknown][] = [
        [/\/items\/PROJ-1\/runs$/, runs],
        [/\/items\/PROJ-1$/, item],
        [/\/items\/PROJ-1\/comments$/, { items: [], page: 1, pageSize: 50, totalCount: 0 }],
        [/\/items\/PROJ-1\/history$/, { items: [], page: 1, pageSize: 50, total: 0 }],
        [/\/items\/PROJ-1\/relations$/, []],
        [/\/items\/PROJ-1\/links$/, []],
        [/\/items\/PROJ-1\/watchers$/, []],
        [/\/items\/PROJ-1\/children$/, []],
        [/\/workflows$/, []],
        [/\/projects\/PROJ\/members$/, []],
        [/\/projects\/PROJ$/, project],
        [/\/github/, []],
      ]
      for (const [pattern, body] of table) {
        if (pattern.test(url)) {
          return new Response(JSON.stringify(body), {
            status: 200,
            headers: { 'content-type': 'application/json' },
          })
        }
      }
      return new Response(JSON.stringify({ title: 'Not found', status: 404 }), {
        status: 404,
        headers: { 'content-type': 'application/problem+json' },
      })
    }),
  )
}

const organization = {
  id: 'org-1',
  slug: 'acme',
  name: 'Acme',
  role: 'member' as const,
  canOperateFactory: false,
}

beforeAll(() => {
  setActivePinia(createPinia())
})

async function mountDetail(canOperateFactory: boolean) {
  stubFetch()
  const pinia = createPinia()
  setActivePinia(pinia)
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/o/:slug/p/:projectKey/items/:itemKey', component: { template: '<div />' } },
      { path: '/o/:slug/factory/:pathMatch(.*)*', component: { template: '<div />' } },
      { path: '/', component: { template: '<div />' } },
    ],
  })
  await router.push('/o/acme/p/PROJ/items/PROJ-1')
  await router.isReady()

  const organizations = useOrganizationsStore()
  organizations.organizations = [{ ...organization, canOperateFactory }]
  organizations.select('acme')
  useSessionStore().set({
    id: 'u1',
    email: 'u1@acme.dev',
    firstName: 'U',
    lastName: 'One',
    fullName: 'U One',
    roles: [],
    isAgent: false,
    avatarKey: null,
    timeZone: null,
  })

  const wrapper = mount(ItemDetail, {
    props: { slug: 'acme', projectKey: 'PROJ', itemKey: 'PROJ-1' },
    global: {
      plugins: [pinia, router, VueQueryPlugin],
      stubs: { RouterLink: RouterLinkStub, Teleport: true },
    },
  })
  await flushPromises()
  return wrapper
}

afterEach(() => {
  vi.unstubAllGlobals()
  setActivePinia(createPinia())
})

describe('ItemDetail runs and the operator line', () => {
  it('shows an operator the Hand to agent button in the runs section, and the log link', async () => {
    const wrapper = await mountDetail(true)

    const section = wrapper.find('[data-testid="item-runs"]')
    expect(section.find('[data-testid="start-run-button"]').text()).toContain('Hand to agent')
    expect(wrapper.find('[data-testid="item-runs"]').text()).toContain('Succeeded')
    expect(wrapper.find('[data-testid="item-runs"]').text()).toContain('Open log')
  })

  it('shows a stakeholder the runs but neither the Start button nor the log link', async () => {
    const wrapper = await mountDetail(false)

    expect(wrapper.find('[data-testid="item-runs"]').text()).toContain('Succeeded')
    expect(wrapper.find('[data-testid="item-runs"]').text()).toContain('Pull request')
    expect(wrapper.find('[data-testid="start-run-button"]').exists()).toBe(false)
    expect(wrapper.text()).not.toContain('Open log')
  })

  it('links a run a comment asked for to that comment, and says when it was a follow-up', async () => {
    const original = runs.items[0]!
    runs.items[0] = {
      ...original,
      triggerCommentId: 'c-9',
      followsUpRunId: 'r-0',
    } as typeof original

    const wrapper = await mountDetail(false)

    const link = wrapper
      .findAllComponents(RouterLinkStub)
      .find((entry) => entry.attributes('data-testid') === 'run-trigger-comment')!
    expect(link.text()).toBe('Follow-up from a comment')
    expect(link.props('to')).toEqual({ hash: '#comment-c-9' })

    runs.items[0] = original
  })

  it('names the rule that dispatched a run with no requester', async () => {
    const original = { ...runs.items[0]! }
    runs.items[0]!.requestedBy = null as unknown as string
    runs.items[0]!.ruleId = 'rule-1'
    runs.items[0]!.ruleName = 'Start implementation'

    const wrapper = await mountDetail(true)

    expect(wrapper.find('[data-testid="item-runs"]').text()).toContain('Rule: Start implementation')

    Object.assign(runs.items[0]!, original)
  })

  it('lets a stakeholder copy the resume command of a recent run with a kept checkout', async () => {
    const original = { ...runs.items[0]! }
    Object.assign(runs.items[0]!, {
      runnerName: 'build-box',
      sessionId: 'sess-1',
      workspacePath: '/srv/runner/r 1/repo',
      finishedAt: new Date().toISOString(),
    })
    const writeText = vi.fn(async () => {})
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true })

    const wrapper = await mountDetail(false)
    const section = wrapper.find('[data-testid="item-runs"]')
    expect(section.find('[data-testid="run-resume-runner"]').text()).toBe('on build-box')
    const action = section.find('[data-testid="run-resume-command"]')
    expect(action.attributes('title')).toContain('same session and checkout')
    await action.trigger('click')
    await flushPromises()
    expect(writeText).toHaveBeenCalledWith("cd '/srv/runner/r 1/repo' && claude --resume 'sess-1'")

    Object.assign(runs.items[0]!, original)
  })

  it('offers no resume command for a run without a stored checkout or past five days', async () => {
    const original = { ...runs.items[0]! }
    Object.assign(runs.items[0]!, { sessionId: 'sess-1', workspacePath: null, finishedAt: new Date().toISOString() })
    expect((await mountDetail(true)).find('[data-testid="run-resume-command"]').exists()).toBe(false)

    Object.assign(runs.items[0]!, { workspacePath: '/srv/repo', finishedAt: '2026-09-01T00:09:00Z' })
    expect((await mountDetail(true)).find('[data-testid="run-resume-command"]').exists()).toBe(false)

    Object.assign(runs.items[0]!, original)
  })
})
