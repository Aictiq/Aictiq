import { spawnSync } from 'node:child_process'
import { chmodSync, mkdtempSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { describe, expect, it } from 'vitest'
import { ASKPASS_SCRIPT, CREDENTIAL_HELPER, TOKEN_ENV } from '../../src/runner/workspace.js'

/**
 * Whether this OS's git can run the runner's two credential paths: the askpass script used
 * for the clone and the `!f() {…}` helper the agent pushes with. Both are shell; Git for
 * Windows runs them through its bundled sh. Against a repository that does not exist, GitHub
 * asks for credentials, so a bogus token reaching it ends in an authentication failure,
 * while a path git could not run ends in "could not read Username" instead.
 *
 * Needs the network, so only the per-OS CI jobs run it (AICTIQ_PLATFORM_TESTS=1).
 */
const url = 'https://github.com/Aictiq/aictiq-platform-check-does-not-exist.git'

function lsRemote(config: string[], env: Record<string, string>) {
  // Outside any checkout and without global or system config: a CI checkout carries the
  // workflow token as an extraheader, and a runner image may have credential helpers, and
  // either would answer GitHub before the scripts under test are asked.
  const cwd = mkdtempSync(join(tmpdir(), 'aictiq-git-'))
  const globalConfig = join(cwd, 'gitconfig')
  writeFileSync(globalConfig, '')
  const result = spawnSync('git', [...config, 'ls-remote', url], {
    cwd,
    encoding: 'utf8',
    timeout: 60_000,
    env: {
      ...process.env,
      GIT_TERMINAL_PROMPT: '0',
      GIT_CONFIG_NOSYSTEM: '1',
      GIT_CONFIG_GLOBAL: globalConfig,
      [TOKEN_ENV]: 'ghs_bogusPlatformCheckToken000000000000',
      ...env,
    },
  })
  return { status: result.status, stderr: result.stderr }
}

const reachedGitHub = /Authentication failed|Invalid username or (token|password)/i
const couldNotRun = /could not read Username|cannot run|unable to start|No such file/i

describe.runIf(process.env.AICTIQ_PLATFORM_TESTS === '1')('git credentials on this OS', () => {
  it('runs the askpass script', () => {
    const dir = mkdtempSync(join(tmpdir(), 'aictiq-askpass-'))
    const askpass = join(dir, 'askpass.sh')
    writeFileSync(askpass, ASKPASS_SCRIPT, { mode: 0o700 })
    chmodSync(askpass, 0o700)

    const { status, stderr } = lsRemote(['-c', 'credential.helper='], { GIT_ASKPASS: askpass })

    expect(status, stderr).not.toBe(0)
    expect(stderr).toMatch(reachedGitHub)
    expect(stderr).not.toMatch(couldNotRun)
  })

  it('runs the push credential helper', () => {
    const { status, stderr } = lsRemote(
      ['-c', 'credential.helper=', '-c', `credential.helper=${CREDENTIAL_HELPER}`],
      { GIT_ASKPASS: '' },
    )

    expect(status, stderr).not.toBe(0)
    expect(stderr).toMatch(reachedGitHub)
    expect(stderr).not.toMatch(couldNotRun)
  })
})
