import {
  PLAN_DOWNGRADE_BLOCKED,
  PLAN_LIMIT,
  type ExceededLimit,
  type PlanOption,
  type Subscription,
  type SubscriptionStatus,
} from '@/api/billing'
import { ApiError, type ProblemDetails } from '@/utils/api'

/**
 * The decisions the Plan page and the shell banner make, kept out of the components so they
 * can be tested without mounting anything.
 *
 * The hosted offer is one flat price per organization with a 30-day evaluation, and where
 * the free tier runs, an evaluation that ends without a subscription lands on the free plan
 * (`hosted_free`). Hosted itself has no higher tier to sell, so a paid organization refused
 * for attachments is told what to delete, never where to upgrade; only a refusal caused by
 * a free-plan limit points at Hosted. The legacy per-seat plans survive only for the
 * organizations that already carry them.
 */

/** The hosted free tier's plan code. */
export const FREE_PLAN = 'hosted_free'

/**
 * Leaving for Stripe Checkout or the Customer Portal: a full navigation, never a fetch -
 * those are Stripe's pages. An object so a test can replace it.
 */
export const navigation = {
  assign: (url: string) => window.location.assign(url),
}

export type PlanChange = 'current' | 'upgrade' | 'downgrade'

/** What a plan is called on the page: the free tier is "Free", the rest by their code. */
export const planName = (code: string) =>
  code === FREE_PLAN ? 'Free' : code.charAt(0).toUpperCase() + code.slice(1)

/**
 * The flat organization price, or null when this is a legacy per-seat plan. A zero counts
 * as absent: a free legacy plan must not read as a flat plan that happens to cost nothing.
 */
const flatPrice = (plan: PlanOption): number | null =>
  plan.organizationPrice != null && plan.organizationPrice > 0 ? plan.organizationPrice : null

/** What a plan costs a month, whichever way it is priced - which is also how the API orders them. */
const planPrice = (plan: PlanOption) => flatPrice(plan) ?? plan.humanSeatPrice

/** True for the flat hosted offer, false for every legacy per-seat plan. */
export const isFlatPlan = (plan: PlanOption) => flatPrice(plan) != null

/**
 * The plans to put in front of someone: the one we sell, plus whichever plan this
 * organization is already on.
 *
 * `purchasable` alone is not the test. The API also marks `free` purchasable, because
 * checkout uses it as the cancellation target - but a per-seat plan capped at three
 * projects is not something to offer a hosted customer, and a price we will not honour is
 * worse on the page than off it. Cancelling is what the Customer Portal is for.
 */
export function offeredPlans(plans: PlanOption[], currentPlan: string): PlanOption[] {
  return plans.filter((plan) => plan.code === currentPlan || (plan.purchasable && isFlatPlan(plan)))
}

/** "$79 per organization / month" for the hosted offer; the legacy plans still say per human. */
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

/** "Remove 2 human members (7 of 5 allowed)" - what a person has to do, not which limit tripped. */
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
      return `Delete ${gib(over)} GiB of attachments (${gib(limit.used)} of ${gib(limit.allowed)} GiB used) - existing files stay readable`
    default:
      return `Reduce ${limit.limit} from ${limit.used} to ${limit.allowed}`
  }
}

const isPlanLimit = (error: unknown): error is ApiError & { problem: ProblemDetails } =>
  error instanceof ApiError && error.status === 402 && error.problem?.type === PLAN_LIMIT

/**
 * The copy for a 402 `plan-limit` refusal, or null when the error is something else. The
 * server's `detail` already says what happened and what to do about it; whether to offer
 * the upgrade as well is {@link planLimitUpgrade}'s decision, and the URL is never taken
 * from the problem - the link is always this organization's own Plan page.
 */
export function planLimitMessage(error: unknown): string | null {
  if (!isPlanLimit(error)) return null
  const detail = typeof error.problem.detail === 'string' ? error.problem.detail.trim() : ''
  if (detail) return detail
  if (error.problem.limit === 'storage_bytes') {
    return 'This organization has reached its attachment allowance. Delete attachments to free space - existing files stay readable.'
  }
  return error.title
}

/**
 * Whether a 402 `plan-limit` refusal was caused by a free-plan limit, which paying for
 * Hosted lifts: the people limit, the runner limit, or the pooled free attachment
 * allowance. Paid Hosted refuses attachments too, but with no `upgradeUrl`, because there
 * is no larger allowance to buy - that refusal says what to delete and offers nothing else.
 * A self-hosted instance never refuses at all.
 */
export function planLimitUpgrade(error: unknown): boolean {
  if (!isPlanLimit(error)) return false
  switch (error.problem.limit) {
    case 'free_people':
    case 'runners':
      return true
    case 'storage_bytes':
      return typeof error.problem.upgradeUrl === 'string' && error.problem.upgradeUrl !== ''
    default:
      return false
  }
}

/** A plan-limit refusal as a page shows it: the server's words, and whether to offer Hosted. */
export interface PlanLimitRefusal {
  message: string
  upgrade: boolean
}

export function planLimitRefusal(error: unknown): PlanLimitRefusal | null {
  const message = planLimitMessage(error)
  return message == null ? null : { message, upgrade: planLimitUpgrade(error) }
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

const KIB = 1_024
const MIB = 1_048_576
/**
 * "200 MiB", "1.5 GiB", "5.9 KiB" - the free allowance is small enough that GiB would read
 * as 0.2, and an operator may tune it below a mebibyte.
 */
export function sizeLabel(bytes: number): string {
  const [value, unit] =
    bytes >= GIB
      ? [bytes / GIB, 'GiB']
      : bytes >= MIB || bytes === 0
        ? [bytes / MIB, 'MiB']
        : [bytes / KIB, 'KiB']
  return `${Number.isInteger(value) ? value : value.toFixed(1)} ${unit}`
}

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
  // still a failed payment - the cause has to be visible either way, or the evaluation
  // banner would claim a read-only organization that lapsed for a different reason.
  if (!subscription?.enabled || (!subscription.graceEndsAt && !subscription.paymentFailedAt))
    return null
  const action = isOwner ? 'manage' : 'ask-owner'
  if (subscription.readOnly) {
    return {
      tone: 'danger',
      action,
      message: isOwner
        ? 'A payment failed and the grace period has ended - this organization is read-only until the payment method is updated.'
        : 'A payment failed and the grace period has ended - this organization is read-only until an owner updates the payment method.',
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

/**
 * Whether a Stripe subscription has ever taken effect: paid, paying late, paused or
 * cancelled since. The evaluation row outlives the subscription that replaced it, but once
 * an organization has bought Hosted its evaluation is history - a paid organization is not
 * counting down to anything, and one that cancelled back to Free is not on an evaluation
 * again. `incomplete` checkouts never took effect, so the evaluation still stands for them.
 */
export const subscriptionTookEffect = (status: SubscriptionStatus | undefined) =>
  status != null && status !== 'none' && status !== 'incomplete' && status !== 'incompleteExpired'

/** The evaluation's countdown, or null when this organization has none (self-host, subscribed). */
export function evaluationNotice(
  subscription: Pick<Subscription, 'evaluation' | 'status'> | null | undefined,
  now: Date = new Date(),
): EvaluationNotice | null {
  if (!subscription?.evaluation || subscriptionTookEffect(subscription.status)) return null
  const endsAt = new Date(subscription.evaluation.endsAt)
  return { expired: subscription.evaluation.expired, daysLeft: daysUntil(endsAt, now), endsAt }
}

/** How close the end has to be before the shell says anything. */
export const EVALUATION_WARNING_DAYS = 7

/**
 * A free organization whose Owner is over the people limit - counted across every unpaid
 * organization that Owner has - is read-only until people are removed or it pays. The
 * remedy is the Owner's either way, so everyone else is told whom to ask.
 */
export function freePeopleBanner(
  subscription:
    Pick<Subscription, 'enabled' | 'readOnly' | 'readOnlyReason' | 'freeTier'> | null | undefined,
  isOwner: boolean,
): PaymentBanner | null {
  if (!subscription?.enabled || !subscription.readOnly) return null
  if (subscription.readOnlyReason !== 'free_people') return null
  const max = subscription.freeTier?.maxPeople
  const allows =
    max != null ? `${max} ${max === 1 ? 'person' : 'people'}` : 'a limited number of people'
  return {
    tone: 'danger',
    action: isOwner ? 'plan' : 'ask-owner',
    message: isOwner
      ? `This organization is read-only: the free plan allows ${allows} across your free organizations, owner included. Remove someone or revoke an invitation, or upgrade to Hosted.`
      : `This organization is read-only: the free plan allows ${allows} across an owner's free organizations, owner included. Ask an owner to remove someone or upgrade to Hosted.`,
  }
}

/**
 * Where the free tier runs, an evaluation that ends moves the organization to the free
 * plan: nothing stops, the limits just tighten. Elsewhere an expired evaluation pauses
 * writes the way a failed payment does, but the cause and the remedy are different: nothing
 * is wrong in Stripe and no card will be charged on its own - the way forward is
 * subscribing, which only an owner can do. Either way the last week counts down, so the
 * day things change is not the day anyone hears about it.
 */
export function evaluationBanner(
  subscription:
    | Pick<Subscription, 'enabled' | 'readOnly' | 'evaluation' | 'status' | 'freeTier'>
    | null
    | undefined,
  isOwner: boolean,
  now: Date = new Date(),
): PaymentBanner | null {
  if (!subscription?.enabled) return null
  const notice = evaluationNotice(subscription, now)
  if (!notice) return null
  const action = isOwner ? 'plan' : 'ask-owner'
  const freeTier = subscription.freeTier
  if (notice.expired) {
    // On the free plan now, and writable: that is the plan working, not a problem to report.
    if (freeTier && !subscription.readOnly) return null
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
  if (freeTier) {
    return {
      tone: 'warning',
      action,
      message: `This organization's evaluation ends in ${plural(notice.daysLeft)} (${notice.endsAt.toLocaleDateString()}). Nothing is charged automatically - it then moves to the free plan: ${freeTier.maxPeople} people and ${sizeLabel(freeTier.storageBytes)} of attachments across ${isOwner ? 'your' : "an owner's"} free organizations. ${isOwner ? 'Subscribe' : 'Ask an owner to subscribe'} to keep Hosted.`,
    }
  }
  return {
    tone: 'warning',
    action,
    message: `This organization's evaluation ends in ${plural(notice.daysLeft)} (${notice.endsAt.toLocaleDateString()}). Nothing is charged automatically - ${isOwner ? 'subscribe' : 'ask an owner to subscribe'} to keep writing.`,
  }
}

/** Everything about a subscription the shell banner reads. */
export type BannerSubscription = Pick<
  Subscription,
  | 'enabled'
  | 'readOnly'
  | 'readOnlyReason'
  | 'graceEndsAt'
  | 'paymentFailedAt'
  | 'evaluation'
  | 'status'
  | 'freeTier'
>

/**
 * What the shell says across the top of the app. A failed payment is checked first: every
 * cause ends in the same `readOnly`, and an organization whose card was declined must not
 * be told its evaluation ran out or that it has too many people. The free plan's people
 * limit comes next, because an organization over it may also carry an expired evaluation.
 */
export function shellBanner(
  subscription: BannerSubscription | null | undefined,
  isOwner: boolean,
  now: Date = new Date(),
): PaymentBanner | null {
  return (
    paymentBanner(subscription, isOwner, now) ??
    freePeopleBanner(subscription, isOwner) ??
    evaluationBanner(subscription, isOwner, now)
  )
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
 * and when the standard price takes over. The server grants it - there is nothing here
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
