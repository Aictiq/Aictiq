import { describe, expect, it } from 'vitest'
import type { ItemLabelView, WorkflowView } from '../src/api/views.js'
import { CliError, ExitCode } from '../src/errors.js'
import { branchName, findState, projectKeyOf, resolveLabelIds } from '../src/resolve.js'

const label = (id: string, name: string): ItemLabelView => ({ id, name, color: null, group: null })
const labels = [label('l1', 'backend'), label('l2', 'urgent'), label('l3', 'stale')]

describe('projectKeyOf', () => {
  it('takes the key prefix, upper-cased', () => {
    expect(projectKeyOf('acme-123')).toBe('ACME')
    expect(projectKeyOf('  ABC9-4 ')).toBe('ABC9')
  })

  it('refuses something that is not an item key', () => {
    expect(() => projectKeyOf('ACME')).toThrowError(CliError)
    expect(() => projectKeyOf('-7')).toThrowError(CliError)
  })
})

describe('resolveLabelIds', () => {
  it('replaces the whole set when given a plain list', () => {
    expect(resolveLabelIds('backend,urgent', labels, [labels[2]!])).toEqual(['l1', 'l2'])
  })

  it('adds and removes when given +/- adjustments', () => {
    expect(resolveLabelIds('+urgent,-stale', labels, [labels[0]!, labels[2]!])).toEqual([
      'l1',
      'l2',
    ])
  })

  it('refuses a mix, rather than guessing whether the rest were cleared', () => {
    expect(() => resolveLabelIds('backend,+urgent', labels, [])).toThrowError(CliError)
  })

  it('names the unknown label and lists what exists', () => {
    const error = catchCli(() => resolveLabelIds('nope', labels, []))
    expect(error.exitCode).toBe(ExitCode.Validation)
    expect(error.message).toContain('backend, urgent, stale')
  })
})

describe('findState', () => {
  const workflow = {
    states: [
      { id: 's1', name: 'To Do' },
      { id: 's2', name: 'In Review' },
    ],
  } as WorkflowView

  it('matches case-insensitively', () => {
    expect(findState(workflow, 'in review').id).toBe('s2')
  })

  it('lists the available states when the name is wrong', () => {
    const error = catchCli(() => findState(workflow, 'Done'))
    expect(error.exitCode).toBe(ExitCode.Validation)
    expect(error.message).toContain('To Do, In Review')
  })
})

describe('branchName', () => {
  it('puts the key first so commit linking can find the item', () => {
    expect(branchName({ key: 'ACME-123', title: 'Fix the login redirect' })).toBe(
      'acme-123-fix-the-login-redirect',
    )
  })

  it('caps the slug and strips punctuation', () => {
    expect(
      branchName({ key: 'ACME-9', title: 'Make  the  CLI: fast, small & obviously correct!' }),
    ).toBe('acme-9-make-the-cli-fast-small-obviously')
  })

  it('falls back to the bare key when the title has nothing usable', () => {
    expect(branchName({ key: 'ACME-9', title: '???' })).toBe('acme-9')
  })
})

function catchCli(action: () => unknown): CliError {
  try {
    action()
  } catch (error) {
    if (error instanceof CliError) return error
    throw error
  }
  throw new Error('Expected a CliError.')
}
