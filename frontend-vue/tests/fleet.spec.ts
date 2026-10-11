import { enableAutoUnmount, flushPromises, mount, RouterLinkStub } from '@vue/test-utils'
import { createPinia } from 'pinia'
import { ref } from 'vue'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import type { FleetSettings, FleetStatus } from '@/api/fleet'
import type { Organization } from '@/api/organizations'
import FleetTagInput from '@/components/factory/FleetTagInput.vue'
import StartRunDialog from '@/components/factory/StartRunDialog.vue'
import { clearFleetStatusCache } from '@/composables/useFleetStatus'
import { orgScopeKey, type OrgScope } from '@/composables/useSettingsScope'
import {
  durationLabel,
  fleetEnrollCommand,
  fleetWaitingCount,
  isValidFleetTag,
  requiredTagErrors,
  runnerTagChips,
  sameFleetTags,
  unknownFleetTags,
  unknownFleetTagWarning,
  waitedFor,
} from '@/lib/fleet'
import { orgSettingsLinks } from '@/router/paths'
import FactoryRunnersView from '@/views/factory/FactoryRunnersView.vue'
import OrgFleetView from '@/views/settings/OrgFleetView.vue'

/**
 * Fleet mode is opt-in, and while it is off every screen must look and behave exactly as it
 * did before it existed: no tag fields, no tags sent. These specs pin both halves - what fleet
 * mode adds when it is active, and that nothing of it leaks out when it is not.
 */

type Route = [pattern: string, body: unknown, options?: { method?: string; status?: number }]

/** First match wins, so the more specific path goes first. Anything else answers 404. */
function stubFetch(routes: Route[]) {
  const fetchMock = vi.fn(async (input: unknown, init?: RequestInit) => {
    const url = String(input)
    const method = init?.method ?? 'GET'
    for (const [pattern, body, options] of routes) {
      if (!url.includes(pattern) || (options?.method && options.method !== method)) continue
      const status = options?.status ?? 200
      if (status === 204) return new Response(null, { status })
      return new Response(JSON.stringify(body), {
        status,
        headers: {
          'content-type': status >= 400 ? 'application/problem+json' : 'application/json',
        },
      })
    }
    return new Response(JSON.stringify({ title: 'Not found', status: 404 }), {
      status: 404,
      headers: { 'content-type': 'application/problem+json' },
    })
  })
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

const bodyOf = (fetchMock: ReturnType<typeof stubFetch>, pattern: string, method: string) => {
  const call = fetchMock.mock.calls.find(
    ([input, init]) => String(input).includes(pattern) && init?.method === method,
  )
  return call ? JSON.parse(String(call[1]!.body)) : undefined
}

const activeStatus: FleetStatus = {
  availability: 'available',
  enabled: true,
  active: true,
  downgraded: false,
  knownTags: ['gpu-enabled', 'os:linux', 'arch:x64'],
}

const offStatus: FleetStatus = {
  availability: 'available',
  enabled: false,
  active: false,
  downgraded: false,
  knownTags: [],
}

const organization: Organization = {
  id: 'org-1',
  slug: 'acme',
  name: 'Acme',
  role: 'owner',
  canOperateFactory: true,
  plan: 'hosted',
  timeZone: 'UTC',
  weekStart: 'monday',
  membersCanCreateProjects: true,
  createdAt: '2026-01-01T00:00:00Z',
  version: 1,
}

const scope = (): OrgScope => ({
  slug: ref('acme'),
  record: ref(organization),
  loading: ref(false),
  notFound: ref(false),
  reload: async () => {},
  set: () => {},
})

const passthrough = { template: '<div><slot /></div>' }
const dialogStubs = {
  Dialog: passthrough,
  DialogContent: passthrough,
  DialogDescription: passthrough,
  DialogFooter: passthrough,
  DialogHeader: passthrough,
  DialogTitle: passthrough,
}

enableAutoUnmount(afterEach)
beforeEach(() => clearFleetStatusCache())
afterEach(() => vi.unstubAllGlobals())

describe('fleet presentation rules', () => {
  it('accepts the tags the API accepts', () => {
    expect(isValidFleetTag('gpu-enabled')).toBe(true)
    expect(isValidFleetTag('os:linux')).toBe(true)
    expect(isValidFleetTag('Gpu')).toBe(false)
    expect(isValidFleetTag('-gpu')).toBe(false)
    expect(isValidFleetTag('a'.repeat(51))).toBe(false)
  })

  it('warns about tags no runner has ever had', () => {
    expect(unknownFleetTags(['gpu-enabled', 'tpu'], ['gpu-enabled'])).toEqual(['tpu'])
    expect(unknownFleetTagWarning('tpu')).toBe(
      'No runner has ever had the tag `tpu`. Runs that require it will wait until one does.',
    )
    expect(sameFleetTags(['a', 'b'], ['b', 'a'])).toBe(true)
    expect(sameFleetTags(['a'], ['a', 'b'])).toBe(false)
  })

  it('says how long the oldest run has waited', () => {
    const now = new Date('2026-10-11T12:00:00Z')
    expect(waitedFor(null, now)).toBe('')
    expect(waitedFor('2026-10-11T11:59:30Z', now)).toBe('under a minute')
    expect(waitedFor('2026-10-11T11:56:00Z', now)).toBe('4 min')
    expect(waitedFor('2026-10-11T09:55:00Z', now)).toBe('2 h 5 min')
    expect(waitedFor('2026-10-08T12:00:00Z', now)).toBe('3 d')
    expect(durationLabel(30)).toBe('30 s')
    expect(durationLabel(300)).toBe('5 min')
    expect(durationLabel(5400)).toBe('1 h 30 min')
  })

  it('counts every waiting run, for any runner and for one', () => {
    expect(fleetWaitingCount(null)).toBe(0)
    expect(
      fleetWaitingCount({ unassignedQueued: 2, runners: [{ queued: 1 }, { queued: 3 }] }),
    ).toBe(6)
  })

  it('says where each of a runner tag comes from, editable ones first', () => {
    expect(
      runnerTagChips({
        tags: ['team-a'],
        effectiveTags: ['os:linux', 'gpu-enabled', 'team-a'],
        capabilities: { tags: ['gpu-enabled'] },
      }),
    ).toEqual([
      { tag: 'team-a', source: 'admin' },
      { tag: 'gpu-enabled', source: 'cli' },
      { tag: 'os:linux', source: 'platform' },
    ])
  })

  it('collects required-tag messages however the API keys them', () => {
    expect(
      requiredTagErrors({
        requiredTags: ['Too many.'],
        'requiredTags[1]': ['Bad tag.'],
        name: ['x'],
      }),
    ).toEqual(['Too many.', 'Bad tag.'])
  })

  it('builds the ephemeral runner command', () => {
    expect(fleetEnrollCommand('https://app.aictiq.com', 'jfe_abc')).toBe(
      'aictiq runner enroll --url https://app.aictiq.com --token jfe_abc --tag gpu-enabled && aictiq runner start --idle-exit 10',
    )
  })

  it('adds the Fleet tab only when asked', () => {
    expect(orgSettingsLinks('acme').map((link) => link.label)).not.toContain('Fleet')
    expect(orgSettingsLinks('acme', { fleet: true })).toContainEqual({
      to: '/o/acme/settings/fleet',
      label: 'Fleet',
    })
  })
})

describe('FleetTagInput', () => {
  function mountInput(props: Record<string, unknown> = {}) {
    const wrapper = mount(FleetTagInput, {
      props: {
        modelValue: [] as string[],
        'onUpdate:modelValue': (tags: string[]) => wrapper.setProps({ modelValue: tags }),
        ...props,
      },
    })
    return wrapper
  }

  it('adds on Enter and comma, lower-cased, and takes the last back on Backspace', async () => {
    const wrapper = mountInput()
    const input = wrapper.get('[data-testid="fleet-tags-input"]')

    await input.setValue('GPU-Enabled')
    await input.trigger('keydown', { key: 'Enter' })
    await input.setValue('os:linux')
    await input.trigger('keydown', { key: ',' })
    expect(wrapper.props('modelValue')).toEqual(['gpu-enabled', 'os:linux'])
    expect(wrapper.findAll('[data-testid="fleet-tags-chip"]')).toHaveLength(2)

    await input.setValue('')
    await input.trigger('keydown', { key: 'Backspace' })
    expect(wrapper.props('modelValue')).toEqual(['gpu-enabled'])
  })

  it('refuses a tag the API would refuse, and says why', async () => {
    const wrapper = mountInput()
    const input = wrapper.get('[data-testid="fleet-tags-input"]')
    await input.setValue('not ok')
    await input.trigger('keydown', { key: 'Enter' })

    expect(wrapper.props('modelValue')).toEqual([])
    expect(wrapper.find('[data-testid="fleet-tags-error"]').exists()).toBe(true)
  })

  it('warns, without blocking, about a tag no runner has had', async () => {
    const wrapper = mountInput({
      modelValue: ['gpu-enabled', 'tpu'],
      suggestions: ['gpu-enabled'],
      warnUnknown: true,
    })
    const warnings = wrapper.findAll('[data-testid="fleet-tags-unknown"]')
    expect(warnings).toHaveLength(1)
    expect(warnings[0]!.text()).toContain('`tpu`')
  })
})

describe('StartRunDialog in fleet mode', () => {
  const playbook = {
    id: 'p1',
    projectId: 'pr1',
    name: 'Train',
    wikiPageId: 'w1',
    harness: 'claude',
    onSuccessStateId: null,
    onFailureStateId: null,
    maxMinutes: 60,
    isDefault: true,
    requiredTags: ['os:linux'],
    createdBy: 'u1',
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    version: 1,
  }
  const agent = {
    userId: 'a1',
    displayName: 'claude-dev',
    email: null,
    ownerUserId: 'u9',
    ownerName: 'Rona',
    role: 'member',
    isActive: true,
    createdAt: '2026-01-01T00:00:00Z',
    lastActiveAt: null,
    tokenCount: 1,
  }
  const member = { userId: 'a1', displayName: 'claude-dev', isAgent: true, role: 'member' }
  const run = { id: 'r1', status: 'queued', harness: 'claude', scheduledFor: null }

  beforeEach(() =>
    vi.stubGlobal('localStorage', {
      getItem: () => null,
      setItem: () => {},
      removeItem: () => {},
    }),
  )

  const routes = (status: unknown): Route[] => [
    ['/fleet/status', status],
    ['/items/PROJ-1/runs', run, { method: 'POST' }],
    ['/playbooks', [playbook]],
    ['/agents', [agent]],
    ['/factory-settings', { defaultAgentId: null }],
    ['/members', [member]],
  ]

  async function mountDialog() {
    const wrapper = mount(StartRunDialog, {
      props: { slug: 'acme', projectKey: 'PROJ', itemKey: 'PROJ-1', open: true },
      global: { stubs: dialogStubs },
    })
    await flushPromises()
    return wrapper
  }

  it('shows the playbook tags as required and sends only the added ones', async () => {
    const fetchMock = stubFetch(routes(activeStatus))
    const wrapper = await mountDialog()

    const locked = wrapper.findAll('[data-testid="start-run-required-tags-locked"]')
    expect(locked.map((chip) => chip.text())).toEqual(['os:linux'])

    const input = wrapper.get('[data-testid="start-run-required-tags-input"]')
    await input.setValue('tpu')
    await input.trigger('keydown', { key: 'Enter' })
    expect(wrapper.get('[data-testid="start-run-required-tags-unknown"]').text()).toContain('`tpu`')

    await wrapper.get('form#start-run').trigger('submit')
    await flushPromises()
    expect(bodyOf(fetchMock, '/items/PROJ-1/runs', 'POST')).toMatchObject({
      playbookId: 'p1',
      requiredTags: ['tpu'],
    })
  })

  it('shows and sends nothing of it while fleet mode is off', async () => {
    const fetchMock = stubFetch(routes(offStatus))
    const wrapper = await mountDialog()

    expect(wrapper.find('[data-testid="start-run-required-tags"]').exists()).toBe(false)
    await wrapper.get('form#start-run').trigger('submit')
    await flushPromises()
    expect(bodyOf(fetchMock, '/items/PROJ-1/runs', 'POST')).not.toHaveProperty('requiredTags')
  })

  it('treats a server without fleet mode as off', async () => {
    const fetchMock = stubFetch(routes(offStatus).slice(1))
    const wrapper = await mountDialog()

    expect(wrapper.find('[data-testid="start-run-required-tags"]').exists()).toBe(false)
    await wrapper.get('form#start-run').trigger('submit')
    await flushPromises()
    expect(bodyOf(fetchMock, '/items/PROJ-1/runs', 'POST')).not.toHaveProperty('requiredTags')
  })
})

describe('FactoryRunnersView in fleet mode', () => {
  const runner = {
    id: 'r1',
    name: 'gpu-1',
    tokenDisplay: 'jrn_a1b2',
    registeredBy: 'u1',
    registeredByName: 'Ana',
    capabilities: {
      v: 1,
      harnesses: [{ name: 'claude', version: '2.0' }],
      os: 'linux',
      arch: 'x64',
      cliVersion: '1.0.0',
      maxParallel: 4,
      tags: ['gpu-enabled'],
    },
    lastSeenAt: new Date().toISOString(),
    isOnline: true,
    isDisabled: false,
    createdAt: '2026-01-01T00:00:00Z',
    tags: ['team-a'],
    effectiveTags: ['team-a', 'gpu-enabled', 'os:linux', 'arch:x64'],
    ephemeral: true,
  }
  const load = {
    runners: [
      {
        runnerId: 'r1',
        running: 1,
        queued: 2,
        scheduled: 0,
        nextScheduledFor: null,
        active: [],
      },
    ],
    unassignedQueued: 3,
    unassignedScheduled: 0,
    nextUnassignedScheduledFor: null,
    oldestQueuedSince: new Date(Date.now() - 4 * 60_000 - 5_000).toISOString(),
  }

  const routes = (status: unknown): Route[] => [
    ['/fleet/status', status],
    ['/runners/load', load],
    ['/runners/r1', { ...runner, tags: ['team-a', 'linux-box'] }, { method: 'PATCH' }],
    ['/runners', [runner]],
  ]

  async function mountRunners() {
    const wrapper = mount(FactoryRunnersView, {
      global: {
        plugins: [createPinia()],
        provide: { [orgScopeKey]: scope() },
        stubs: { ...dialogStubs, RouterLink: RouterLinkStub },
      },
    })
    await flushPromises()
    return wrapper
  }

  it('shows tags, slots, the ephemeral badge and the queue', async () => {
    stubFetch(routes(activeStatus))
    const wrapper = await mountRunners()

    expect(wrapper.get('[data-testid="runner-tag-admin"]').text()).toBe('team-a')
    expect(wrapper.get('[data-testid="runner-tag-cli"]').text()).toBe('gpu-enabled')
    expect(wrapper.findAll('[data-testid="runner-tag-platform"]').map((tag) => tag.text())).toEqual(
      ['os:linux', 'arch:x64'],
    )
    expect(wrapper.find('[data-testid="runner-ephemeral"]').exists()).toBe(true)
    expect(wrapper.get('[data-testid="runner-slots"]').text()).toBe('1 / 4 running')

    const queue = wrapper.get('[data-testid="fleet-queue"]')
    expect(queue.get('[data-testid="fleet-queue-waiting"]').text()).toBe('5')
    expect(queue.get('[data-testid="fleet-queue-oldest"]').text()).toContain('4 min')
  })

  it('saves the admin tags, leaving the CLI and platform ones alone', async () => {
    const fetchMock = stubFetch(routes(activeStatus))
    const wrapper = await mountRunners()

    await wrapper.get('[data-testid="runner-edit-tags"]').trigger('click')
    expect(
      wrapper.findAll('[data-testid="runner-tags-input-locked"]').map((chip) => chip.text()),
    ).toEqual(['gpu-enabled', 'os:linux', 'arch:x64'])
    const input = wrapper.get('[data-testid="runner-tags-input-input"]')
    await input.setValue('linux-box')
    await input.trigger('keydown', { key: 'Enter' })
    await wrapper.get('form#runner-tags').trigger('submit')
    await flushPromises()

    expect(bodyOf(fetchMock, '/runners/r1', 'PATCH')).toEqual({ tags: ['team-a', 'linux-box'] })
  })

  it('renders exactly as before while fleet mode is off', async () => {
    stubFetch(routes(offStatus))
    const wrapper = await mountRunners()

    for (const id of [
      'runner-tags',
      'runner-ephemeral',
      'runner-slots',
      'runner-edit-tags',
      'fleet-queue',
      'fleet-downgraded',
    ]) {
      expect(wrapper.find(`[data-testid="${id}"]`).exists(), id).toBe(false)
    }
    expect(wrapper.get('[data-testid="runner-load"]').text()).toContain('1 / 4 running')
  })

  it('says so when the plan no longer includes fleet mode', async () => {
    stubFetch(routes({ ...activeStatus, availability: 'upgrade', active: false, downgraded: true }))
    const wrapper = await mountRunners()

    expect(wrapper.find('[data-testid="fleet-downgraded"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="runner-tags"]').exists()).toBe(false)
  })
})

describe('OrgFleetView', () => {
  const settings = (overrides: Partial<FleetSettings> = {}): FleetSettings => ({
    availability: 'available',
    enabled: false,
    active: false,
    downgraded: false,
    scaleWebhookUrl: null,
    hasWebhookSecret: false,
    scaleAfterSeconds: 120,
    scaleCooldownSeconds: 300,
    lastScaleCallAt: null,
    lastScaleCallError: null,
    enrollTokenDisplay: null,
    ...overrides,
  })

  async function mountFleet() {
    const wrapper = mount(OrgFleetView, {
      global: {
        provide: { [orgScopeKey]: scope() },
        stubs: { RouterLink: RouterLinkStub },
      },
    })
    await flushPromises()
    return wrapper
  }

  it('offers the upgrade instead of the switch on a plan without fleet mode', async () => {
    stubFetch([['/fleet', settings({ availability: 'upgrade' })]])
    const wrapper = await mountFleet()

    expect(wrapper.get('[data-testid="fleet-switch"]').attributes('disabled')).toBeDefined()
    expect(wrapper.getComponent(RouterLinkStub).props('to')).toBe('/o/acme/settings/billing')
    expect(wrapper.find('[data-testid="fleet-scale-form"]').exists()).toBe(false)
  })

  it('turns it on, then offers scale from zero and ephemeral runners', async () => {
    const on = settings({ enabled: true, active: true })
    const fetchMock = stubFetch([
      ['/fleet/status', activeStatus],
      ['/fleet', { settings: on, webhookSecret: null }, { method: 'PUT' }],
      ['/fleet', settings()],
    ])
    const wrapper = await mountFleet()

    await wrapper.get('[data-testid="fleet-switch"]').trigger('click')
    await flushPromises()

    expect(bodyOf(fetchMock, '/fleet', 'PUT')).toEqual({ enabled: true })
    expect(wrapper.get('[data-testid="fleet-switch"]').attributes('aria-checked')).toBe('true')
    expect(wrapper.find('[data-testid="fleet-scale-form"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="fleet-enroll"]').exists()).toBe(true)
  })

  it('shows the signing secret once, after the first URL is saved', async () => {
    const saved = settings({
      enabled: true,
      active: true,
      scaleWebhookUrl: 'https://scaler.example.com/hook',
      hasWebhookSecret: true,
    })
    const fetchMock = stubFetch([
      ['/fleet', { settings: saved, webhookSecret: 'whsec_once' }, { method: 'PUT' }],
      ['/fleet', settings({ enabled: true, active: true })],
    ])
    const wrapper = await mountFleet()

    await wrapper
      .get('[data-testid="fleet-webhook-url"]')
      .setValue('https://scaler.example.com/hook')
    await wrapper.get('[data-testid="fleet-scale-after"]').setValue('3')
    await wrapper.get('[data-testid="fleet-scale-form"]').trigger('submit')
    await flushPromises()

    expect(bodyOf(fetchMock, '/fleet', 'PUT')).toEqual({
      scaleWebhookUrl: 'https://scaler.example.com/hook',
      scaleAfterSeconds: 180,
      scaleCooldownSeconds: 300,
    })
    expect(wrapper.get('[data-testid="fleet-webhook-secret-value"]').text()).toBe('whsec_once')
    expect(wrapper.get('[data-testid="fleet-webhook-secret"]').text()).toContain(
      'X-Aictiq-Signature: sha256=',
    )
  })

  it('mints an enrollment token, shows it once, and puts it in the command', async () => {
    stubFetch([
      ['/fleet/enroll-token', { secret: 'jfe_secret', display: 'jfe_secr…' }, { method: 'POST' }],
      ['/fleet', settings({ enabled: true, active: true })],
    ])
    const wrapper = await mountFleet()

    await wrapper.get('[data-testid="fleet-enroll-mint"]').trigger('click')
    await flushPromises()

    expect(wrapper.get('[data-testid="fleet-enroll-token-value"]').text()).toBe('jfe_secret')
    expect(wrapper.get('[data-testid="fleet-enroll-command"]').text()).toContain(
      '--token jfe_secret --tag gpu-enabled && aictiq runner start --idle-exit 10',
    )
    expect(wrapper.find('[data-testid="fleet-enroll-revoke"]').exists()).toBe(true)
  })

  it('says it is not available on an instance that does not offer it', async () => {
    stubFetch([])
    const wrapper = await mountFleet()

    expect(wrapper.text()).toContain('Fleet mode is not available here')
    expect(wrapper.find('[data-testid="fleet-switch"]').exists()).toBe(false)
  })
})
