import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { RunnerClient } from '../../src/runner/client.js'
import { executeRun } from '../../src/runner/execute.js'
import { RunnerLoop, RunnerRevokedError } from '../../src/runner/loop.js'
import type { RunnerCapabilities } from '../../src/runner/types.js'
import { mkdtempSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { claimedRun, hello, scriptAdapter } from './helpers.js'
import { FakeInstance } from './fake-instance.js'

const capabilities: RunnerCapabilities = {
  v: 1,
  harnesses: [{ name: 'fake', version: '1.0.0' }],
  os: 'linux',
  arch: 'x64',
  cliVersion: '0.1.0',
  maxParallel: 1,
}

describe('RunnerLoop', () => {
  let instance: FakeInstance

  beforeEach(async () => {
    instance = await new FakeInstance().start()
  })
  afterEach(async () => {
    await instance.stop()
  })

  it('says hello, claims a run, executes it, reports it, and stops cleanly', async () => {
    let claims = 0
    instance.on((r) => {
      if (r.path.endsWith('/runner/hello')) return { status: 200, body: hello }
      if (r.path.endsWith('/runs/claim'))
        return ++claims === 1 ? { status: 200, body: claimedRun() } : { status: 204 }
      if (r.path.includes('/runner/runs/') && r.path.endsWith('/heartbeat'))
        return { status: 200, body: { cancelRequested: false } }
      return undefined
    })
    const client = new RunnerClient({ baseUrl: instance.url, token: 'jrn_secret_0123456789' })
    const adapter = scriptAdapter(`console.log('RESULT ok')`)
    let loop: RunnerLoop
    const finished = new Promise<void>((resolve) => {
      loop = new RunnerLoop({
        client,
        parallel: 2,
        probe: async () => capabilities,
        local: () => {},
        execute: async (run, h, shutdown) => {
          const report = await executeRun(run, {
            client,
            hello: h,
            adapters: { fake: adapter },
            runnerToken: 'jrn_secret_0123456789',
            shutdown,
            workspace: {
              root: mkdtempSync(join(tmpdir(), 'jr-')),
              repositories: {},
              keep: false,
              mcpServer: { command: 'x', args: [] },
            },
            provision: async () => ({
              runDir: tmpdir(),
              checkout: tmpdir(),
              branch: run.branchName,
              promptFile: '',
              mcpConfigFile: '',
              attachmentsDir: '',
              prompt: run.prompt,
              env: {},
              cleanup: async () => {},
            }),
            findPullRequest: async () => null,
            flushIntervalMs: 10,
          })
          resolve()
          return report
        },
      })
    })
    const running = loop!.run()
    await finished
    loop!.stop()
    await running

    expect(instance.to('/runner/hello')[0]!.body).toEqual({ capabilities })
    expect(instance.to('/runs/claim')[0]!.body).toEqual({ harnesses: ['fake'], slots: 2 })
    expect(instance.to('/finish')[0]!.body).toMatchObject({ outcome: 'succeeded', summary: 'ok' })
  })

  it('offers every known harness when none is detected, so a run fails visibly instead of waiting', async () => {
    instance.on((r) =>
      r.path.endsWith('/runner/hello') ? { status: 200, body: hello } : undefined,
    )
    const loop = new RunnerLoop({
      client: new RunnerClient({ baseUrl: instance.url, token: 'jrn_x' }),
      parallel: 1,
      probe: async () => ({ ...capabilities, harnesses: [] }),
      local: () => {},
      execute: async () => {},
    })
    const running = loop.run()
    while (instance.to('/runs/claim').length === 0)
      await new Promise((resolve) => setTimeout(resolve, 5))
    loop.stop()
    await running
    expect(instance.to('/runs/claim')[0]!.body).toMatchObject({
      harnesses: ['claude', 'codex', 'opencode'],
    })
  })

  it('exits with a revoked error when the instance answers token-revoked', async () => {
    instance.on((r) =>
      r.path.endsWith('/runner/hello')
        ? {
            status: 401,
            body: { type: 'https://aictiq.com/problems/token-revoked', title: 'Revoked' },
          }
        : undefined,
    )
    const loop = new RunnerLoop({
      client: new RunnerClient({ baseUrl: instance.url, token: 'jrn_x' }),
      parallel: 1,
      probe: async () => capabilities,
      local: () => {},
      execute: async () => {},
    })
    await expect(loop.run()).rejects.toBeInstanceOf(RunnerRevokedError)
  })

  it('backs off and retries when the instance cannot be reached', async () => {
    const notes: string[] = []
    const loop = new RunnerLoop({
      client: new RunnerClient({ baseUrl: 'http://127.0.0.1:9', token: 'jrn_x' }),
      parallel: 1,
      probe: async () => capabilities,
      local: (m) => notes.push(m),
      execute: async () => {},
      backoffMs: () => 10,
    })
    const running = loop.run()
    while (notes.length < 2) await new Promise((resolve) => setTimeout(resolve, 5))
    loop.stop()
    await running
    expect(notes[0]).toMatch(/Cannot reach/)
  })
})
