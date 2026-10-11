import { apiFetch } from '@/utils/api'

/**
 * Fleet mode: an opt-in organization setting for running many runners as one pool. Runners
 * carry tags, rules, playbooks and Start run can require them, dispatch prefers the least
 * loaded runner, and a scale webhook can start machines when runs wait with nobody to take
 * them.
 *
 * Only paid hosted plans can turn it on. A self-hosted instance answers `hidden` and every
 * fleet route but the status one answers 404 there; self-hosted support may come later.
 */

/** `hidden`: not offered on this instance; `upgrade`: needs a paid hosted plan; `available`. */
export type FleetAvailability = 'hidden' | 'upgrade' | 'available'

/** What anyone who may start a run needs to know: whether to show the tag fields at all. */
export interface FleetStatus {
  availability: FleetAvailability
  enabled: boolean
  /** Fleet rules are in force: enabled, and the plan allows it. */
  active: boolean
  /** Enabled, but the plan no longer allows it: runs dispatch as if fleet mode were off. */
  downgraded: boolean
  /** Every tag a runner of the organization has ever had. Only filled while active. */
  knownTags: string[]
}

/** The Admin's view. Secrets never come back here, only whether they exist. */
export interface FleetSettings {
  availability: FleetAvailability
  enabled: boolean
  active: boolean
  downgraded: boolean
  /** Called when runs wait with no runner to take them; null while scale from zero is off. */
  scaleWebhookUrl: string | null
  hasWebhookSecret: boolean
  /** How long the oldest run waits before the webhook is called. */
  scaleAfterSeconds: number
  /** The least time between two calls. */
  scaleCooldownSeconds: number
  lastScaleCallAt: string | null
  lastScaleCallError: string | null
  /** `jfe_a1b2…` - all that survives of the enrollment token; null when there is none. */
  enrollTokenDisplay: string | null
}

export interface UpdateFleetBody {
  enabled?: boolean
  /** An empty string turns scale from zero off. */
  scaleWebhookUrl?: string
  /** 30 to 86400. */
  scaleAfterSeconds?: number
  /** 60 to 86400. */
  scaleCooldownSeconds?: number
}

export interface FleetSettingsSaved {
  settings: FleetSettings
  /** Set once, when this save minted the webhook's signing secret. Shown once. */
  webhookSecret: string | null
}

/** A secret shown exactly once. */
export interface FleetSecretIssued {
  secret: string
  display: string | null
}

const fleetBase = (slug: string) => `/orgs/${slug}/fleet`

/** Open to every Member. */
export const getFleetStatus = (slug: string) => apiFetch<FleetStatus>(`${fleetBase(slug)}/status`)

export const getFleetSettings = (slug: string) => apiFetch<FleetSettings>(fleetBase(slug))

/** 402 `plan-limit` when enabling without a paid plan; 404 on a self-hosted instance. */
export const updateFleetSettings = (slug: string, body: UpdateFleetBody) =>
  apiFetch<FleetSettingsSaved>(fleetBase(slug), { method: 'PUT', body })

/** A new signing secret for the scale webhook; the old one stops signing at once. */
export const rotateFleetWebhookSecret = (slug: string) =>
  apiFetch<FleetSecretIssued>(`${fleetBase(slug)}/webhook-secret`, { method: 'POST' })

/** Mints (or replaces) the token ephemeral runners enroll with. Fleet mode must be active. */
export const rotateFleetEnrollToken = (slug: string) =>
  apiFetch<FleetSecretIssued>(`${fleetBase(slug)}/enroll-token`, { method: 'POST' })

/** Runners already enrolled keep working; no new one can enroll. */
export const revokeFleetEnrollToken = (slug: string) =>
  apiFetch<void>(`${fleetBase(slug)}/enroll-token`, { method: 'DELETE' })
