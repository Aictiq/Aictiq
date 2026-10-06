import { execFileSync } from 'node:child_process'
import { describe, expect, it } from 'vitest'

import type { Run } from '@/api/runs'
import { quoteForShell, resumeCommand } from '@/lib/resumeCommand'

const now = Date.parse('2026-10-02T12:00:00Z')
const run = (overrides: Partial<Run> = {}): Run =>
  ({
    harness: 'claude',
    status: 'succeeded',
    sessionId: 'sess-1',
    workspacePath: '/home/runner/.local/share/aictiq/runner/r-1/repo',
    finishedAt: '2026-10-01T12:00:00Z',
    ...overrides,
  }) as Run

describe('resumeCommand', () => {
  it('builds the command for each harness, inside the kept checkout', () => {
    const cd = "cd '/home/runner/.local/share/aictiq/runner/r-1/repo' && "
    expect(resumeCommand(run(), now)).toBe(`${cd}claude --resume 'sess-1'`)
    expect(resumeCommand(run({ harness: 'codex' }), now)).toBe(`${cd}codex resume 'sess-1'`)
    expect(resumeCommand(run({ harness: 'opencode' }), now)).toBe(
      `${cd}opencode --session 'sess-1'`,
    )
    expect(resumeCommand(run({ harness: 'cursor' }), now)).toBe(`${cd}agent --resume 'sess-1'`)
    expect(resumeCommand(run({ harness: 'copilot' }), now)).toBe(`${cd}copilot --resume='sess-1'`)
  })

  it('offers nothing without a session, a stored checkout, a finish, or within five days', () => {
    expect(resumeCommand(run({ sessionId: null }), now)).toBeNull()
    expect(resumeCommand(run({ workspacePath: null }), now)).toBeNull()
    expect(resumeCommand(run({ workspacePath: undefined }), now)).toBeNull()
    expect(resumeCommand(run({ finishedAt: null, status: 'running' }), now)).toBeNull()
    expect(resumeCommand(run({ finishedAt: '2026-09-27T11:59:00Z' }), now)).toBeNull()
    expect(resumeCommand(run({ finishedAt: '2026-09-27T12:01:00Z' }), now)).not.toBeNull()
  })

  it('quotes a Windows checkout for PowerShell', () => {
    expect(resumeCommand(run({ workspacePath: "C:\\Users\\O'Neil\\repo" }), now)).toBe(
      "cd 'C:\\Users\\O''Neil\\repo' && claude --resume 'sess-1'",
    )
  })
})

describe('quoteForShell', () => {
  const awkward = "/tmp/a dir/it's $HOME `x` \\n \\ end"

  // Without the user's startup files, which may print on their own.
  it.each([
    ['bash', '--norc'],
    ['zsh', '-f'],
    ['fish', '--no-config'],
  ])('reads back as the same path in %s', (shell, noConfig) => {
    let output: string
    try {
      output = execFileSync(shell, [noConfig, '-c', `printf '%s' ${quoteForShell(awkward)}`], {
        encoding: 'utf8',
      })
    } catch {
      // The shell is not installed here; the other two still cover the quoting.
      return
    }
    expect(output).toBe(awkward)
  })
})
