import { existsSync, mkdtempSync, readdirSync, readFileSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { extractZip } from '../../src/runner/zip.js'
import { makeZip } from './zip-fixture.js'

let scratch: string

beforeEach(() => {
  scratch = mkdtempSync(join(tmpdir(), 'aictiq-zip-'))
})

afterEach(() => {
  rmSync(scratch, { recursive: true, force: true })
})

describe('extractZip', () => {
  it('extracts deflated and stored files into their folders', () => {
    const dest = join(scratch, 'out')
    const result = extractZip(makeZip([
      { name: 'docs/' },
      { name: 'docs/readme.md', data: '# Secret codename: BLUEBIRD' },
      { name: 'notes.txt', data: 'stored', stored: true },
    ]), dest)

    expect(result.error).toBeUndefined()
    expect(result.skipped).toEqual([])
    expect(result.extracted).toEqual([
      { path: 'docs/readme.md', sizeBytes: 27 },
      { path: 'notes.txt', sizeBytes: 6 },
    ])
    expect(readFileSync(join(dest, 'docs', 'readme.md'), 'utf8')).toContain('BLUEBIRD')
    expect(readFileSync(join(dest, 'notes.txt'), 'utf8')).toBe('stored')
  })

  it('never writes outside the destination, through a link or past its limits', () => {
    const dest = join(scratch, 'out')
    const result = extractZip(makeZip([
      { name: '../escape.txt', data: 'x' },
      { name: '/etc/passwd', data: 'x' },
      { name: 'C:\\windows\\evil.txt', data: 'x' },
      { name: 'link', data: '/etc/passwd', symlink: true },
      { name: 'locked.txt', data: 'x', encrypted: true },
      { name: 'bomb.txt', data: 'a'.repeat(4096), size: 10 },
      { name: 'big.txt', data: 'a'.repeat(64) },
      { name: 'ok.txt', data: 'fine' },
      { name: 'ok.txt', data: 'again' },
      { name: 'more.txt', data: 'two' },
      { name: 'extra.txt', data: 'one too many' },
    ]), dest, { maxFiles: 2, maxEntryBytes: 32, maxTotalBytes: 1024 })

    expect(result.extracted).toEqual([{ path: 'ok.txt', sizeBytes: 4 }, { path: 'more.txt', sizeBytes: 3 }])
    expect(Object.fromEntries(result.skipped.map((s) => [s.path, s.reason]))).toEqual({
      '../escape.txt': 'unsafe path',
      '/etc/passwd': 'unsafe path',
      'C:\\windows\\evil.txt': 'unsafe path',
      link: 'symbolic link',
      'locked.txt': 'password-protected',
      'bomb.txt': 'damaged entry or larger than it declares',
      'big.txt': 'larger than 32 bytes',
      'ok.txt': 'duplicate name',
      'extra.txt': 'over the limit of 2 files',
    })
    expect(readdirSync(dest).sort()).toEqual(['more.txt', 'ok.txt'])
    expect(existsSync(join(scratch, 'escape.txt'))).toBe(false)
  })

  it('says so when the bytes are not an archive', () => {
    const result = extractZip(new TextEncoder().encode('not a zip at all'), join(scratch, 'out'))
    expect(result.error).toBe('not a readable ZIP archive')
    expect(result.extracted).toEqual([])
  })
})
