import { mkdtempSync, statSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { describe, expect, it } from 'vitest'
import { clearConfig, configPath, readConfig, writeConfig } from '../src/config.js'
import { resolveSettings } from '../src/context.js'

const scratch = () => join(mkdtempSync(join(tmpdir(), 'aictiq-cli-')), 'config.json')

describe('config file', () => {
  it('round-trips and is written 0600', () => {
    const path = scratch()
    writeConfig({ url: 'https://aictiq.example.com', token: 'aiq_secret', org: 'acme' }, path)

    expect(readConfig(path)).toEqual({
      url: 'https://aictiq.example.com',
      token: 'aiq_secret',
      org: 'acme',
    })
    // The file holds a bearer token; anything group- or world-readable is a leak.
    expect(statSync(path).mode & 0o777).toBe(0o600)
  })

  it('narrows the mode of a file that was widened after it was written', () => {
    const path = scratch()
    writeConfig({ token: 'aiq_one' }, path)
    writeFileSync(path, '{}', { mode: 0o644 })
    writeConfig({ token: 'aiq_two' }, path)

    expect(statSync(path).mode & 0o777).toBe(0o600)
  })

  it('treats an unreadable or corrupt file as no configuration', () => {
    const path = scratch()
    writeFileSync(path, 'not json{')
    expect(readConfig(path)).toEqual({})
    expect(readConfig(join(path, 'missing.json'))).toEqual({})
  })

  it('drops values of the wrong type rather than passing them on', () => {
    const path = scratch()
    writeFileSync(path, JSON.stringify({ url: 42, token: 'aiq_ok', org: null }))
    expect(readConfig(path)).toEqual({ token: 'aiq_ok' })
  })

  it('removes the file on logout', () => {
    const path = scratch()
    writeConfig({ token: 'aiq_secret' }, path)
    clearConfig(path)
    expect(readConfig(path)).toEqual({})
  })

  it('honours XDG_CONFIG_HOME', () => {
    expect(configPath({ XDG_CONFIG_HOME: '/x/cfg' } as NodeJS.ProcessEnv)).toBe(
      '/x/cfg/aictiq/config.json',
    )
  })
})

describe('settings precedence', () => {
  const stored = { url: 'https://file', token: 'aiq_file', org: 'file-org' }
  const env = {
    AICTIQ_URL: 'https://env',
    AICTIQ_TOKEN: 'aiq_env',
    AICTIQ_ORG: 'env-org',
  } as NodeJS.ProcessEnv

  it('prefers flags over environment over the config file', () => {
    expect(
      resolveSettings({ url: 'https://flag', token: 'aiq_flag', org: 'flag-org' }, env, stored),
    ).toEqual({ url: 'https://flag', token: 'aiq_flag', org: 'flag-org' })
  })

  it('falls back to the environment, then the file', () => {
    expect(resolveSettings({}, env, stored)).toEqual({
      url: 'https://env',
      token: 'aiq_env',
      org: 'env-org',
    })
    expect(resolveSettings({}, {} as NodeJS.ProcessEnv, stored)).toEqual(stored)
  })
})
