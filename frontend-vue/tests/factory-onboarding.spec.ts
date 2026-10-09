import { flushPromises, mount } from '@vue/test-utils'
import { createPinia } from 'pinia'
import { ref } from 'vue'
import { afterEach, describe, expect, it, vi } from 'vitest'

import type { Organization } from '@/api/organizations'
import FactoryDocsLink from '@/components/factory/FactoryDocsLink.vue'
import { orgScopeKey, type OrgScope } from '@/composables/useSettingsScope'
import { factoryDocsUrl } from '@/lib/factory'
import FactoryRunnersView from '@/views/factory/FactoryRunnersView.vue'

const organization: Organization = {
  id: 'org-1',
  slug: 'acme',
  name: 'Acme',
  role: 'owner',
  canOperateFactory: true,
  plan: 'trial',
  timeZone: 'UTC',
  weekStart: 'monday',
  membersCanCreateProjects: true,
  createdAt: '2026-01-01T00:00:00Z',
  version: 1,
}

afterEach(() => vi.unstubAllGlobals())

/** Every dialog is rendered inline, so a test can read a step without opening a portal. */
function mountRunners() {
  const scope: OrgScope = {
    slug: ref('acme'),
    record: ref(organization),
    loading: ref(false),
    notFound: ref(false),
    reload: async () => {},
    set: () => {},
  }
  const passthrough = { template: '<div><slot /></div>' }
  return mount(FactoryRunnersView, {
    global: {
      plugins: [createPinia()],
      provide: { [orgScopeKey]: scope },
      stubs: {
        Dialog: passthrough,
        DialogContent: passthrough,
        DialogDescription: passthrough,
        DialogFooter: passthrough,
        DialogHeader: passthrough,
        DialogTitle: passthrough,
      },
    },
  })
}

describe('Factory onboarding', () => {
  it('opens the public Factory guide in a separate tab', () => {
    const wrapper = mount(FactoryDocsLink)
    const link = wrapper.get('a')

    expect(link.attributes('href')).toBe(factoryDocsUrl)
    expect(link.attributes('target')).toBe('_blank')
    expect(link.attributes('rel')).toBe('noopener noreferrer')
  })

  it('shows the machine, harness and repository checklist before registering a runner', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(
        async () =>
          new Response(JSON.stringify([]), {
            status: 200,
            headers: { 'content-type': 'application/json' },
          }),
      ),
    )
    const wrapper = mountRunners()
    await flushPromises()

    const steps = wrapper.get('[data-testid="runner-setup-checklist"]').findAll('li')
    expect(steps).toHaveLength(3)
    expect(steps[0]!.text()).toContain('Prepare the machine')
    expect(steps[1]!.text()).toContain('Sign in and clone')
    expect(steps[2]!.text()).toContain('Register and keep it running')
    expect(wrapper.get(`a[href="${factoryDocsUrl}"]`).text()).toContain('Factory guide')

    wrapper.unmount()
  })

  it('hands out the install-as-service commands beside `runner start`', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(
        async (input: RequestInfo | URL, init?: RequestInit) =>
          new Response(
            JSON.stringify(
              String(input).endsWith('/runners/load')
                ? { runners: [], unassignedQueued: 0, unassignedScheduled: 0 }
                : init?.method === 'POST'
                  ? {
                      runner: {
                        id: 'r1',
                        name: 'vps-1',
                        tokenDisplay: 'jrn_ab…',
                        registeredBy: 'u1',
                        registeredByName: 'Alice',
                        capabilities: null,
                        lastSeenAt: null,
                        isOnline: false,
                        isDisabled: false,
                        createdAt: '2026-01-01T00:00:00Z',
                      },
                      secret: 'jrn_secret',
                    }
                  : [],
            ),
            { status: 200, headers: { 'content-type': 'application/json' } },
          ),
      ),
    )
    const wrapper = mountRunners()
    await flushPromises()

    await wrapper.get('#runner-name').setValue('vps-1')
    await wrapper.get('#register-runner').trigger('submit')
    await flushPromises()

    const step = wrapper.get('[data-testid="runner-service-step"]')
    expect(wrapper.text()).toContain('aictiq runner start')
    expect(step.get('[data-testid="runner-service-command"]').text()).toContain(
      'aictiq runner install-service',
    )

    // The runner rarely lives on the machine reading this page, so every platform is a click away.
    const windows = step.findAll('button').find((button) => button.text() === 'Windows')!
    await windows.trigger('click')
    expect(step.get('[data-testid="runner-service-command"]').text()).toContain(
      'install-runner.ps1',
    )

    wrapper.unmount()
  })
})
