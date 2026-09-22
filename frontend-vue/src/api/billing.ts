import { apiFetch } from '@/utils/api'

/**
 * Stripe subscriptions. The browser never talks to Stripe itself: the API hands
 * back a Checkout or Customer Portal URL and the page navigates to it, so there is no Stripe
 * script, no publishable key and nothing to add to the CSP.
 */

/** Stripe's statuses, as the API names them (camelCase), plus `none` for "never subscribed". */
export type SubscriptionStatus =
  | 'none'
  | 'incomplete'
  | 'incompleteExpired'
  | 'trialing'
  | 'active'
  | 'pastDue'
  | 'canceled'
  | 'unpaid'
  | 'paused'

export interface PlanLimits {
  seatsHuman?: number | null
  seatsAgent?: number | null
  projects?: number | null
  storageBytes?: number | null
  /** Days finished-run raw logs are kept; null when unlimited or not metered. */
  runLogDays?: number | null
  /** Days of analytics history; null when unlimited or not metered. */
  analyticsDays?: number | null
  features?: string[] | null
}

export interface PlanOption {
  code: string
  /** Legacy per-seat pricing; zero on the flat hosted plan, which charges per organization. */
  humanSeatPrice: number
  /** Positive on a flat plan (one price per organization); null or zero on legacy per-seat plans. */
  organizationPrice: number | null
  /** Agents that come free with each human seat; null when agents are never billed separately. */
  includedAgentsPerHuman: number | null
  limits: PlanLimits
  /** Whether this instance sells it: Free, or a plan with a configured Stripe price. */
  purchasable: boolean
}

export interface Subscription {
  mode: 'self_hosted' | 'saas'
  /** False on a self-hosted instance, or a SaaS one without Stripe keys: hide every billing action. */
  enabled: boolean
  /** The plan limits are enforced against right now. */
  plan: string
  /** The plan the Stripe subscription is for; null without one. */
  subscribedPlan: string | null
  status: SubscriptionStatus
  currentPeriodEnd: string | null
  cancelAtPeriodEnd: boolean
  seats: { human: number; agent: number }
  paymentFailedAt: string | null
  /** From this moment the organization is read-only until the payment is fixed. */
  graceEndsAt: string | null
  readOnly: boolean
  hasBillingAccount: boolean
  /**
   * Every plan the organization may see: the one purchasable hosted plan, plus whichever
   * legacy plan it still carries. A plan with `purchasable: false` is history, not an offer.
   */
  plans: PlanOption[]
  /**
   * The one 30-day hosted evaluation; null once it has been converted, or on an
   * organization that never had one. `expired` is the server's decision, not a date
   * comparison the browser makes, and it is what turns `readOnly` on without a payment failure.
   */
  evaluation: { startedAt: string; endsAt: string; expired: boolean } | null
  /**
   * The server-granted founding discount: `price` for `periods` monthly periods, of which
   * `periodsBilled` have been charged, then `renewalPrice`. Null without one - and the
   * browser can never ask for one, there is no request that grants it.
   */
  founding: {
    price: number
    periods: number
    periodsBilled: number
    renewalPrice: number
    convertedAt: string | null
    /**
     * When the discounted price stops, computed by the server from the current period and
     * the periods still to collect. Null before Stripe has reported a period end, and null
     * once the offer has already converted (`convertedAt` is the date then).
     */
    endsAt: string | null
  } | null
}

/** Where to send the browser; null when the change was made in place and a webhook will confirm it. */
export interface BillingRedirect {
  url: string | null
  changed: boolean
}

/** One entry of a refused downgrade's `exceeded` list. */
export interface ExceededLimit {
  limit: 'seats_human' | 'seats_agent' | 'projects' | 'storage_bytes' | string
  used: number
  allowed: number
}

export const PLAN_DOWNGRADE_BLOCKED = 'https://aictiq.com/problems/plan-downgrade-blocked'
/** 402: an action refused by a plan allowance - attachments over the 10 GiB allowance, mostly. */
export const PLAN_LIMIT = 'https://aictiq.com/problems/plan-limit'
export const ORG_READ_ONLY = 'https://aictiq.com/problems/org-read-only'

/** Every member may read this - the payment-failed banner is for everyone about to lose writes. */
export const getSubscription = (slug: string) =>
  apiFetch<Subscription>(`/orgs/${slug}/billing/subscription`)

/** Owner only. A 409 `plan-downgrade-blocked` carries `exceeded`; see `exceededFrom`. */
export const startCheckout = (slug: string, plan: string) =>
  apiFetch<BillingRedirect>(`/orgs/${slug}/billing/checkout`, { method: 'POST', body: { plan } })

/** Owner only. Stripe's Customer Portal: invoices, payment method, cancellation. */
export const openBillingPortal = (slug: string) =>
  apiFetch<BillingRedirect>(`/orgs/${slug}/billing/portal`, { method: 'POST' })
