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
    const scope: OrgScope = {
      slug: ref('acme'),
      record: ref(organization),
      loading: ref(false),
      notFound: ref(false),
      reload: async () => {},
      set: () => {},
    }
    const passthrough = { template: '<div><slot /></div>' }
    const wrapper = mount(FactoryRunnersView, {
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
    await flushPromises()

    const steps = wrapper.get('[data-testid="runner-setup-checklist"]').findAll('li')
    expect(steps).toHaveLength(3)
    expect(steps[0]!.text()).toContain('Prepare the machine')
    expect(steps[1]!.text()).toContain('Sign in and clone')
    expect(steps[2]!.text()).toContain('Register and keep it running')
    expect(wrapper.get(`a[href="${factoryDocsUrl}"]`).text()).toContain('Factory guide')

    wrapper.unmount()
  })
})
