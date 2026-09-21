import { describe, expect, it } from 'vitest'
import { burndownOption, cfdOption, cycleOption, throughputOption, velocityOption } from '@/lib/analytics'

describe('analytics chart adapters', () => {
  it('keeps dates and values aligned for a burndown exportable chart', () => {
    const option = burndownOption([{ day: '2026-09-01', scope: 8, remaining: 5, completed: 3, idealRemaining: 6, scopeChanges: [] }])
    expect(option.xAxis).toMatchObject({ data: ['09-01'] })
    expect(option.series).toMatchObject([{ data: [5] }, { data: [6] }, { data: [8] }])
  })
  it('produces empty-safe options and exposes all CFD categories', () => {
    expect(cfdOption([]).series).toEqual([])
    expect(cfdOption([{ day: '2026-09-01', counts: { active: 2, completed: 1 } }]).series).toHaveLength(2)
    expect(cycleOption([]).series).toMatchObject([{ data: [] }])
    expect(throughputOption([]).series).toHaveLength(2)
    expect(velocityOption([]).series).toHaveLength(2)
  })
})
