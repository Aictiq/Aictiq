import type { EChartsOption } from 'echarts'

import type { RunStats, RunStatsDay, RunStatus } from '@/api/runs'
import { runStatuses, runStatusLabel } from '@/lib/runs'

/**
 * Factory → Runs statistics: the date range the list and the stats share, and how the
 * figures read. Kept out of the components for the same reason as `lib/runs.ts`.
 */

export const runRanges = ['24h', '7d', '30d', '90d', 'all'] as const
export type RunRange = (typeof runRanges)[number]

/** The range a URL without one means. It stays out of the URL, so a bare link is the default view. */
export const defaultRunRange: RunRange = '30d'

export const runRangeLabel: Record<RunRange, string> = {
  '24h': 'Last 24 hours',
  '7d': 'Last 7 days',
  '30d': 'Last 30 days',
  '90d': 'Last 90 days',
  all: 'All time',
}

const rangeHours: Record<Exclude<RunRange, 'all'>, number> = {
  '24h': 24,
  '7d': 7 * 24,
  '30d': 30 * 24,
  '90d': 90 * 24,
}

/** `yyyy-mm-dd` of a moment in the browser's time zone - the days the stats are counted in. */
export function localDay(at: Date): string {
  const pad = (value: number) => String(value).padStart(2, '0')
  return `${at.getFullYear()}-${pad(at.getMonth() + 1)}-${pad(at.getDate())}`
}

/** Local midnight at the start of a `yyyy-mm-dd` day; null for anything that is not one. */
export function startOfLocalDay(day: string): Date | null {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(day)
  if (!match) return null
  const at = new Date(Number(match[1]), Number(match[2]) - 1, Number(match[3]))
  return Number.isNaN(at.getTime()) || localDay(at) !== day ? null : at
}

function addDays(at: Date, days: number): Date {
  const next = new Date(at)
  next.setDate(next.getDate() + days)
  return next
}

export interface RunWindow {
  /** ISO instant, inclusive; absent for all time. */
  from?: string
  /** ISO instant, exclusive; absent for up to now. */
  to?: string
}

/**
 * The instants a range means. A preset rolls back from `now`; a pair of days (a chart
 * drill-down) runs from the first day's local midnight to the end of the last day.
 */
export function runWindow(
  range: RunRange,
  days: { from?: string; to?: string } = {},
  now: Date = new Date(),
): RunWindow {
  const fromDay = days.from ? startOfLocalDay(days.from) : null
  const toDay = days.to ? startOfLocalDay(days.to) : null
  if (fromDay || toDay) {
    return {
      from: fromDay?.toISOString(),
      to: toDay ? addDays(toDay, 1).toISOString() : undefined,
    }
  }
  if (range === 'all') return {}
  return { from: new Date(now.getTime() - rangeHours[range] * 3_600_000).toISOString() }
}

/**
 * Every day of the window, oldest first, so a quiet day is a zero on the chart rather than a
 * gap the axis closes up. All time starts at the first day with a run.
 */
export function windowDays(
  window: RunWindow,
  data: RunStatsDay[],
  now: Date = new Date(),
): string[] {
  const first = window.from
    ? new Date(window.from)
    : data.length
      ? startOfLocalDay(data.reduce((min, day) => (day.day < min ? day.day : min), data[0]!.day))
      : null
  if (!first) return []
  const last = window.to ? new Date(new Date(window.to).getTime() - 1) : now
  const days: string[] = []
  // A runaway loop over a bad window is worse than a short axis.
  for (
    let at = startOfLocalDay(localDay(first))!;
    at <= last && days.length < 3660;
    at = addDays(at, 1)
  ) {
    days.push(localDay(at))
  }
  return days
}

/** "–" for a ratio over nothing: no finished runs is not a 0 % success rate. */
export function formatPercent(part: number, whole: number): string {
  return whole === 0 ? '–' : `${Math.round((part / whole) * 100)} %`
}

export function formatUsd(value: number | null | undefined): string {
  if (value === null || value === undefined) return '–'
  return `$${value.toFixed(value !== 0 && Math.abs(value) < 1 ? 3 : 2)}`
}

/** "1.2M", "84k", "512". */
export function formatTokens(value: number): string {
  if (value >= 1_000_000) return `${(value / 1_000_000).toFixed(1).replace(/\.0$/, '')}M`
  if (value >= 1_000) return `${(value / 1_000).toFixed(1).replace(/\.0$/, '')}k`
  return String(value)
}

/** "42 s", "4 min", "1 h 12 m" - the same wording as a run's own duration. */
export function formatSeconds(value: number | null | undefined): string {
  if (value === null || value === undefined) return '–'
  const seconds = Math.round(value)
  if (seconds < 60) return `${seconds} s`
  const minutes = Math.floor(seconds / 60)
  if (minutes < 60) return `${minutes} min`
  return `${Math.floor(minutes / 60)} h ${minutes % 60} m`
}

/**
 * Chart colours by status. ECharts paints on a canvas, so these are literals; they are the
 * mid tones that read on both themes.
 */
export const runStatusColour: Record<RunStatus, string> = {
  queued: '#94a3b8',
  assigned: '#7c3aed',
  running: '#2563eb',
  succeeded: '#16a34a',
  failed: '#dc2626',
  cancelled: '#64748b',
  timedOut: '#d97706',
}

const base = (): EChartsOption => ({
  textStyle: { fontFamily: 'Instrument Sans, sans-serif' },
  tooltip: { trigger: 'axis', axisPointer: { type: 'shadow' } },
  grid: { left: 48, right: 16, top: 16, bottom: 52 },
})

const axisDay = (day: string) => day.slice(5)

/** Runs per day, stacked by status. Each series is named by its status so a click can say which. */
export function runsPerDayOption(stats: RunStats, days: string[]): EChartsOption {
  const present = runStatuses.filter((status) => stats.days.some((day) => day.status === status))
  return {
    ...base(),
    legend: { bottom: 0, itemWidth: 12, itemHeight: 8 },
    xAxis: { type: 'category', data: days.map(axisDay) },
    yAxis: { type: 'value', minInterval: 1 },
    series: present.map((status) => ({
      id: status,
      name: runStatusLabel[status],
      type: 'bar',
      stack: 'runs',
      itemStyle: { color: runStatusColour[status] },
      emphasis: { focus: 'series' },
      data: days.map(
        (day) => stats.days.find((row) => row.day === day && row.status === status)?.runs ?? 0,
      ),
    })),
  }
}

export function costPerDayOption(stats: RunStats, days: string[]): EChartsOption {
  return {
    ...base(),
    grid: { left: 56, right: 16, top: 16, bottom: 28 },
    tooltip: {
      trigger: 'axis',
      axisPointer: { type: 'shadow' },
      valueFormatter: (value) => formatUsd(Number(value)),
    },
    xAxis: { type: 'category', data: days.map(axisDay) },
    yAxis: { type: 'value', axisLabel: { formatter: (value: number) => `$${value}` } },
    series: [
      {
        name: 'Cost',
        type: 'bar',
        itemStyle: { color: '#2563eb' },
        data: days.map((day) =>
          Number(
            stats.days
              .filter((row) => row.day === day)
              .reduce((sum, row) => sum + row.costUsd, 0)
              .toFixed(4),
          ),
        ),
      },
    ],
  }
}
