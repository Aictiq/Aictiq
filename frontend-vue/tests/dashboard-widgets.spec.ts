import { describe, expect, it, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import DashboardWidgetRenderer from '@/components/dashboard/DashboardWidgetRenderer.vue'
import { compactOption, velocityOption } from '@/lib/analytics'

// happy-dom has no canvas for echarts to draw on; the chart's options are covered in analytics.spec.ts.
vi.mock('vue-echarts', () => ({ default: { name: 'VChart', template: '<div class="chart-stub" />' } }))

const sprint = { id: 's1', name: 'Sprint 4', teamName: 'Core', startsOn: '2026-10-05', endsOn: '2026-10-19' }
const render = (type: string, data: unknown) => mount(DashboardWidgetRenderer, { props: { type, data, config: {} } })

describe('dashboard widgets', () => {
  it('names assignees instead of numbering them', () => {
    const text = render('items-by-assignee', [
      { assigneeId: 'u1', displayName: 'Ana Horvat', count: 3 },
      { assigneeId: null, displayName: null, count: 2 },
      { assigneeId: 'gone', displayName: null, count: 1 },
    ]).text()
    expect(text).toContain('Ana Horvat')
    expect(text).toContain('Unassigned')
    expect(text).toContain('Former member')
    expect(text).toContain('6 total')
    expect(text).not.toContain('Member 1')
  })

  it('shows the active sprint health figures', () => {
    const text = render('sprint-health', { sprint, health: { sprintId: 's1', percentDone: 37.5, daysElapsed: 3, workingDays: 10, projectedCompletionOn: null, blockedCount: 1, unestimatedCount: 2, unassignedCount: 4, agentSharePercent: 0 } }).text()
    expect(text).toContain('37.5%')
    expect(text).toContain('Sprint 4')
    expect(text).toContain('day 3 of 14')
    expect(text).toMatch(/Blocked\s*1/)
    expect(text).toMatch(/Unassigned\s*4/)
  })

  it('charts the burndown and reports what is left today', () => {
    const wrapper = render('burndown', { sprint, unit: 'points', days: [
      { day: '2026-10-05', scope: 8, remaining: 8, completed: 0, idealRemaining: 8, scopeChanges: [] },
      { day: '2026-10-06', scope: 8, remaining: 5, completed: 3, idealRemaining: 7, scopeChanges: [] },
      { day: '2026-10-07', scope: 8, remaining: null, completed: null, idealRemaining: 6, scopeChanges: [] },
    ] })
    expect(wrapper.text()).toContain('5 points remaining')
    expect(wrapper.find('.chart-stub').exists()).toBe(true)
  })

  it('summarizes cycle time and explains empty sprint and velocity widgets', () => {
    expect(render('cycle-time', { leadTime: { p50: 4, p85: 6, p95: 9 }, cycleTime: { p50: 2.25, p85: 3, p95: 5 }, completedCount: 7, throughput: [] }).text()).toMatch(/Median cycle\s*2\.3d/)
    expect(render('burndown', { sprint: null }).text()).toContain('Start a sprint')
    expect(render('velocity', { teamName: 'Core', velocity: { sprints: [], averageVelocity: 0, rollingAverageVelocity: 0, forecast: null } }).text()).toContain('Complete a sprint')
    expect(render('cfd', { days: [{ day: '2026-10-07', counts: {} }] }).text()).toContain('Daily snapshots')
  })

  it('keeps a compact chart inside its card', () => {
    expect(compactOption(velocityOption([])).grid).toMatchObject({ bottom: 40 })
  })
})
