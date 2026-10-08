import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { claude } from '../../src/runner/harness/claude.js'
import { codex } from '../../src/runner/harness/codex.js'
import { readUsageLimits, recordUsageLimits, usageWindow } from '../../src/runner/limits.js'

// Shapes recorded from Claude Code 2.1.294 and Codex CLI 0.160.0 (docs/harness-usage-limits.md).
const claudeEvent = JSON.stringify({
  type: 'rate_limit_event',
  rate_limit_info: {
    status: 'allowed',
    resetsAt: 1791459000,
    rateLimitType: 'five_hour',
    unifiedWindows: {
      five_hour: { utilization: 0.79, resetsAt: 1791459000 },
      seven_day: { utilization: 0.14, resetsAt: 1791763200 },
    },
  },
})

const codexTokenCount = JSON.stringify({
  timestamp: '2026-10-08T11:17:50.378Z',
  type: 'event_msg',
  payload: {
    type: 'token_count',
    info: { total_token_usage: { input_tokens: 20073, output_tokens: 5 } },
    rate_limits: {
      limit_id: 'codex',
      primary: { used_percent: 19.0, window_minutes: 300, resets_at: 1791465252 },
      secondary: { used_percent: 16.0, window_minutes: 10080, resets_at: 1791959976 },
    },
  },
})

describe('Claude usage limits', () => {
  it('reads both windows of a rate limit event as percentages', () => {
    expect(claude.parse(claudeEvent)).toEqual({
      log: null,
      limits: {
        fiveHour: { usedPercent: 79, resetsAt: '2026-10-08T11:30:00.000Z' },
        weekly: { usedPercent: 14, resetsAt: '2026-10-12T00:00:00.000Z' },
      },
    })
  })

  it('reads the one window an event without unifiedWindows names', () => {
    const event = {
      type: 'rate_limit_event',
      rate_limit_info: { rateLimitType: 'seven_day', utilization: 0.5, resetsAt: 1791763200 },
    }
    expect(claude.parse(JSON.stringify(event)).limits).toEqual({
      weekly: { usedPercent: 50, resetsAt: '2026-10-12T00:00:00.000Z' },
    })
  })

  it('reports nothing for an event without utilization', () => {
    const event = { type: 'rate_limit_event', rate_limit_info: { status: 'allowed' } }
    expect(claude.parse(JSON.stringify(event))).toEqual({ log: null })
  })
})

describe('Codex usage limits', () => {
  let home: string
  const previous = process.env.CODEX_HOME

  beforeEach(() => {
    home = mkdtempSync(join(tmpdir(), 'aictiq-codex-home-'))
    process.env.CODEX_HOME = home
  })

  afterEach(() => {
    if (previous === undefined) delete process.env.CODEX_HOME
    else process.env.CODEX_HOME = previous
    rmSync(home, { recursive: true, force: true })
  })

  it('tells the windows apart by length, alongside the token counts', () => {
    const parsed = codex.parse(codexTokenCount)
    expect(parsed.limits).toEqual({
      fiveHour: { usedPercent: 19, resetsAt: '2026-10-08T13:14:12.000Z' },
      weekly: { usedPercent: 16, resetsAt: '2026-10-14T06:39:36.000Z' },
    })
    expect(parsed.outputTokens).toBe(5)
  })

  it('reads the last rate limits from the session rollout file', () => {
    const session = '01a11b3b-c668-7aa3-a11d-556f467eed09'
    const day = join(home, 'sessions', '2026', '10', '08')
    mkdirSync(day, { recursive: true })
    const older = codexTokenCount.replace('"used_percent":19', '"used_percent":5')
    writeFileSync(
      join(day, `rollout-2026-10-08T13-17-45-${session}.jsonl`),
      [older, '{"type":"turn_context"}', codexTokenCount, '{"type":"event_msg","payl'].join('\n'),
    )
    expect(codex.limits?.(session)?.fiveHour?.usedPercent).toBe(19)
    expect(codex.limits?.('another-session')).toBeNull()
    expect(codex.limits?.(undefined)).toBeNull()
  })
})

describe('usage limits store', () => {
  let file: string
  beforeEach(() => {
    file = join(mkdtempSync(join(tmpdir(), 'aictiq-limits-')), 'harness-limits.json')
  })

  const window = (usedPercent: number) => ({ usedPercent, resetsAt: null })

  it('keeps a window a later run did not report', () => {
    recordUsageLimits('claude', { fiveHour: window(10), weekly: window(20) }, new Date(1_000), file)
    recordUsageLimits('claude', { fiveHour: window(30) }, new Date(2_000), file)
    recordUsageLimits('codex', { weekly: window(5) }, new Date(3_000), file)
    expect(readUsageLimits(file)).toEqual([
      {
        harness: 'claude',
        observedAt: new Date(2_000).toISOString(),
        fiveHour: window(30),
        weekly: window(20),
      },
      {
        harness: 'codex',
        observedAt: new Date(3_000).toISOString(),
        fiveHour: null,
        weekly: window(5),
      },
    ])
  })

  it('never lets an older observation replace a newer one', () => {
    recordUsageLimits('claude', { fiveHour: window(50) }, new Date(5_000), file)
    recordUsageLimits('claude', { fiveHour: window(10) }, new Date(4_000), file)
    expect(readUsageLimits(file)[0]?.fiveHour).toEqual(window(50))
  })

  it('reads a missing or damaged file as nothing', () => {
    expect(readUsageLimits(file)).toEqual([])
    writeFileSync(file, '{not json')
    expect(readUsageLimits(file)).toEqual([])
    writeFileSync(file, JSON.stringify([{ harness: 'nope', observedAt: 'x' }]))
    expect(readUsageLimits(file)).toEqual([])
    expect(readFileSync(file, 'utf8')).toContain('nope')
  })

  it('normalizes a window', () => {
    expect(usageWindow(42.456, '2026-10-12T00:00:00Z')).toEqual({
      usedPercent: 42.5,
      resetsAt: '2026-10-12T00:00:00.000Z',
    })
    expect(usageWindow(-3, 'garbage')).toEqual({ usedPercent: 0, resetsAt: null })
    expect(usageWindow(Number.NaN, null)).toBeNull()
  })
})
