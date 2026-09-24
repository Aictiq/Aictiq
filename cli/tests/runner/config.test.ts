import { mkdtempSync, readFileSync, statSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { describe, expect, it } from 'vitest'
import {
  profileLabel,
  readRunnerConfig,
  runnerConfigPath,
  writeRunnerConfig,
} from '../../src/runner/config.js'

const machineId = '4d1c7f2e-9b1a-4c3e-8f5d-2a6b7c8d9e0f'

describe('runner.json', () => {
  it('lives beside config.json but is its own file', () => {
    expect(runnerConfigPath({ AICTIQ_CONFIG_HOME: '/cfg' })).toBe('/cfg/aictiq/runner.json')
  })

  it('round-trips its profiles with mode 0600 and ignores non-string workspace entries', () => {
    const path = join(mkdtempSync(join(tmpdir(), 'aictiq-runner-')), 'runner.json')
    const config = {
      machineId,
      profiles: [
        { url: 'https://j.example', token: 'jrn_a', organization: 'acme', workspaces: { ACME: '/src/acme' }, repoRoots: ['/src/acme-clients'] },
        { url: 'https://j.example', token: 'jrn_b', organization: 'globex', workspaces: { ACME: '/src/globex-acme' }, repoRoots: [] },
      ],
      attachments: { maxCount: 10, maxBytes: 1024 },
    }
    writeRunnerConfig(config, path)
    expect(statSync(path).mode & 0o777).toBe(0o600)
    expect(readRunnerConfig(path)).toEqual(config)

    writeFileSync(path, JSON.stringify({ machineId, profiles: [{ url: 'u', token: 't', workspaces: { A: '/a', B: 3 }, repoRoots: ['/r', 3, ''] }, { url: 'broken' }] }))
    const read = readRunnerConfig(path)!
    expect(read.profiles).toEqual([{ url: 'u', token: 't', workspaces: { A: '/a' }, repoRoots: ['/r'] }])
    expect(read.attachments).toEqual({ maxCount: 25, maxBytes: 25 * 1024 * 1024 })
  })

  it('reads a file from before profiles as one profile and keeps a machine id once written', () => {
    const path = join(mkdtempSync(join(tmpdir(), 'aictiq-runner-')), 'runner.json')
    writeFileSync(path, JSON.stringify({
      url: 'https://j.example', token: 'jrn_x', name: 'laptop',
      workspaces: { ACME: '/src/acme' }, repoRoots: ['/src'], attachments: { maxCount: 3, maxBytes: 9 },
    }))

    const legacy = readRunnerConfig(path)!
    expect(legacy).toMatchObject({
      name: 'laptop',
      profiles: [{ url: 'https://j.example', token: 'jrn_x', workspaces: { ACME: '/src/acme' }, repoRoots: ['/src'] }],
      attachments: { maxCount: 3, maxBytes: 9 },
    })
    expect(legacy.profiles[0]!.organization).toBeUndefined()
    expect(legacy.machineId).toMatch(/^[0-9a-f-]{36}$/)
    expect(profileLabel(legacy.profiles[0]!)).toBe('j.example')

    writeRunnerConfig(legacy, path)
    const written = JSON.parse(readFileSync(path, 'utf8')) as Record<string, unknown>
    expect(written.url).toBeUndefined()
    expect(written.machineId).toBe(legacy.machineId)
    expect(readRunnerConfig(path)!.machineId).toBe(legacy.machineId)
  })

  it('treats a missing or broken file as unregistered', () => {
    const dir = mkdtempSync(join(tmpdir(), 'aictiq-runner-'))
    expect(readRunnerConfig(join(dir, 'none.json'))).toBeNull()
    writeFileSync(join(dir, 'bad.json'), '{')
    expect(readRunnerConfig(join(dir, 'bad.json'))).toBeNull()
    writeFileSync(join(dir, 'empty.json'), '{}')
    expect(readRunnerConfig(join(dir, 'empty.json'))).toBeNull()
  })
})
