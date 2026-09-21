import { apiFetch } from '@/utils/api'

/**
 * The signed-in person's product-tour preferences.
 *
 * One small record per account: whether the welcome tour was started, put off, dismissed
 * or finished, and which step it stopped on. There is no record until the first PATCH,
 * and a GET before that answers `not_started` with version 0. `version` is xmin — echo it
 * back on every PATCH or the API answers 409, because two tabs writing at once must not
 * both decide how the tour ended.
 */

export type OnboardingStatus =
  | 'not_started'
  | 'in_progress'
  | 'deferred'
  | 'dismissed'
  | 'completed'

export type TourStepId =
  | 'navigation'
  | 'organization'
  | 'project-team'
  | 'board'
  | 'prepare-item'
  | 'project-knowledge'
  | 'factory-concepts'
  | 'runner-setup'
  | 'handoff'
  | 'follow-up'

export interface OnboardingPreferences {
  tourVersion: number
  status: OnboardingStatus
  lastStepId: string | null
  completedAt: string | null
  updatedAt: string
  /** xmin. Echo it back on every PATCH or the API answers 409. */
  version: number
}

export interface UpdateOnboardingBody {
  status: OnboardingStatus
  lastStepId?: string | null
  version: number
}

export const getOnboarding = () => apiFetch<OnboardingPreferences>('/me/onboarding')

export const updateOnboarding = (body: UpdateOnboardingBody) =>
  apiFetch<OnboardingPreferences>('/me/onboarding', { method: 'PATCH', body })
