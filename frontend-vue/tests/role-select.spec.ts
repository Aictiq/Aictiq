import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'

import { canAssignRole, orgRoles } from '@/api/members'
import type { OrgRole } from '@/api/organizations'
import { projectRoles } from '@/api/projects'
import RoleSelect from '@/components/common/RoleSelect.vue'

/**
 * The role dropdown is the one control in the app that hands out authority, so what it
 * *offers* has to match what `MembershipRules` will actually accept. Being more permissive
 * than the server means showing people a choice that answers 403; being less permissive
 * means an owner who cannot promote anyone.
 *
 * These assertions pair the component with the real `canAssignRole` mirror rather than a
 * stub, because a component that gates correctly against a wrong rule is no use.
 */

function render(props: InstanceType<typeof RoleSelect>['$props']) {
  return mount(RoleSelect, { props })
}

function disabledRoles(wrapper: ReturnType<typeof render>) {
  return wrapper
    .findAll('option')
    .filter((option) => option.attributes('disabled') !== undefined)
    .map((option) => option.attributes('value'))
}

function isOrgRole(role: string): role is OrgRole {
  return orgRoles.includes(role as OrgRole)
}

function orgCanAssign(actor: OrgRole, target: OrgRole) {
  return (role: string) => isOrgRole(role) && canAssignRole(actor, target, role)
}

describe('RoleSelect', () => {
  it('offers every role to an owner', () => {
    const wrapper = render({
      modelValue: 'member',
      roles: orgRoles,
      canAssign: orgCanAssign('owner', 'member'),
      label: 'Role',
    })

    expect(wrapper.findAll('option')).toHaveLength(4)
    expect(disabledRoles(wrapper)).toEqual([])
  })

  it('keeps an admin from handing out admin or owner', () => {
    const wrapper = render({
      modelValue: 'member',
      roles: orgRoles,
      canAssign: orgCanAssign('admin', 'member'),
      label: 'Role',
    })

    expect(disabledRoles(wrapper)).toEqual(['owner', 'admin'])
  })

  it('locks the control entirely when the target outranks the actor', () => {
    // An admin acting on a peer: `canAssignRole` refuses every role, including the one
    // already set, so there is nothing to choose between and the select says so.
    const wrapper = render({
      modelValue: 'admin',
      roles: orgRoles,
      canAssign: orgCanAssign('admin', 'admin'),
      label: 'Role',
    })

    expect(wrapper.find('select').attributes('disabled')).toBeDefined()
  })

  it('always lists the role that is already set, so the control never renders blank', () => {
    const wrapper = render({
      modelValue: 'owner',
      roles: orgRoles,
      canAssign: orgCanAssign('admin', 'owner'),
      label: 'Role',
    })

    const current = wrapper.findAll('option').find((option) => option.attributes('value') === 'owner')
    expect(current).toBeDefined()
    expect(current!.attributes('disabled')).toBeUndefined()
  })

  it('emits the picked role', async () => {
    const wrapper = render({
      modelValue: 'member',
      roles: orgRoles,
      canAssign: () => true,
      label: 'Role',
    })

    await wrapper.find('select').setValue('guest')

    expect(wrapper.emitted('update:modelValue')).toEqual([['guest']])
  })

  it('stays shut when the caller disables it, whatever the role rules say', () => {
    const wrapper = render({
      modelValue: 'member',
      roles: projectRoles,
      canAssign: () => true,
      disabled: true,
      label: 'Project role',
    })

    expect(wrapper.find('select').attributes('disabled')).toBeDefined()
  })

  it('carries a label, because the visible text is only the role name', () => {
    const wrapper = render({
      modelValue: 'guest',
      roles: projectRoles,
      label: 'Role for Ada Lovelace',
    })

    expect(wrapper.find('select').attributes('aria-label')).toBe('Role for Ada Lovelace')
  })
})
