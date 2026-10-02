import type { RefinementStatus } from '@/api/refinement'

export const refinementLabels: Record<RefinementStatus, string> = {
  refining: 'Refining',
  needsInput: 'Needs your input',
  ready: 'Ready for review',
  failed: 'Refinement failed',
  confirmed: 'Confirmed',
}
