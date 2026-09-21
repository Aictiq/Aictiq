import {
  PLAN_DOWNGRADE_BLOCKED,
  PLAN_LIMIT,
  type ExceededLimit,
  type PlanOption,
  type Subscription,
} from '@/api/billing'
import { ApiError } from '@/utils/api'

/**
 * The decisions the Plan page and the shell banner make, kept out of the components so they
 * can be tested without mounting anything.
 *
 * The hosted offer is one flat price per organization with a 30-day
 * evaluation, so there is no higher tier to sell: a refusal says what to delete, never
 * where to upgrade, and the legacy per-seat plans survive only for the organizations that
 * already carry them.
 */

/**
 * Leaving for Stripe Checkout or the Customer Portal: a full navigation, never a fetch —
 * those are Stripe's pages. An object so a test can replace it.
 */
export const navigation = {
  assign: (url: string) => window.location.assign(url),
}

export type PlanChange = 'current' | 'upgrade' | 'downgrade'

/**
 * The flat organization price, or null when this is a legacy per-seat plan. A zero counts
 * as absent: a free legacy plan must not read as a flat plan that happens to cost nothing.
 */
const flatPrice = (plan: PlanOption): number | null =>
  plan.organizationPrice != null && plan.organizationPrice > 0 ? plan.organizationPrice : null

/** What a plan costs a month, whichever way it is priced — which is also how the API orders them. */
const planPrice = (plan: PlanOption) => flatPrice(plan) ?? plan.humanSeatPrice

/** True for the flat hosted offer, false for every legacy per-seat plan. */
export const isFlatPlan = (plan: PlanOption) => flatPrice(plan) != null

/**
 * The plans to put in front of someone: the one we sell, plus whichever plan this
 * organization is already on.
 *
 * `purchasable` alone is not the test. The API also marks `free` purchasable, because
 * checkout uses it as the cancellation target — but a per-seat plan capped at three
 * projects is not something to offer a hosted customer, and a price we will not honour is
 * worse on the page than off it. Cancelling is what the Customer Portal is for.
 */
export function offeredPlans(plans: PlanOption[], currentPlan: string): PlanOption[] {
  return plans.filter((plan) => plan.code === currentPlan || (plan.purchasable && isFlatPlan(plan)))
}

/** "$49 per organization / month" for the hosted offer; the legacy plans still say per human. */
export function planPriceLabel(plan: PlanOption): string {
  const flat = flatPrice(plan)
  if (flat != null) return `$${flat} per organization / month`
  return plan.humanSeatPrice > 0 ? `$${plan.humanSeatPrice} per human / month` : 'Free'
}

export function planChange(current: string, target: PlanOption, plans: PlanOption[]): PlanChange {
  if (target.code === current) return 'current'
  const price = (code: string) => {
    const plan = plans.find((p) => p.code === code)
    return plan ? planPrice(plan) : 0
  }
  return planPrice(target) > price(current) ? 'upgrade' : 'downgrade'
}

/** A refused downgrade's list of what to remove, or null when the error is something else. */
export function exceededFrom(error: unknown): ExceededLimit[] | null {
  if (!(error instanceof ApiError) || error.problem?.type !== PLAN_DOWNGRADE_BLOCKED) return null
  const exceeded = error.problem.exceeded
  return Array.isArray(exceeded) ? (exceeded as ExceededLimit[]) : []
}

const GIB = 1_073_741_824
const gib = (bytes: number) => (bytes / GIB).toFixed(2)

/** "Remove 2 human members (7 of 5 allowed)" — what a person has to do, not which limit tripped. */
export function describeExceeded(limit: ExceededLimit): string {
  const over = limit.used - limit.allowed
  switch (limit.limit) {
    case 'seats_human':
      return `Remove ${over} human member${over === 1 ? '' : 's'} (${limit.used} of ${limit.allowed} allowed)`
    case 'seats_agent':
      return `Remove ${over} agent${over === 1 ? '' : 's'} (${limit.used} of ${limit.allowed} allowed)`
    case 'projects':
      return `Archive ${over} project${over === 1 ? '' : 's'} (${limit.used} of ${limit.allowed} allowed)`
    case 'storage_bytes':
      return `Delete ${gib(over)} GiB of attachments (${gib(limit.used)} of ${gib(limit.allowed)} GiB used) — existing files stay readable`
    default:
      return `Reduce ${limit.limit} from ${limit.used} to ${limit.allowed}`
  }
}

/**
 * The copy for a 402 `plan-limit` refusal, or null when the error is something else.
 *
 * The problem may still carry an `upgradeUrl`, and the browser deliberately ignores it:
 * the hosted offer has no higher tier, so an organization over its attachment allowance is
 * told to delete attachments — pointing at a plan that does not exist would be worse than
 * saying nothing. Nothing here ever returns a URL.
 */
export function planLimitMessage(error: unknown): string | null {
  if (!(error instanceof ApiError) || error.status !== 402 || error.problem?.type !== PLAN_LIMIT) {
    return null
  }
  const detail = typeof error.problem.detail === 'string' ? error.problem.detail.trim() : ''
  if (detail) return detail
  if (error.problem.limit === 'storage_bytes') {
    return 'This organization has reached its attachment allowance. Delete attachments to free space — existing files stay readable.'
  }
  return error.title
}

export interface PaymentBanner {
  tone: 'warning' | 'danger'
  message: string
  /** Owners can fix it; everyone else is told whom to ask. */
  action: 'manage' | 'ask-owner' | 'plan'
}

const DAY = 86_400_000
const daysUntil = (date: Date, now: Date) =>
  Math.max(0, Math.ceil((date.getTime() - now.getTime()) / DAY))
const plural = (count: number) => `${count} day${count === 1 ? '' : 's'}`

/** What the shell says about an organization whose *payment* failed; see {@link shellBanner}. */
export function paymentBanner(
  subscription:
    | Pick<Subscription, 'enabled' | 'readOnly' | 'graceEndsAt' | 'paymentFailedAt'>
    | null
    | undefined,
  isOwner: boolean,
  now: Date = new Date(),
): PaymentBanner | null {
  // `graceEndsAt` is the usual signal, but a failed payment with no grace window recorded is
  // still a failed payment — the cause has to be visible either way, or the evaluation
  // banner would claim a read-only organization that lapsed for a different reason.
  if (!subscription?.enabled || (!subscription.graceEndsAt && !subscription.paymentFailedAt))
    return null
  const action = isOwner ? 'manage' : 'ask-owner'
  if (subscription.readOnly) {
    return {
      tone: 'danger',
      action,
      message: isOwner
        ? 'A payment failed and the grace period has ended — this organization is read-only until the payment method is updated.'
        : 'A payment failed and the grace period has ended — this organization is read-only until an owner updates the payment method.',
    }
  }
  if (!subscription.graceEndsAt) {
    return {
      tone: 'warning',
      action,
      message: `A payment failed. This organization becomes read-only unless ${isOwner ? 'the payment method is updated' : 'an owner updates the payment method'}.`,
    }
  }
  const ends = new Date(subscription.graceEndsAt)
  return {
    tone: 'warning',
    action,
    message: `A payment failed. This organization becomes read-only in ${plural(daysUntil(ends, now))} (${ends.toLocaleDateString()}) unless ${isOwner ? 'the payment method is updated' : 'an owner updates the payment method'}.`,
  }
}

export interface EvaluationNotice {
  expired: boolean
  daysLeft: number
  endsAt: Date
}

/** The evaluation's countdown, or null when this organization has none (self-host, paid). */
export function evaluationNotice(
  subscription: Pick<Subscription, 'evaluation'> | null | undefined,
  now: Date = new Date(),
): EvaluationNotice | null {
  if (!subscription?.evaluation) return null
  const endsAt = new Date(subscription.evaluation.endsAt)
  return { expired: subscription.evaluation.expired, daysLeft: daysUntil(endsAt, now), endsAt }
}

/** How close the end has to be before the shell says anything. */
export const EVALUATION_WARNING_DAYS = 7

/**
 * An expired evaluation pauses writes the way a failed payment does, but the cause and the
 * remedy are different: nothing is wrong in Stripe and no card will be charged on its own —
 * the way forward is subscribing, which only an owner can do. The last week counts down, so
 * the day writes stop is not the day anyone hears about it.
 */
export function evaluationBanner(
  subscription: Pick<Subscription, 'enabled' | 'readOnly' | 'evaluation'> | null | undefined,
  isOwner: boolean,
  now: Date = new Date(),
): PaymentBanner | null {
  if (!subscription?.enabled) return null
  const notice = evaluationNotice(subscription, now)
  if (!notice) return null
  const action = isOwner ? 'plan' : 'ask-owner'
  if (notice.expired) {
    const paused = subscription.readOnly
      ? ' Reading, downloading and exporting still work; new changes and agent runs are paused.'
      : ''
    return {
      tone: subscription.readOnly ? 'danger' : 'warning',
      action,
      message: `This organization's evaluation has ended.${paused} ${isOwner ? 'Subscribe to continue.' : 'Ask an owner to subscribe.'}`,
    }
  }
  if (notice.daysLeft > EVALUATION_WARNING_DAYS) return null
  return {
    tone: 'warning',
    action,
    message: `This organization's evaluation ends in ${plural(notice.daysLeft)} (${notice.endsAt.toLocaleDateString()}). Nothing is charged automatically — ${isOwner ? 'subscribe' : 'ask an owner to subscribe'} to keep writing.`,
  }
}

/**
 * What the shell says across the top of the app. A failed payment is checked first: both
 * causes end in the same `readOnly`, and an organization whose card was declined must not
 * be told its evaluation ran out.
 */
export function shellBanner(
  subscription:
    | Pick<Subscription, 'enabled' | 'readOnly' | 'graceEndsAt' | 'paymentFailedAt' | 'evaluation'>
    | null
    | undefined,
  isOwner: boolean,
  now: Date = new Date(),
): PaymentBanner | null {
  return paymentBanner(subscription, isOwner, now) ?? evaluationBanner(subscription, isOwner, now)
}

export interface FoundingNotice {
  price: number
  renewalPrice: number
  periods: number
  periodsBilled: number
  periodsLeft: number
  converted: boolean
  /**
   * The renewal that first charges the standard price: the date the offer ends. Null when
   * the subscription has no period to count from.
   */
  standardFrom: Date | null
}

/**
 * The founding offer, as the Plan page states it: what it costs, how much of it is spent
 * and when the standard price takes over. The server grants it — there is nothing here
 * that asks for one, and nothing a browser could send that would produce one.
 */
export function foundingNotice(
  subscription: Pick<Subscription, 'founding' | 'currentPeriodEnd'> | null | undefined,
): FoundingNotice | null {
  const founding = subscription?.founding
  if (!founding) return null
  const periodsLeft = Math.max(0, founding.periods - founding.periodsBilled)
  const converted = founding.convertedAt != null
  // The end date is the server's arithmetic, not ours: it knows how many periods it has
  // collected and when the current one ends, and a date the browser derives differently
  // from the one the invoice will carry is worse than no date at all.
  const standardFrom = converted
    ? new Date(founding.convertedAt!)
    : founding.endsAt
      ? new Date(founding.endsAt)
      : null
  return {
    price: founding.price,
    renewalPrice: founding.renewalPrice,
    periods: founding.periods,
    periodsBilled: founding.periodsBilled,
    periodsLeft,
    converted,
    standardFrom,
  }
}
