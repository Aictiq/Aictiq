import { mkdirSync, mkdtempSync, readFileSync, rmSync, utimesSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { readRunnerConfig, writeRunnerConfig } from '../../src/runner/config.js'
import { readUsageLimits, recordUsageLimits } from '../../src/runner/limits.js'
import {
  ClaudePollIntervalMs,
  ClaudeUsageUrl,
  CodexReadIntervalMs,
  credentialsOf,
  latestCodexLimits,
  parseClaudeOAuthUsage,
  readClaudeCredentials,
  tail,
  UsagePoller,
  type ClaudeCredentials,
} from '../../src/runner/usagePoll.js'

// A token_count line as Codex CLI 0.160.0 writes it to an interactive session's rollout.
const tokenCount = (usedPercent: number, timestamp: string) =>
  JSON.stringify({
    timestamp,
    type: 'event_msg',
    payload: {
      type: 'token_count',
      info: { total_token_usage: { input_tokens: 20073, output_tokens: 5 } },
      rate_limits: {
        primary: { used_percent: usedPercent, window_minutes: 300, resets_at: 1791465252 },
        secondary: { used_percent: 16.0, window_minutes: 10080, resets_at: 1791959976 },
      },
    },
  })

// The shape /api/oauth/usage returned on 2026-10-08 for a Pro account.
const oauthUsage = {
  five_hour: { utilization: 22.0, resets_at: '2026-10-08T16:30:00.377917+00:00' },
  seven_day: { utilization: 18.0, resets_at: '2026-10-12T00:00:00.000000+00:00' },
  seven_day_opus: null,
}

let dir: string
beforeEach(() => {
  dir = mkdtempSync(join(tmpdir(), 'aictiq-usage-'))
})
afterEach(() => {
  rmSync(dir, { recursive: true, force: true })
})

function rollout(day: string, name: string, lines: string[], modified: number): string {
  const folder = join(dir, 'sessions', ...day.split('/'))
  mkdirSync(folder, { recursive: true })
  const path = join(folder, name)
  writeFileSync(path, lines.join('\n'))
  utimesSync(path, modified / 1000, modified / 1000)
  return path
}

describe('Codex rollout read', () => {
  it('takes the newest rollout by modification, with the event time as observedAt', () => {
    rollout(
      '2026/10/07',
      'rollout-2026-10-07T09-00-00-a.jsonl',
      [tokenCount(80, '2026-10-08T12:00:00.000Z')],
      3_000_000,
    )
    rollout(
      '2026/10/08',
      'rollout-2026-10-08T08-00-00-b.jsonl',
      [tokenCount(40, '2026-10-08T08:30:00.000Z')],
      2_000_000,
    )
    const latest = latestCodexLimits(join(dir, 'sessions'))
    // The session started yesterday is still the one in use.
    expect(latest?.limits.fiveHour?.usedPercent).toBe(80)
    expect(latest?.limits.weekly?.usedPercent).toBe(16)
    expect(latest?.observedAt.toISOString()).toBe('2026-10-08T12:00:00.000Z')
  })

  it('falls back to an older rollout when the newest has no rate limits yet', () => {
    rollout(
      '2026/10/08',
      'rollout-2026-10-08T08-00-00-b.jsonl',
      [tokenCount(40, '2026-10-08T08:30:00.000Z')],
      2_000_000,
    )
    rollout(
      '2026/10/08',
      'rollout-2026-10-08T13-00-00-c.jsonl',
      ['{"type":"session_meta"}'],
      3_000_000,
    )
    expect(latestCodexLimits(join(dir, 'sessions'))?.limits.fiveHour?.usedPercent).toBe(40)
  })

  it('reads nothing when there are no sessions', () => {
    expect(latestCodexLimits(join(dir, 'sessions'))).toBeNull()
  })

  it('reads only the tail, dropping the line it cuts', () => {
    const filler = JSON.stringify({ type: 'response_item', text: 'x'.repeat(400 * 1024) })
    const path = rollout(
      '2026/10/08',
      'rollout-2026-10-08T08-00-00-d.jsonl',
      [
        tokenCount(5, '2026-10-08T07:00:00.000Z'),
        filler,
        tokenCount(55, '2026-10-08T09:00:00.000Z'),
      ],
      2_000_000,
    )
    const lines = tail(path, 1024)
    expect(lines).toHaveLength(1)
    expect(lines[0]).toContain('"used_percent":55')
    expect(latestCodexLimits(join(dir, 'sessions'))?.limits.fiveHour?.usedPercent).toBe(55)
  })
})

describe('Claude OAuth usage parser', () => {
  it('reads utilization as a percentage, not a fraction', () => {
    expect(parseClaudeOAuthUsage(oauthUsage)).toEqual({
      fiveHour: { usedPercent: 22, resetsAt: '2026-10-08T16:30:00.377Z' },
      weekly: { usedPercent: 18, resetsAt: '2026-10-12T00:00:00.000Z' },
    })
  })

  it('leaves out a null window', () => {
    expect(parseClaudeOAuthUsage({ five_hour: null, seven_day: oauthUsage.seven_day })).toEqual({
      weekly: { usedPercent: 18, resetsAt: '2026-10-12T00:00:00.000Z' },
    })
  })

  it('refuses a shape it does not know', () => {
    expect(parseClaudeOAuthUsage(null)).toBeNull()
    expect(parseClaudeOAuthUsage({ error: 'nope' })).toBeNull()
    expect(parseClaudeOAuthUsage({ five_hour: { utilization: '22%' } })).toBeNull()
  })

  it('reads the token from Claude Code credentials, respecting CLAUDE_CONFIG_DIR', async () => {
    expect(credentialsOf('{"claudeAiOauth":{"accessToken":"t","expiresAt":5}}')).toEqual({
      accessToken: 't',
      expiresAt: 5,
    })
    expect(credentialsOf('{"apiKey":"sk"}')).toBeNull()
    writeFileSync(join(dir, '.credentials.json'), '{"claudeAiOauth":{"accessToken":"from-file"}}')
    expect(await readClaudeCredentials({ CLAUDE_CONFIG_DIR: dir }, 'linux')).toEqual({
      accessToken: 'from-file',
    })
    expect(
      await readClaudeCredentials({ CLAUDE_CONFIG_DIR: join(dir, 'none') }, 'linux'),
    ).toBeNull()
  })
})

describe('usage poller', () => {
  const NowMs = Date.parse('2026-10-08T14:00:00.000Z')
  let now: number
  let limitsPath: string
  let logs: string[]

  beforeEach(() => {
    now = NowMs
    limitsPath = join(dir, 'harness-limits.json')
    logs = []
  })

  const response = (
    status: number,
    body: unknown = oauthUsage,
    headers: Record<string, string> = {},
  ) => new Response(JSON.stringify(body), { status, headers })

  function poller(options: {
    enabled?: boolean
    credentials?: ClaudeCredentials | null
    fetch?: ReturnType<typeof vi.fn>
  }) {
    const readClaudeCredentials = vi.fn(async () =>
      options.credentials === undefined
        ? { accessToken: 'secret', expiresAt: NowMs + 3_600_000 }
        : options.credentials,
    )
    const fetch = options.fetch ?? vi.fn(async () => response(200))
    const instance = new UsagePoller({
      claudeEnabled: () => options.enabled ?? true,
      log: (message) => logs.push(message),
      fetch: fetch as unknown as typeof globalThis.fetch,
      readClaudeCredentials,
      now: () => now,
      codexSessions: join(dir, 'sessions'),
      limitsPath,
    })
    return { instance, fetch, readClaudeCredentials }
  }

  it('reads Codex on a timer and never lets an older rollout replace a newer reading', async () => {
    const { instance } = poller({ enabled: false })
    rollout(
      '2026/10/08',
      'rollout-2026-10-08T08-00-00-b.jsonl',
      [tokenCount(40, '2026-10-08T08:30:00.000Z')],
      2_000_000,
    )
    await instance.refresh()
    expect(readUsageLimits(limitsPath)).toMatchObject([
      { harness: 'codex', observedAt: '2026-10-08T08:30:00.000Z', fiveHour: { usedPercent: 40 } },
    ])

    // A run recorded a newer reading meanwhile.
    recordUsageLimits(
      'codex',
      { fiveHour: { usedPercent: 70, resetsAt: null } },
      new Date('2026-10-08T10:00:00Z'),
      limitsPath,
    )
    now += CodexReadIntervalMs
    await instance.refresh()
    expect(readUsageLimits(limitsPath)[0]?.fiveHour?.usedPercent).toBe(70)

    // An interactive turn after it wins, and within the minute not again.
    rollout(
      '2026/10/08',
      'rollout-2026-10-08T13-00-00-c.jsonl',
      [tokenCount(75, '2026-10-08T13:59:00.000Z')],
      3_000_000,
    )
    await instance.refresh()
    expect(readUsageLimits(limitsPath)[0]?.fiveHour?.usedPercent).toBe(70)
    now += CodexReadIntervalMs
    await instance.refresh()
    expect(readUsageLimits(limitsPath)[0]).toMatchObject({
      observedAt: '2026-10-08T13:59:00.000Z',
      fiveHour: { usedPercent: 75 },
    })
  })

  it('reads no credentials and calls nobody while the Claude poll is off', async () => {
    const { instance, fetch, readClaudeCredentials } = poller({ enabled: false })
    await instance.refresh()
    expect(readClaudeCredentials).not.toHaveBeenCalled()
    expect(fetch).not.toHaveBeenCalled()
  })

  it('records the OAuth usage at most once per interval when on', async () => {
    const { instance, fetch } = poller({})
    await Promise.all([instance.refresh(), instance.refresh()])
    expect(fetch).toHaveBeenCalledTimes(1)
    const [url, init] = fetch.mock.calls[0] as [string, RequestInit]
    expect(url).toBe(ClaudeUsageUrl)
    expect(init.headers).toMatchObject({
      Authorization: 'Bearer secret',
      'anthropic-beta': 'oauth-2025-04-20',
    })
    expect(readUsageLimits(limitsPath)).toEqual([
      {
        harness: 'claude',
        observedAt: new Date(NowMs).toISOString(),
        fiveHour: { usedPercent: 22, resetsAt: '2026-10-08T16:30:00.377Z' },
        weekly: { usedPercent: 18, resetsAt: '2026-10-12T00:00:00.000Z' },
      },
    ])
    now += ClaudePollIntervalMs - 1
    await instance.refresh()
    expect(fetch).toHaveBeenCalledTimes(1)
    now += 1
    await instance.refresh()
    expect(fetch).toHaveBeenCalledTimes(2)
  })

  it('skips an expired token without refreshing it, and logs once', async () => {
    const credentials = join(dir, '.credentials.json')
    const stored = '{"claudeAiOauth":{"accessToken":"old","refreshToken":"keep","expiresAt":1}}'
    writeFileSync(credentials, stored)
    const { instance, fetch } = poller({ credentials: credentialsOf(stored) })
    await instance.refresh()
    now += ClaudePollIntervalMs
    await instance.refresh()
    expect(fetch).not.toHaveBeenCalled()
    expect(readFileSync(credentials, 'utf8')).toBe(stored)
    expect(logs.filter((line) => line.includes('expired'))).toHaveLength(1)
    expect(readUsageLimits(limitsPath)).toEqual([])
  })

  it('leaves the snapshot alone on 401 and calls only the usage endpoint', async () => {
    recordUsageLimits(
      'claude',
      { fiveHour: { usedPercent: 9, resetsAt: null } },
      new Date(NowMs - 60_000),
      limitsPath,
    )
    const fetch = vi.fn(async () => response(401, { error: 'invalid token' }))
    const { instance } = poller({ fetch })
    await instance.refresh()
    expect(fetch.mock.calls.map(([url]) => url)).toEqual([ClaudeUsageUrl])
    expect(readUsageLimits(limitsPath)[0]?.fiveHour?.usedPercent).toBe(9)
    expect(logs.join('\n')).toContain('401')
    // Tried again at the usual interval, once Claude Code may have renewed the token.
    now += ClaudePollIntervalMs
    await instance.refresh()
    expect(fetch).toHaveBeenCalledTimes(2)
  })

  it('backs off on 429, honoring retry-after', async () => {
    const fetch = vi.fn(async () => response(429, {}, { 'retry-after': '1200' }))
    const { instance } = poller({ fetch })
    await instance.refresh()
    now += ClaudePollIntervalMs
    await instance.refresh()
    expect(fetch).toHaveBeenCalledTimes(1)
    now += 1_200_000 - ClaudePollIntervalMs
    await instance.refresh()
    expect(fetch).toHaveBeenCalledTimes(2)
    // The second failure waits twice as long as the interval at least.
    now += ClaudePollIntervalMs
    await instance.refresh()
    expect(fetch).toHaveBeenCalledTimes(2)
  })

  it('survives a network failure and an unknown shape', async () => {
    const failing = poller({ fetch: vi.fn(async () => Promise.reject(new Error('offline'))) })
    await expect(failing.instance.refresh()).resolves.toBeUndefined()
    const odd = poller({ fetch: vi.fn(async () => response(200, { usage: [1] })) })
    await odd.instance.refresh()
    expect(logs.join('\n')).toContain('offline')
    expect(logs.join('\n')).toContain('{usage: array}')
    expect(logs.join('\n')).not.toContain('secret')
    expect(readUsageLimits(limitsPath)).toEqual([])
  })

  it('skips a machine without an OAuth sign-in', async () => {
    const { instance, fetch } = poller({ credentials: null })
    await instance.refresh()
    expect(fetch).not.toHaveBeenCalled()
    expect(logs).toHaveLength(1)
  })
})

describe('Claude usage poll setting', () => {
  it('is kept in runner.json and off when absent', () => {
    const path = join(dir, 'runner.json')
    const base = {
      machineId: '4d1c7f2e-9b1a-4c3e-8f5d-2a6b7c8d9e0f',
      profiles: [],
      attachments: { maxCount: 1, maxBytes: 1 },
    }
    writeRunnerConfig(base, path)
    expect(readRunnerConfig(path)?.claudeUsagePoll).toBeUndefined()
    writeRunnerConfig({ ...base, claudeUsagePoll: true }, path)
    expect(JSON.parse(readFileSync(path, 'utf8'))).toMatchObject({ claudeUsagePoll: true })
    expect(readRunnerConfig(path)?.claudeUsagePoll).toBe(true)
  })
})
