import { describe, expect, it } from 'vitest'

import {
  formatPercent,
  formatSeconds,
  formatTokens,
  formatUsd,
  localDay,
  runWindow,
  startOfLocalDay,
  windowDays,
} from '@/lib/runStats'

/**
 * The window the stats and the list share decides which runs are counted, so it is asserted
 * on its own: presets roll back from now, a chart's day pair covers whole local days.
 */
describe('runWindow', () => {
  const now = new Date(2026, 9, 2, 15, 30)

  it('rolls a preset back from now and leaves all time open', () => {
    expect(runWindow('24h', {}, now)).toEqual({
      from: new Date(2026, 9, 1, 15, 30).toISOString(),
    })
    expect(runWindow('7d', {}, now).from).toBe(
      new Date(now.getTime() - 7 * 86_400_000).toISOString(),
    )
    expect(runWindow('all', {}, now)).toEqual({})
  })

  it('turns a day pair into whole local days, and it overrides the preset', () => {
    expect(runWindow('30d', { from: '2026-09-28', to: '2026-09-28' }, now)).toEqual({
      from: new Date(2026, 8, 28).toISOString(),
      to: new Date(2026, 8, 29).toISOString(),
    })
  })

  it('ignores a day that is not one', () => {
    expect(startOfLocalDay('2026-02-30')).toBeNull()
    expect(startOfLocalDay('yesterday')).toBeNull()
    expect(runWindow('all', { from: 'nonsense' }, now)).toEqual({})
  })
})

describe('windowDays', () => {
  it('lists every day of the window, quiet ones included', () => {
    const now = new Date(2026, 9, 2, 15, 30)
    const window = runWindow('7d', {}, now)
    const days = windowDays(window, [], now)
    expect(days).toHaveLength(8)
    expect(days[0]).toBe('2026-09-25')
    expect(days.at(-1)).toBe(localDay(now))
  })

  it('starts all time at the first day with a run', () => {
    const now = new Date(2026, 9, 2, 15, 30)
    const days = windowDays(
      {},
      [
        { day: '2026-09-30', status: 'succeeded', runs: 1, costUsd: 0 },
        { day: '2026-09-29', status: 'failed', runs: 1, costUsd: 0 },
      ],
      now,
    )
    expect(days).toEqual(['2026-09-29', '2026-09-30', '2026-10-01', '2026-10-02'])
    expect(windowDays({}, [], now)).toEqual([])
  })
})

describe('formatting', () => {
  it('says "–" rather than a misleading zero', () => {
    expect(formatPercent(0, 0)).toBe('–')
    expect(formatPercent(2, 3)).toBe('67 %')
    expect(formatUsd(null)).toBe('–')
    expect(formatSeconds(null)).toBe('–')
  })

  it('reads at a glance', () => {
    expect(formatUsd(12.5)).toBe('$12.50')
    expect(formatUsd(0.042)).toBe('$0.042')
    expect(formatUsd(0)).toBe('$0.00')
    expect(formatTokens(512)).toBe('512')
    expect(formatTokens(84_000)).toBe('84k')
    expect(formatTokens(1_250_000)).toBe('1.3M')
    expect(formatSeconds(42)).toBe('42 s')
    expect(formatSeconds(4380)).toBe('1 h 13 m')
  })
})
