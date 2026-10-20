import { flushPromises, mount, RouterLinkStub, type VueWrapper } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query'
import { createMemoryHistory, createRouter } from 'vue-router'
import { describe, expect, it, vi, afterEach } from 'vitest'

import ItemDetail from '@/components/items/ItemDetail.vue'
import ClaimBanner from '@/components/common/ClaimBanner.vue'
import MarkdownEditor from '@/components/common/MarkdownEditor.vue'
import type { Mentionable } from '@/lib/mentions'
import { useOrganizationsStore } from '@/stores/organizations'
import { useSessionStore } from '@/stores/session'

vi.mock('@/composables/useProjectRealtime', () => ({ useProjectRealtime: vi.fn() }))

/** Item factory history and actions are visible only to factory operators. */
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
      runnerName: null as string | null,
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

const members = [
  { userId: 'u2', displayName: 'Ana Horvat', avatarKey: null, role: 'member', isAgent: false },
  { userId: 'a9', displayName: 'builder-bot', avatarKey: null, role: 'member', isAgent: true },
]

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
        [/\/projects\/PROJ\/members$/, members],
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

const wrappers: VueWrapper[] = []
const clients: QueryClient[] = []

async function mountDetail(canOperateFactory?: boolean, modal = false) {
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
  organizations.organizations =
    canOperateFactory === undefined ? [] : [{ ...organization, canOperateFactory }]
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

  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  clients.push(client)
  const wrapper = mount(ItemDetail, {
    props: { slug: 'acme', projectKey: 'PROJ', itemKey: 'PROJ-1', modal },
    global: {
      plugins: [pinia, router, [VueQueryPlugin, { queryClient: client }]],
      stubs: { RouterLink: RouterLinkStub, Teleport: true },
    },
  })
  wrappers.push(wrapper)
  await flushPromises()
  return wrapper
}

afterEach(() => {
  wrappers.splice(0).forEach((wrapper) => wrapper.unmount())
  clients.splice(0).forEach((client) => client.clear())
  vi.unstubAllGlobals()
  setActivePinia(createPinia())
})

describe('ItemDetail comment mentions', () => {
  const suggested = (wrapper: VueWrapper) =>
    (wrapper.findAllComponents(MarkdownEditor).at(-1)!.props('mentionables') as Mentionable[]).map(
      (person) => person.name,
    )

  it('offers an operator both people and agents', async () => {
    const wrapper = await mountDetail(true)

    expect(suggested(wrapper)).toEqual(['Ana Horvat', 'builder-bot'])
  })

  it.each([false, undefined])('offers only people when the caller may not operate the factory (%s)', async (canOperate) => {
    const wrapper = await mountDetail(canOperate)

    expect(suggested(wrapper)).toEqual(['Ana Horvat'])
  })
})

describe('ItemDetail runs and the operator line', () => {
  it('shows an operator the Hand to agent button in the runs section, and the log link', async () => {
    const wrapper = await mountDetail(true)

    const section = wrapper.find('[data-testid="item-runs"]')
    expect(section.find('[data-testid="start-run-button"]').text()).toContain('Hand to agent')
    expect(wrapper.find('[data-testid="item-runs"]').text()).toContain('Succeeded')
    expect(wrapper.find('[data-testid="item-runs"]').text()).toContain('Open log')
  })

  it.each([false, true])('hides factory details and skips the runs request for a stakeholder (modal=%s)', async (modal) => {
    const wrapper = await mountDetail(false, modal)

    expect(wrapper.find('[data-testid="item-runs"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="start-run-button"]').exists()).toBe(false)
    expect(wrapper.text()).not.toContain('Agent runs')
    expect(wrapper.text()).not.toContain('Open log')
    expect(wrapper.text()).not.toContain('Copy resume command')
    expect(wrapper.text()).toContain('Description')
    expect(wrapper.text()).toContain('comments')
    expect(vi.mocked(fetch).mock.calls.some(([url]) => String(url).includes('/runs'))).toBe(false)
  })

  it('waits for factory permission to be resolved before requesting runs', async () => {
    const wrapper = await mountDetail()
    expect(wrapper.find('[data-testid="item-runs"]').exists()).toBe(false)
    expect(vi.mocked(fetch).mock.calls.some(([url]) => String(url).includes('/runs'))).toBe(false)

    useOrganizationsStore().organizations = [{ ...organization, canOperateFactory: true }]
    await flushPromises()
    expect(wrapper.get('[data-testid="item-runs"]').text()).toContain('Succeeded')
  })

  it('hides cached runs and stops refetching when factory permission is revoked', async () => {
    const wrapper = await mountDetail(true)
    expect(wrapper.get('[data-testid="item-runs"]').text()).toContain('Succeeded')
    const runRequests = () =>
      vi.mocked(fetch).mock.calls.filter(([url]) => String(url).includes('/runs')).length
    const before = runRequests()

    useOrganizationsStore().organizations[0]!.canOperateFactory = false
    await flushPromises()
    await clients[0]!.invalidateQueries({ queryKey: ['acme', 'PROJ-1', 'runs'] })
    await flushPromises()

    expect(wrapper.find('[data-testid="item-runs"]').exists()).toBe(false)
    expect(wrapper.text()).not.toContain('claude-dev')
    expect(runRequests()).toBe(before)
  })

  it('removes cached factory details from the claim banner when permission is revoked', async () => {
    const original = runs.items[0]!
    runs.items[0] = { ...original, status: 'running', runnerName: 'build-box' }
    Object.assign(item, { claimedBy: original.agentId, claimedAt: new Date().toISOString() })
    try {
      const wrapper = await mountDetail(true)
      const banner = wrapper.getComponent(ClaimBanner)
      expect(banner.props('liveRun')?.runnerName).toBe('build-box')
      expect(banner.find('[data-testid="claim-banner-log-link"]').exists()).toBe(true)

      useOrganizationsStore().organizations[0]!.canOperateFactory = false
      await flushPromises()

      expect(banner.props('liveRun')).toBeNull()
      expect(banner.props('runLogTo')).toBeNull()
      expect(banner.text()).not.toContain('build-box')
      expect(banner.find('[data-testid="claim-banner-log-link"]').exists()).toBe(false)
    } finally {
      runs.items[0] = original
      Object.assign(item, { claimedBy: null, claimedAt: null })
    }
  })

  it('links a run a comment asked for to that comment, and says when it was a follow-up', async () => {
    const original = runs.items[0]!
    runs.items[0] = {
      ...original,
      triggerCommentId: 'c-9',
      followsUpRunId: 'r-0',
    } as typeof original

    const wrapper = await mountDetail(true)

    const link = wrapper
      .findAllComponents(RouterLinkStub)
      .find((entry) => entry.attributes('data-testid') === 'run-trigger-comment')!
    expect(link.text()).toBe('Follow-up from a comment')
    expect(link.props('to')).toEqual({ hash: '#comment-c-9' })

    runs.items[0] = original
  })

  it('labels a stage run with its stage and verdict, and a rework run as rework', async () => {
    const original = runs.items[0]!
    runs.items[0] = {
      ...original,
      kind: 'stage',
      playbookName: 'Code review',
      stageVerdict: 'changes_requested',
    } as typeof original
    runs.items.push({ ...original, id: 'r-2', reworkOfRunId: 'r-1' } as typeof original)

    const wrapper = await mountDetail(true)
    const section = wrapper.find('[data-testid="item-runs"]')

    expect(section.get('[data-testid="run-stage-badge"]').text()).toBe('Stage: Code review')
    expect(section.get('[data-testid="run-stage-verdict"]').text()).toBe('changes requested')
    expect(section.findAll('[data-testid="run-rework-badge"]')).toHaveLength(1)
    expect(section.findAll('[data-testid="run-stage-badge"]')).toHaveLength(1)

    runs.items.splice(0, runs.items.length, original)
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

  it('lets an operator copy the resume command of a recent run with a kept checkout', async () => {
    const original = { ...runs.items[0]! }
    Object.assign(runs.items[0]!, {
      runnerName: 'build-box',
      sessionId: 'sess-1',
      workspacePath: '/srv/runner/r 1/repo',
      finishedAt: new Date().toISOString(),
    })
    const writeText = vi.fn(async () => {})
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true })

    const wrapper = await mountDetail(true)
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
