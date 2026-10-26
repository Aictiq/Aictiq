import { chmodSync, mkdirSync, writeFileSync } from 'node:fs'
import { dirname, join, relative, resolve, sep } from 'node:path'
import { inflateRawSync } from 'node:zlib'

/**
 * Bounded ZIP extraction for files a person attached to a project-chat question. The archive
 * is untrusted: entries are only ever written below `dest`, never through a symlink, and the
 * runner stops at fixed counts and sizes rather than trusting what the archive declares. What
 * it does not extract is named with the reason, so the agent can say what it could not read.
 */

export interface ZipLimits {
  /** Files written from one archive. */
  maxFiles: number
  /** Uncompressed bytes of one entry. */
  maxEntryBytes: number
  /** Uncompressed bytes written from one archive. */
  maxTotalBytes: number
}

export const DefaultZipLimits: ZipLimits = {
  maxFiles: 200,
  maxEntryBytes: 25 * 1024 * 1024,
  maxTotalBytes: 100 * 1024 * 1024,
}

export interface ZipExtraction {
  /** Paths relative to `dest`, `/`-separated, with their sizes. */
  extracted: Array<{ path: string; sizeBytes: number }>
  skipped: Array<{ path: string; reason: string }>
  /** Set when nothing could be read: not a ZIP, ZIP64, or a broken central directory. */
  error?: string
}

const EndOfCentralDirectory = 0x06054b50
const CentralDirectoryEntry = 0x02014b50
const LocalFileHeader = 0x04034b50

export function extractZip(bytes: Uint8Array, dest: string, limits: ZipLimits = DefaultZipLimits): ZipExtraction {
  const result: ZipExtraction = { extracted: [], skipped: [] }
  const data = Buffer.from(bytes.buffer, bytes.byteOffset, bytes.byteLength)
  const end = findEndOfCentralDirectory(data)
  if (end < 0) return { ...result, error: 'not a readable ZIP archive' }

  const count = data.readUInt16LE(end + 10)
  const directorySize = data.readUInt32LE(end + 12)
  const directoryOffset = data.readUInt32LE(end + 16)
  if (count === 0xffff || directoryOffset === 0xffffffff || directorySize === 0xffffffff) {
    return { ...result, error: 'ZIP64 archives are not supported' }
  }
  if (directoryOffset + directorySize > data.length) return { ...result, error: 'the ZIP central directory is damaged' }

  const root = resolve(dest)
  mkdirSync(root, { recursive: true, mode: 0o700 })
  let total = 0
  let offset = directoryOffset
  for (let index = 0; index < count; index++) {
    if (offset + 46 > data.length || data.readUInt32LE(offset) !== CentralDirectoryEntry) {
      return { ...result, error: 'the ZIP central directory is damaged' }
    }
    const flags = data.readUInt16LE(offset + 8)
    const method = data.readUInt16LE(offset + 10)
    const compressedSize = data.readUInt32LE(offset + 20)
    const size = data.readUInt32LE(offset + 24)
    const nameLength = data.readUInt16LE(offset + 28)
    const extraLength = data.readUInt16LE(offset + 30)
    const commentLength = data.readUInt16LE(offset + 32)
    const madeBy = data.readUInt16LE(offset + 4) >> 8
    const externalAttributes = data.readUInt32LE(offset + 38)
    const localOffset = data.readUInt32LE(offset + 42)
    const rawName = data.subarray(offset + 46, offset + 46 + nameLength)
    // Bit 11 marks UTF-8 names; anything else is CP437, which latin1 reads well enough for a label.
    const name = rawName.toString(flags & 0x800 ? 'utf8' : 'latin1')
    offset += 46 + nameLength + extraLength + commentLength

    if (name.endsWith('/') || name.endsWith('\\')) continue
    const skip = (reason: string) => result.skipped.push({ path: name, reason })
    // Unix mode in the high half of the external attributes, when the archive came from Unix.
    if (madeBy === 3 && ((externalAttributes >>> 16) & 0o170000) === 0o120000) {
      skip('symbolic link')
      continue
    }
    const path = safePath(name)
    if (!path) {
      skip('unsafe path')
      continue
    }
    if (flags & 0x1) {
      skip('password-protected')
      continue
    }
    if (method !== 0 && method !== 8) {
      skip(`unsupported compression method ${method}`)
      continue
    }
    if (result.extracted.length >= limits.maxFiles) {
      skip(`over the limit of ${limits.maxFiles} files`)
      continue
    }
    if (size > limits.maxEntryBytes) {
      skip(`larger than ${formatBytes(limits.maxEntryBytes)}`)
      continue
    }
    if (total + size > limits.maxTotalBytes) {
      skip(`would exceed ${formatBytes(limits.maxTotalBytes)} extracted`)
      continue
    }

    if (localOffset + 30 > data.length || data.readUInt32LE(localOffset) !== LocalFileHeader) {
      skip('damaged entry')
      continue
    }
    const start = localOffset + 30 + data.readUInt16LE(localOffset + 26) + data.readUInt16LE(localOffset + 28)
    if (start + compressedSize > data.length) {
      skip('damaged entry')
      continue
    }
    const compressed = data.subarray(start, start + compressedSize)
    let content: Buffer
    try {
      // The declared size is a claim: inflation stops one byte past it, so a bomb fails here.
      content = method === 0 ? compressed : inflateRawSync(compressed, { maxOutputLength: Math.max(1, size + 1) })
    } catch {
      skip(size === 0 ? 'damaged entry' : 'damaged entry or larger than it declares')
      continue
    }
    if (content.length !== size) {
      skip('damaged entry')
      continue
    }

    const target = resolve(root, ...path.split('/'))
    if (!target.startsWith(root + sep)) {
      skip('unsafe path')
      continue
    }
    try {
      mkdirSync(dirname(target), { recursive: true, mode: 0o700 })
      // `wx`: an earlier entry of the same name keeps its place; a later one is reported.
      writeFileSync(target, content, { mode: 0o600, flag: 'wx' })
      chmodSync(target, 0o600)
    } catch (error) {
      skip((error as NodeJS.ErrnoException).code === 'EEXIST' ? 'duplicate name' : 'could not be written')
      continue
    }
    total += size
    result.extracted.push({ path: relative(root, target).split(sep).join('/'), sizeBytes: size })
  }
  return result
}

/** The entry's path below the extraction root, or null for absolute, drive or `..` paths. */
function safePath(name: string): string | null {
  const parts = name.replace(/\\/g, '/').split('/').filter((part) => part !== '' && part !== '.')
  if (name.startsWith('/') || name.startsWith('\\') || /^[A-Za-z]:/.test(name)) return null
  if (parts.length === 0 || parts.some((part) => part === '..' || /\p{Cc}/u.test(part))) return null
  return join(...parts).split(sep).join('/')
}

function findEndOfCentralDirectory(data: Buffer): number {
  // The record is 22 bytes plus a comment of at most 65,535.
  const floor = Math.max(0, data.length - 22 - 0xffff)
  for (let at = data.length - 22; at >= floor; at--) {
    if (data.readUInt32LE(at) === EndOfCentralDirectory) return at
  }
  return -1
}

function formatBytes(value: number): string {
  return value >= 1024 * 1024 ? `${(value / (1024 * 1024)).toFixed(0)} MiB` : `${value} bytes`
}
