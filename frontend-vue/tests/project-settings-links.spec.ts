import { describe, expect, it } from 'vitest'

import { projectSettingsLinks } from '@/router/paths'

/** Admins configure a project; a member may only import into it; a guest has no tabs. */
describe('projectSettingsLinks', () => {
  const labels = (role: 'admin' | 'member' | 'guest') =>
    projectSettingsLinks('acme', 'DSO', role).map((link) => link.label)

  it('gives an admin every tab, the Jira import last', () => {
    expect(labels('admin')[0]).toBe('General')
    expect(labels('admin').at(-1)).toBe('Import from Jira')
  })

  it('gives a member only the Jira import', () => {
    expect(projectSettingsLinks('acme', 'DSO', 'member')).toEqual([
      { to: '/o/acme/p/DSO/settings/import', label: 'Import from Jira' },
    ])
  })

  it('gives a guest nothing', () => {
    expect(labels('guest')).toEqual([])
  })
})
