import { existsSync, readFileSync, writeFileSync } from 'node:fs'
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

describe('aictiq attachment upload and item comment --attachment', () => {
  let instance: FakeInstance
  beforeEach(async () => { instance = await new FakeInstance().start() })
  afterEach(async () => { vi.restoreAllMocks(); await instance.stop() })

  it('uploads into the item\'s project and commits the uploads to the posted comment', async () => {
    const id = 'a1b2c3d4-0000-4000-8000-000000000002'
    const commentId = 'c0000000-0000-4000-8000-000000000003'
    const file = join(mkdtempSync(join(tmpdir(), 'aictiq-upload-')), 'result.png')
    writeFileSync(file, Buffer.from([137, 80, 78, 71]))
    instance.on((request) => {
      if (request.path === '/api/v1/orgs/acme/projects/ACME/attachments') {
        return { status: 200, body: { id, fileName: 'result.webp', contentType: 'image/webp', sizeBytes: 4 } }
      }
      if (request.path === '/api/v1/orgs/acme/items/ACME-7/comments') return { status: 201, body: { id: commentId } }
      if (request.path === `/api/v1/orgs/acme/attachments/${id}/commit`) return { status: 200, body: { id, commentId } }
      return undefined
    })
    const out = vi.spyOn(process.stdout, 'write').mockImplementation(() => true)
    const global = ['--url', instance.url, '--token', 'aiq_secret', '--org', 'acme']

    await createProgram().parseAsync([...global, 'attachment', 'upload', file, '--item', 'acme-7'], { from: 'user' })
    const upload = instance.to('/projects/ACME/attachments')[0]!
    expect(upload.method).toBe('POST')
    expect(upload.body).toContain('filename="result.png"')
    expect(upload.body).toContain('Content-Type: image/png')
    expect(out.mock.calls.map((call) => String(call[0])).join('')).toContain(
      `![result.webp](/api/v1/orgs/acme/attachments/${id}/download)`,
    )

    await createProgram().parseAsync(
      [...global, 'item', 'comment', 'ACME-7', '-m', 'proof', '--attachment', id],
      { from: 'user' },
    )
    const commits = instance.to(`/attachments/${id}/commit`)
    expect(commits).toHaveLength(1)
    expect(commits[0]!.body).toEqual({ commentId })
  })
})
