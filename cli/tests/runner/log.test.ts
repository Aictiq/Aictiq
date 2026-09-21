import { describe, expect, it } from 'vitest'
import { RunnerHttpError, RunnerNetworkError } from '../../src/runner/client.js'
import type { LogChunk } from '../../src/runner/client.js'
import { LogStreamer, redact, splitByBytes } from '../../src/runner/log.js'

const agentToken = 'aiq_agent_0123456789abcdef'
const cloneToken = 'ghs_installation_0123456789'

describe('redact', () => {
  it('blanks every occurrence of every secret', () => {
    expect(
      redact(
        `push https://x-access-token:${cloneToken}@github.com and ${agentToken} ${agentToken}`,
        [agentToken, cloneToken],
      ),
    ).toBe('push https://x-access-token:[redacted]@github.com and [redacted] [redacted]')
  })

  it('ignores empty and trivially short secrets rather than shredding the line', () => {
    expect(redact('a normal line', ['', 'a'])).toBe('a normal line')
  })
})

describe('splitByBytes', () => {
  it('keeps multi-byte characters whole', () => {
    const pieces = splitByBytes('ééééé', 4)
    expect(pieces).toEqual(['éé', 'éé', 'é'])
    expect(pieces.every((p) => Buffer.byteLength(p) <= 4)).toBe(true)
  })
})

describe('LogStreamer', () => {
  it('batches lines with consecutive sequence numbers and redacts before sending', async () => {
    const sent: LogChunk[][] = []
    const log = new LogStreamer({
      send: async (chunks) => void sent.push(chunks),
      secrets: [agentToken],
      maxBatchBytes: 64 * 1024,
      flushIntervalMs: 10_000,
    })
    log.push('event', 'Workspace ready')
    log.push('stdout', `token is ${agentToken}`)
    log.push('stderr', 'warning')
    await log.close()

    expect(sent).toHaveLength(1)
    expect(sent[0]!.map((c) => [c.seq, c.stream, c.text])).toEqual([
      [0, 'event', 'Workspace ready'],
      [1, 'stdout', 'token is [redacted]'],
      [2, 'stderr', 'warning'],
    ])
  })

  it('flushes on its own once the interval passes', async () => {
    const sent: LogChunk[][] = []
    const log = new LogStreamer({
      send: async (c) => void sent.push(c),
      secrets: [],
      maxBatchBytes: 1024,
      flushIntervalMs: 20,
    })
    log.push('stdout', 'one')
    await new Promise((resolve) => setTimeout(resolve, 80))
    expect(sent).toHaveLength(1)
    await log.close()
  })

  it('splits batches at the byte limit', async () => {
    const sent: LogChunk[][] = []
    const log = new LogStreamer({
      send: async (c) => void sent.push(c),
      secrets: [],
      maxBatchBytes: 10,
      flushIntervalMs: 10_000,
    })
    for (const line of ['aaaaaa', 'bbbbbb', 'cccccc']) log.push('stdout', line)
    await log.close()
    expect(sent.map((batch) => batch.map((c) => c.seq))).toEqual([[0], [1], [2]])
  })

  it('re-sends a failed batch with the same sequence numbers', async () => {
    const attempts: number[][] = []
    let fail = true
    const log = new LogStreamer({
      send: async (chunks) => {
        attempts.push(chunks.map((c) => c.seq))
        if (fail) {
          fail = false
          throw new RunnerNetworkError('connection reset')
        }
      },
      secrets: [],
      maxBatchBytes: 1024,
      flushIntervalMs: 10_000,
      retryDelayMs: () => 1,
    })
    log.push('stdout', 'a')
    log.push('stdout', 'b')
    await log.close()
    expect(attempts).toEqual([
      [0, 1],
      [0, 1],
    ])
  })

  it('stops sending at the server cap and says so locally', async () => {
    const notes: string[] = []
    let calls = 0
    const log = new LogStreamer({
      send: async () => {
        calls++
        throw new RunnerHttpError(
          413,
          { type: 'https://aictiq.com/problems/log-limit-exceeded' },
          'capped',
        )
      },
      secrets: [],
      maxBatchBytes: 1024,
      flushIntervalMs: 10_000,
      local: (m) => notes.push(m),
    })
    log.push('stdout', 'a')
    await log.flush()
    log.push('stdout', 'b')
    await log.close()
    expect(calls).toBe(1)
    expect(log.truncated).toBe(true)
    expect(notes[0]).toMatch(/capped/)
  })
})
