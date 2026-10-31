import { flushPromises, mount } from '@vue/test-utils'
import { createPinia } from 'pinia'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ref } from 'vue'
import { createMemoryHistory, createRouter } from 'vue-router'

import { PLAN_LIMIT } from '@/api/billing'
import { getFactorySettings, updateFactorySettings, type FactorySettings } from '@/api/playbooks'
import type { RunRepositoryStep } from '@/api/runs'
import RepositoryStepsEditor from '@/components/factory/RepositoryStepsEditor.vue'
import RunRepositorySteps from '@/components/factory/RunRepositorySteps.vue'
import { projectScopeKey } from '@/composables/useSettingsScope'
import {
  aliasProblem,
  multiRepoErrors,
  pullRequestLabel,
  workingDirectoryProblem,
} from '@/lib/repositories'
import { useOrganizationsStore } from '@/stores/organizations'
import { ApiError } from '@/utils/api'
import ProjectFactoryView from '@/views/settings/ProjectFactoryView.vue'

vi.mock('@/api/playbooks', () => ({
  getFactorySettings: vi.fn(),
  updateFactorySettings: vi.fn(),
}))
vi.mock('@/api/github', () => ({ listGitHubBindings: vi.fn(async () => []) }))
vi.mock('@/api/projects', () => ({ listProjectMembers: vi.fn(async () => []) }))
vi.mock('@/api/agents', () => ({ listAgents: vi.fn(async () => []) }))
vi.mock('@/api/runners', () => ({ listRunnerChoices: vi.fn(async () => []) }))

/**
 * Multiple repositories stay out of the way of the single-repository majority: one line until
 * someone opts in, nothing at all where the plan can never allow it.
 */
const single: FactorySettings = {
  projectId: 'p1',
  repoSource: 1,
  repoFullName: null,
  defaultBranch: 'main',
  localPathHint: '/srv/repos/backend',
  defaultAgentId: null,
  updatedAt: null,
  version: 1,
  multiRepoEnabled: false,
  multiRepoAvailability: 'available',
  primaryAlias: 'backend',
  primaryWorkingDirectory: null,
  additionalRepositories: [],
}
const path = '/o/acme/p/WEB/settings/factory'
const wrappers: ReturnType<typeof mount>[] = []

beforeEach(() => {
  vi.clearAllMocks()
})

afterEach(() => {
  while (wrappers.length) wrappers.pop()!.unmount()
})

async function mountFactory(settings: FactorySettings) {
  vi.mocked(getFactorySettings).mockResolvedValue(settings)
  const pinia = createPinia()
  useOrganizationsStore(pinia).organizations = [
    { id: 'org-1', slug: 'acme', name: 'Acme', role: 'member', canOperateFactory: false },
  ]
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [{ path, component: { template: '<div />' } }],
  })
  await router.push(path)
  const wrapper = mount(ProjectFactoryView, {
    global: {
      plugins: [pinia, router],
      provide: {
        [projectScopeKey as symbol]: {
          slug: ref('acme'),
          projectKey: ref('WEB'),
          record: ref({ role: 'admin', isArchived: false }),
        },
      },
      stubs: { RefinementSettingsForm: true },
    },
  })
  wrappers.push(wrapper)
  await flushPromises()
  return wrapper
}

describe('the multiple repositories note', () => {
  it('is absent where the plan can never allow it', async () => {
    const wrapper = await mountFactory({ ...single, multiRepoAvailability: 'hidden' })
    expect(wrapper.find('[data-testid="multi-repo"]').exists()).toBe(false)
  })

  it('is absent on an older server that does not report it', async () => {
    const wrapper = await mountFactory({ ...single, multiRepoAvailability: undefined })
    expect(wrapper.find('[data-testid="multi-repo"]').exists()).toBe(false)
  })

  it('offers the upgrade on a free plan, with no switch', async () => {
    const wrapper = await mountFactory({ ...single, multiRepoAvailability: 'upgrade' })
    const note = wrapper.get('[data-testid="multi-repo-note"]')
    expect(note.text()).toContain('supported on paid plans')
    expect(note.get('[data-testid="multi-repo-upgrade-link"]').attributes('href')).toBe(
      '/o/acme/settings/billing',
    )
    expect(wrapper.find('[data-testid="multi-repo-switch"]').exists()).toBe(false)
  })

  it('saves exactly what it always did while the switch stays off', async () => {
    vi.mocked(updateFactorySettings).mockImplementation(async (_slug, _key, body) => ({
      ...single,
      ...body,
      version: 2,
    }))
    const wrapper = await mountFactory({ ...single, multiRepoAvailability: 'upgrade' })
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    const body = vi.mocked(updateFactorySettings).mock.calls[0]![2]
    expect(body).not.toHaveProperty('multiRepoEnabled')
    expect(body).not.toHaveProperty('additionalRepositories')
  })

  it('turns on, adds a repository and saves the list', async () => {
    vi.mocked(updateFactorySettings).mockImplementation(async (_slug, _key, body) => ({
      ...single,
      ...body,
      version: 2,
    }))
    const wrapper = await mountFactory(single)
    expect(wrapper.get('[data-testid="multi-repo-note"]').text()).toContain(
      'Monorepos and multiple repositories are supported.',
    )
    await wrapper.get('[data-testid="multi-repo-turn-on"]').trigger('click')
    expect(wrapper.get('[data-testid="multi-repo-switch"]').element).toHaveProperty('checked', true)
    expect((wrapper.get('#multi-repo-primary-alias').element as HTMLInputElement).value).toBe(
      'backend',
    )

    await wrapper.get('#multi-repo-primary-directory').setValue('packages/api')
    await wrapper.get('[data-testid="multi-repo-add"]').trigger('click')
    await wrapper.get('#multi-repo-alias-0').setValue('frontend')
    await wrapper.get('#multi-repo-source-0').setValue('1')
    await wrapper.get('#multi-repo-path-0').setValue('/srv/repos/frontend')
    expect(wrapper.text()).toContain('aictiq runner map WEB/frontend /srv/repos/frontend')

    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(vi.mocked(updateFactorySettings).mock.calls[0]![2]).toMatchObject({
      multiRepoEnabled: true,
      primaryAlias: 'backend',
      primaryWorkingDirectory: 'packages/api',
      additionalRepositories: [
        {
          alias: 'frontend',
          repoSource: 1,
          repoFullName: null,
          localPathHint: '/srv/repos/frontend',
          defaultBranch: 'main',
          workingDirectory: null,
        },
      ],
    })
  })

  it('shows a plan refusal inside the section', async () => {
    const detail = 'Multiple repositories are part of the paid Hosted plan.'
    vi.mocked(updateFactorySettings).mockRejectedValue(
      new ApiError(402, 'Plan limit', {
        type: PLAN_LIMIT,
        title: 'Plan limit',
        status: 402,
        detail,
        limit: 'multi_repo',
      }),
    )
    const wrapper = await mountFactory(single)
    await wrapper.get('[data-testid="multi-repo-turn-on"]').trigger('click')
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(wrapper.get('[data-testid="multi-repo-errors"]').text()).toContain(detail)
  })

  it('keeps a save off while an alias collides', async () => {
    const wrapper = await mountFactory({
      ...single,
      multiRepoEnabled: true,
      additionalRepositories: [
        {
          alias: 'backend',
          repoSource: 1,
          repoFullName: null,
          localPathHint: '/x',
          defaultBranch: 'main',
          workingDirectory: null,
        },
      ],
    })
    expect(wrapper.text()).toContain('Another repository already uses this alias.')
    expect(wrapper.get('button[type="submit"]').attributes('disabled')).toBeDefined()
  })

  it('says a downgraded project falls back to its primary repository and keeps its setup', async () => {
    const wrapper = await mountFactory({
      ...single,
      multiRepoEnabled: true,
      multiRepoAvailability: 'upgrade',
    })
    expect(wrapper.get('[data-testid="multi-repo-paused"]').text()).toContain(
      'new runs use the primary repository only',
    )
    expect(wrapper.get('#multi-repo-primary-alias').attributes('disabled')).toBeDefined()
  })
})

describe('a playbook repository steps editor', () => {
  it('adds steps from the project aliases and orders one after another', async () => {
    const steps = ref(
      [] as {
        alias: string
        workingDirectory: string | null
        after: string[]
        waitForMerge: boolean
      }[],
    )
    const wrapper = mount(RepositoryStepsEditor, {
      props: {
        aliases: ['backend', 'frontend'],
        errors: [],
        modelValue: steps.value,
        'onUpdate:modelValue': (value: typeof steps.value) => {
          steps.value = value
          void wrapper.setProps({ modelValue: value })
        },
      },
    })
    await wrapper.get('[data-testid="playbook-repository-add"]').trigger('click')
    await wrapper.get('[data-testid="playbook-repository-add"]').trigger('click')
    expect(steps.value.map((step) => step.alias)).toEqual(['backend', 'frontend'])
    expect(wrapper.find('[data-testid="playbook-repository-add"]').exists()).toBe(false)

    const second = wrapper.findAll('[data-testid="playbook-repository-step"]')[1]!
    await second.get('input[type="checkbox"]').setValue(true)
    expect(steps.value[1]!.after).toEqual(['backend'])
    await second.findAll('input[type="checkbox"]')[1]!.setValue(true)
    expect(steps.value[1]!.waitForMerge).toBe(true)

    await wrapper
      .findAll('[data-testid="playbook-repository-step"]')[0]!
      .get('button')
      .trigger('click')
    expect(steps.value).toEqual([
      { alias: 'frontend', workingDirectory: null, after: [], waitForMerge: true },
    ])
  })
})

describe('a run repository steps list', () => {
  it('shows each step with its state, what it waits for and its pull requests', async () => {
    const steps: RunRepositoryStep[] = [
      {
        aliases: ['backend'],
        after: [],
        waitForMerge: false,
        state: 'succeeded',
        runId: 'r1',
        pullRequestUrls: ['https://github.com/acme/backend/pull/7'],
      },
      {
        aliases: ['frontend'],
        after: ['backend'],
        waitForMerge: true,
        state: 'waiting-for-merge',
        runId: null,
        pullRequestUrls: [],
      },
    ]
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [{ path: '/:p(.*)', component: { template: '<div />' } }],
    })
    const wrapper = mount(RunRepositorySteps, {
      props: { slug: 'acme', steps, currentRunId: 'r1' },
      global: { plugins: [router] },
    })
    const rows = wrapper.findAll('[data-testid="run-repository-step"]')
    expect(rows[0]!.text()).toContain('Succeeded')
    expect(rows[0]!.text()).toContain('acme/backend#7')
    expect(rows[0]!.text()).toContain('This run')
    expect(rows[1]!.text()).toContain('Waiting for merge')
    expect(rows[1]!.text()).toContain('after backend is merged')
  })
})

describe('multiple repository rules', () => {
  it('checks aliases and working directories the way the API does', () => {
    expect(aliasProblem('backend-service', [])).toBeNull()
    expect(aliasProblem('Backend', [])).not.toBeNull()
    expect(aliasProblem('api', ['api'])).toContain('already uses')
    expect(workingDirectoryProblem('packages/api')).toBeNull()
    expect(workingDirectoryProblem('')).toBeNull()
    expect(workingDirectoryProblem('/etc')).not.toBeNull()
    expect(workingDirectoryProblem('packages/../..')).not.toBeNull()
  })

  it('picks the multi-repository messages out of a validation problem', () => {
    expect(
      multiRepoErrors({
        multiRepoEnabled: ['Multiple repositories need a paid hosted plan.'],
        'additionalRepositories[0].alias': ['Taken.'],
        defaultBranch: ['Other.'],
      }),
    ).toEqual(['Multiple repositories need a paid hosted plan.', 'Taken.'])
  })

  it('labels GitHub pull requests by repository and number', () => {
    expect(pullRequestLabel('https://github.com/acme/web/pull/12')).toBe('acme/web#12')
    expect(pullRequestLabel('https://example.com/x')).toBe('https://example.com/x')
  })
})
