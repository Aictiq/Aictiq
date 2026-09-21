import { mkdtempSync, statSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { describe, expect, it } from 'vitest'
import { readRunnerConfig, runnerConfigPath, writeRunnerConfig } from '../../src/runner/config.js'

describe('runner.json', () => {
  it('lives beside config.json but is its own file', () => {
    expect(runnerConfigPath({ AICTIQ_CONFIG_HOME: '/cfg' })).toBe('/cfg/aictiq/runner.json')
  })

  it('round-trips with mode 0600 and ignores non-string workspace entries', () => {
    const path = join(mkdtempSync(join(tmpdir(), 'aictiq-runner-')), 'runner.json')
    writeRunnerConfig(
      {
        url: 'https://j.example', token: 'jrn_x', workspaces: { ACME: '/src/aictiq' },
        attachments: { maxCount: 10, maxBytes: 1024 },
      },
      path,
    )
    expect(statSync(path).mode & 0o777).toBe(0o600)
    expect(readRunnerConfig(path)).toEqual({
      url: 'https://j.example',
      token: 'jrn_x',
      workspaces: { ACME: '/src/aictiq' },
      attachments: { maxCount: 10, maxBytes: 1024 },
    })

    writeFileSync(path, JSON.stringify({ url: 'u', token: 't', workspaces: { A: '/a', B: 3 } }))
    expect(readRunnerConfig(path)?.workspaces).toEqual({ A: '/a' })
    expect(readRunnerConfig(path)?.attachments).toEqual({ maxCount: 25, maxBytes: 25 * 1024 * 1024 })
  })

  it('treats a missing or broken file as unregistered', () => {
    const dir = mkdtempSync(join(tmpdir(), 'aictiq-runner-'))
    expect(readRunnerConfig(join(dir, 'none.json'))).toBeNull()
    writeFileSync(join(dir, 'bad.json'), '{')
    expect(readRunnerConfig(join(dir, 'bad.json'))).toBeNull()
  })
})
