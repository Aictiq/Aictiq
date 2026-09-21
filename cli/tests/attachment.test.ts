import { existsSync, readFileSync } from 'node:fs'
import { mkdtempSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createProgram } from '../src/program.js'
import { FakeInstance } from './runner/fake-instance.js'

describe('aictiq attachment get', () => {
  let instance: FakeInstance
  beforeEach(async () => { instance = await new FakeInstance().start() })
  afterEach(async () => { vi.restoreAllMocks(); await instance.stop() })

  it('uses the scoped authenticated download route and writes exact bytes outside the checkout', async () => {
    const id = 'a1b2c3d4-0000-4000-8000-000000000001'
    const bytes = new Uint8Array([0, 255, 1, 2])
    const output = join(mkdtempSync(join(tmpdir(), 'aictiq-attachment-')), 'screenshot.webp')
    instance.on((request) => request.path === `/api/v1/orgs/acme/attachments/${id}/download`
      ? { status: 200, body: bytes }
      : undefined)

    await createProgram().parseAsync(
      ['--url', instance.url, '--token', 'aiq_secret', '--org', 'acme', 'attachment', 'get', id, '-o', output],
      { from: 'user' },
    )

    expect(existsSync(output)).toBe(true)
    expect(readFileSync(output)).toEqual(Buffer.from(bytes))
    expect(instance.to(`/attachments/${id}/download`)).toHaveLength(1)
  })
})
