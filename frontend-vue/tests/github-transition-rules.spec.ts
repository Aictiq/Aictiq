import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'

import GitHubTransitionRulesEditor from '@/components/settings/GitHubTransitionRulesEditor.vue'

const binding = {
  id: 'binding-1',
  repoId: 42,
  installationId: 7,
  fullName: 'acme/web',
  createdAt: '2026-01-01T00:00:00Z',
  onPullRequestOpenedStateId: 'active',
  onPullRequestMergedStateId: 'resolved',
}
const states = [
  {
    id: 'new',
    name: 'New',
    category: 'proposed' as const,
    position: 0,
    color: null,
    isInitial: true,
  },
  {
    id: 'active',
    name: 'Active',
    category: 'active' as const,
    position: 1,
    color: null,
    isInitial: false,
  },
  {
    id: 'resolved',
    name: 'Resolved',
    category: 'resolved' as const,
    position: 2,
    color: null,
    isInitial: false,
  },
]

describe('GitHub transition rules editor', () => {
  it('echoes saved rules and emits null when an optional rule is cleared', async () => {
    const wrapper = mount(GitHubTransitionRulesEditor, { props: { binding, states } })
    const selects = wrapper.findAll('select')
    expect((selects[0]!.element as HTMLSelectElement).value).toBe('active')
    expect((selects[1]!.element as HTMLSelectElement).value).toBe('resolved')

    await selects[0]!.setValue('')
    await selects[1]!.setValue('active')
    await wrapper.find('form').trigger('submit')

    expect(wrapper.emitted('save')).toEqual([
      [{ onPullRequestOpenedStateId: null, onPullRequestMergedStateId: 'active' }],
    ])
  })
})
