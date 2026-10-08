import { flushPromises, mount, RouterLinkStub } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { ref } from 'vue'
import { afterEach, describe, expect, it, vi } from 'vitest'

import {
  getSubscription,
  openBillingPortal,
  PLAN_LIMIT,
  startCheckout,
  type PlanOption,
  type Subscription,
} from '@/api/billing'
import type { Organization } from '@/api/organizations'
import InviteMembersDialog from '@/components/shell/InviteMembersDialog.vue'
import PaymentBanner from '@/components/shell/PaymentBanner.vue'
import { orgScopeKey, type OrgScope } from '@/composables/useSettingsScope'
import {
  describeExceeded,
  evaluationNotice,
  exceededFrom,
  foundingNotice,
  isFlatPlan,
  navigation,
  offeredPlans,
  paymentBanner,
  planChange,
  planLimitMessage,
  planLimitRefusal,
  planLimitUpgrade,
  planPriceLabel,
  shellBanner,
  sizeLabel,
} from '@/lib/billing'
import { useOrganizationsStore } from '@/stores/organizations'
import { toApiError } from '@/utils/api'
import FactoryRunnersView from '@/views/factory/FactoryRunnersView.vue'
import OrgBillingView from '@/views/settings/OrgBillingView.vue'

vi.mock('vue-router', async (importOriginal) => ({
  ...(await importOriginal<typeof import('vue-router')>()),
  useRoute: () => ({ params: { slug: 'acme' }, query: {} }),
}))

/**
 * Billing from the browser's side. The hosted offer is one flat
 * organization price with a 30-day evaluation, so this file holds the offer to its word:
 * the price is never quoted per seat, the evaluation counts down and says when it ends,
 * the founding discount shows what it becomes and when, an organization over its
 * attachment allowance is told to delete rather than to upgrade, a read-only organization
 * is told which cause stopped it, and a self-hosted instance is offered nothing at all.
 * Where the hosted free tier runs, its limits are shown per Owner, and only a refusal one
 * of them caused offers the way to Hosted.
 */

const GIB = 1_073_741_824

type Responder = (url: string, init?: RequestInit) => { status?: number; body: unknown }

function stubFetch(respond: Responder) {
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const { status = 200, body } = respond(String(input), init)
    return new Response(JSON.stringify(body), {
      status,
      headers: { 'content-type': status >= 400 ? 'application/problem+json' : 'application/json' },
    })
  })
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

afterEach(() => {
  vi.useRealTimers()
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

/**
 * A legacy per-seat plan: still carried by the organizations on it, sold to nobody. The API
 * marks `free` purchasable all the same, because checkout uses it as its downgrade target.
 */
const legacy = (code: string, humanSeatPrice: number, purchasable = false): PlanOption => ({
  code,
  humanSeatPrice,
  organizationPrice: null,
  includedAgentsPerHuman: code === 'starter' ? 3 : null,
  limits: {
    seatsHuman: code === 'free' ? 5 : 20,
    seatsAgent: 5,
    projects: code === 'free' ? 3 : 20,
    storageBytes: 5 * GIB,
  },
  purchasable,
})
const hostedPlan: PlanOption = {
  code: 'hosted',
  humanSeatPrice: 0,
  organizationPrice: 79,
  includedAgentsPerHuman: null,
  limits: {
    seatsHuman: null,
    seatsAgent: null,
    projects: null,
    storageBytes: 10 * GIB,
    runLogDays: 90,
    analyticsDays: 365,
    features: ['webhooks', 'page_permissions', 'audit_export'],
  },
  purchasable: true,
}
const freePlan = legacy('free', 0, true)
const starterPlan = legacy('starter', 9)
const plans = [freePlan, starterPlan, legacy('team', 15), legacy('enterprise', 19), hostedPlan]

const subscription = (overrides: Partial<Subscription> = {}): Subscription => ({
  mode: 'saas',
  enabled: true,
  plan: 'starter',
  subscribedPlan: 'starter',
  status: 'active',
  currentPeriodEnd: '2026-10-10T00:00:00Z',
  cancelAtPeriodEnd: false,
  seats: { human: 1, agent: 0 },
  paymentFailedAt: null,
  graceEndsAt: null,
  readOnly: false,
  hasBillingAccount: true,
  plans,
  evaluation: null,
  founding: null,
  readOnlyReason: null,
  freeTier: null,
  ...overrides,
})

/** An organization inside its 30-day evaluation: entitled to hosted, subscribed to nothing. */
const evaluating = (overrides: Partial<Subscription> = {}) =>
  subscription({
    plan: 'hosted',
    subscribedPlan: null,
    status: 'none',
    currentPeriodEnd: null,
    hasBillingAccount: false,
    evaluation: {
      startedAt: '2026-08-22T00:00:00Z',
      endsAt: '2026-09-30T00:00:00Z',
      expired: false,
    },
    ...overrides,
  })

const MIB = 1_048_576
const freeTier = (overrides: Partial<NonNullable<Subscription['freeTier']>> = {}) => ({
  maxPeople: 3,
  people: 2,
  storageBytes: 200 * MIB,
  storedBytes: 50 * MIB,
  maxRunners: 2,
  runLogDays: 30,
  ...overrides,
})
const freePlanOption: PlanOption = {
  code: 'hosted_free',
  humanSeatPrice: 0,
  organizationPrice: 0,
  includedAgentsPerHuman: null,
  limits: {
    seatsHuman: null,
    seatsAgent: null,
    projects: null,
    storageBytes: 200 * MIB,
    runLogDays: 30,
  },
  purchasable: true,
}
/** An organization whose evaluation ended where the free tier runs: on Free, writable. */
const onFree = (overrides: Partial<Subscription> = {}) =>
  subscription({
    plan: 'hosted_free',
    subscribedPlan: null,
    status: 'none',
    currentPeriodEnd: null,
    hasBillingAccount: false,
    plans: [freePlanOption, hostedPlan],
    evaluation: {
      startedAt: '2026-08-01T00:00:00Z',
      endsAt: '2026-09-01T00:00:00Z',
      expired: true,
    },
    freeTier: freeTier(),
    ...overrides,
  })

const localDate = (value: string) => new Date(value).toLocaleDateString()

describe('the billing endpoints', () => {
  it('reads the subscription and posts plan changes with the CSRF header', async () => {
    const fetchMock = stubFetch(() => ({
      body: { url: 'https://checkout.stripe.test/x', changed: false },
    }))

    await getSubscription('acme')
    await startCheckout('acme', 'hosted')
    await openBillingPortal('acme')

    expect(String(fetchMock.mock.calls[0]![0])).toBe('/api/v1/orgs/acme/billing/subscription')
    const [checkoutUrl, checkoutInit] = fetchMock.mock.calls[1]!
    expect(String(checkoutUrl)).toBe('/api/v1/orgs/acme/billing/checkout')
    expect(checkoutInit!.method).toBe('POST')
    expect(JSON.parse(String(checkoutInit!.body))).toEqual({ plan: 'hosted' })
    expect(new Headers(checkoutInit!.headers).get('X-Aictiq-Request')).toBe('1')
    expect(String(fetchMock.mock.calls[2]![0])).toBe('/api/v1/orgs/acme/billing/portal')
    expect(fetchMock.mock.calls[2]![1]!.method).toBe('POST')
  })
})

describe('plan prices', () => {
  it('quotes the hosted plan per organization and the legacy ones per seat', () => {
    expect(planPriceLabel(hostedPlan)).toBe('$79 per organization / month')
    expect(planPriceLabel(starterPlan)).toBe('$9 per human / month')
    expect(planPriceLabel(freePlan)).toBe('Free')
  })

  it('reads a zero organization price as no flat price at all', () => {
    // A legacy plan whose flat price is 0 rather than null is still a per-seat plan; the
    // other reading would print "Free" over a plan that charges $9 a head.
    expect(planPriceLabel({ ...starterPlan, organizationPrice: 0 })).toBe('$9 per human / month')
    expect(planChange('free', { ...starterPlan, organizationPrice: 0 }, plans)).toBe('upgrade')
  })

  it('offers the flat plan and whatever the organization is already on, and nothing else', () => {
    expect(offeredPlans(plans, 'starter').map((p) => p.code)).toEqual(['starter', 'hosted'])
    expect(offeredPlans(plans, 'hosted').map((p) => p.code)).toEqual(['hosted'])
    // `free` is purchasable - as checkout's downgrade target - and still not an offer.
    expect(offeredPlans(plans, 'free').map((p) => p.code)).toEqual(['free', 'hosted'])
    expect(isFlatPlan(hostedPlan)).toBe(true)
    expect(isFlatPlan(freePlan)).toBe(false)
  })

  it('ranks a change by what the plan actually costs', () => {
    expect(planChange('starter', hostedPlan, plans)).toBe('upgrade')
    expect(planChange('hosted', starterPlan, plans)).toBe('downgrade')
    expect(planChange('hosted', hostedPlan, plans)).toBe('current')
  })
})

describe('refused actions', () => {
  it('turns a refused plan change into things a person can do', () => {
    const error = toApiError({
      status: 409,
      data: {
        status: 409,
        title: 'Too much',
        type: 'https://aictiq.com/problems/plan-downgrade-blocked',
        exceeded: [
          { limit: 'seats_human', used: 7, allowed: 5 },
          { limit: 'projects', used: 4, allowed: 3 },
        ],
      },
    })

    const exceeded = exceededFrom(error)!

    expect(exceeded.map(describeExceeded)).toEqual([
      'Remove 2 human members (7 of 5 allowed)',
      'Archive 1 project (4 of 3 allowed)',
    ])
    // Any other conflict is not a downgrade list.
    expect(exceededFrom(toApiError({ status: 409, data: { status: 409, title: 'x' } }))).toBeNull()
  })

  it('tells a person how to free storage rather than naming a higher tier', () => {
    const message = describeExceeded({ limit: 'storage_bytes', used: 12 * GIB, allowed: 10 * GIB })

    expect(message).toBe(
      'Delete 2.00 GiB of attachments (12.00 of 10.00 GiB used) - existing files stay readable',
    )
    expect(message.toLowerCase()).not.toContain('upgrade')
  })

  it('states a paid 402 attachment refusal in the server’s words and offers no upgrade', () => {
    const error = toApiError({
      status: 402,
      data: {
        status: 402,
        title: 'Plan limit reached.',
        type: PLAN_LIMIT,
        detail:
          'This organization has reached its attachment allowance (10 GiB). Delete attachments to free space - existing files stay readable and downloadable.',
        limit: 'storage_bytes',
        // Paid Hosted has no higher tier, and the API says so by sending no URL.
        upgradeUrl: null,
      },
    })

    const message = planLimitMessage(error)!

    expect(message).toContain('Delete attachments to free space')
    expect(message.toLowerCase()).not.toContain('upgrade')
    expect(planLimitUpgrade(error)).toBe(false)
    expect(planLimitRefusal(error)).toEqual({ message, upgrade: false })
  })

  const refusal = (limit: string, upgradeUrl: string | null, detail = 'Refused.') =>
    toApiError({
      status: 402,
      data: {
        status: 402,
        title: 'Plan limit reached.',
        type: PLAN_LIMIT,
        detail,
        limit,
        upgradeUrl,
      },
    })

  it('offers Hosted only for a refusal a free-plan limit caused', () => {
    expect(planLimitUpgrade(refusal('free_people', '/settings/billing'))).toBe(true)
    expect(planLimitUpgrade(refusal('runners', '/settings/billing'))).toBe(true)
    // The free tier's pooled attachments carry the URL; paid Hosted's allowance does not.
    expect(planLimitUpgrade(refusal('storage_bytes', '/settings/billing'))).toBe(true)
    expect(planLimitUpgrade(refusal('storage_bytes', null))).toBe(false)
    // A legacy seat plan's refusal is not something Hosted is sold against here.
    expect(planLimitUpgrade(refusal('seats_human', '/settings/billing'))).toBe(false)
    expect(planLimitUpgrade(toApiError({ status: 409, data: { status: 409, title: 'x' } }))).toBe(
      false,
    )
    expect(planLimitUpgrade(new Error('offline'))).toBe(false)
  })

  it('keeps the server’s words for a free-plan refusal', () => {
    const detail =
      'The free plan allows 3 people, owner included, across all the free organizations an owner has; this would make 4. Remove someone or revoke an invitation, or upgrade this organization to Hosted.'
    expect(planLimitRefusal(refusal('free_people', '/settings/billing', detail))).toEqual({
      message: detail,
      upgrade: true,
    })
    expect(planLimitRefusal(new Error('offline'))).toBeNull()
  })

  it('falls back to allowance wording when the problem carries no detail, and ignores other errors', () => {
    expect(
      planLimitMessage(
        toApiError({
          status: 402,
          data: {
            status: 402,
            title: 'Plan limit reached.',
            type: PLAN_LIMIT,
            limit: 'storage_bytes',
          },
        }),
      ),
    ).toContain('Delete attachments to free space')
    expect(
      planLimitMessage(toApiError({ status: 409, data: { status: 409, title: 'x' } })),
    ).toBeNull()
    expect(planLimitMessage(new Error('offline'))).toBeNull()
  })
})

describe('the evaluation notice', () => {
  const now = new Date('2026-09-21T12:00:00Z')

  it('says nothing without an evaluation', () => {
    expect(evaluationNotice(null, now)).toBeNull()
    expect(evaluationNotice(subscription(), now)).toBeNull()
  })

  it('counts the days left, and reports expiry as the server said', () => {
    const active = evaluationNotice(evaluating(), now)!
    expect(active.daysLeft).toBe(9)
    expect(active.expired).toBe(false)
    expect(active.endsAt).toEqual(new Date('2026-09-30T00:00:00Z'))

    const expired = evaluationNotice(
      evaluating({
        evaluation: {
          startedAt: '2026-08-01T00:00:00Z',
          endsAt: '2026-09-01T00:00:00Z',
          expired: true,
        },
      }),
      now,
    )!
    expect(expired.expired).toBe(true)
    expect(expired.daysLeft).toBe(0)
  })

  it('is history once the organization has subscribed, and stays history after it cancels', () => {
    const lastWeek = {
      startedAt: '2026-08-22T00:00:00Z',
      endsAt: '2026-09-26T00:00:00Z',
      expired: false,
    }
    // Still evaluating, nothing bought: the notice and the shell countdown both show.
    expect(evaluationNotice(evaluating({ evaluation: lastWeek }), now)).not.toBeNull()
    expect(shellBanner(evaluating({ evaluation: lastWeek }), true, now)).not.toBeNull()
    // A checkout that never completed bought nothing either.
    expect(evaluationNotice(evaluating({ status: 'incomplete' }), now)).not.toBeNull()

    // Subscribed: the evaluation row is still there, but nothing is counting down.
    for (const status of ['active', 'trialing'] as const) {
      const paid = evaluating({
        plan: 'hosted',
        subscribedPlan: 'hosted',
        status,
        evaluation: lastWeek,
      })
      expect(evaluationNotice(paid, now)).toBeNull()
      expect(shellBanner(paid, true, now)).toBeNull()
    }

    // Cancelled back to Free, before or after the evaluation's end: no evaluation again.
    for (const expired of [false, true]) {
      const cancelled = evaluating({
        plan: 'hosted_free',
        subscribedPlan: null,
        status: 'canceled',
        evaluation: { ...lastWeek, expired },
        freeTier: freeTier(),
      })
      expect(evaluationNotice(cancelled, now)).toBeNull()
      expect(shellBanner(cancelled, true, now)).toBeNull()
    }
  })
})

describe('the founding offer', () => {
  it('counts the periods left and takes the end date from the server', () => {
    const notice = foundingNotice(
      subscription({
        currentPeriodEnd: '2026-10-10T00:00:00Z',
        founding: {
          price: 29,
          periods: 12,
          periodsBilled: 4,
          renewalPrice: 79,
          convertedAt: null,
          endsAt: '2027-06-10T00:00:00Z',
        },
      }),
    )!

    expect(notice.price).toBe(29)
    expect(notice.renewalPrice).toBe(79)
    expect(notice.periodsLeft).toBe(8)
    expect(notice.converted).toBe(false)
    expect(notice.standardFrom).toEqual(new Date('2027-06-10T00:00:00Z'))
  })

  it('never derives the end date itself, even with a period end to count from', () => {
    // The server does this arithmetic against the periods it has actually collected. A
    // date the browser worked out separately would eventually disagree with the invoice.
    const notice = foundingNotice(
      subscription({
        currentPeriodEnd: '2026-12-31T00:00:00Z',
        founding: {
          price: 29,
          periods: 12,
          periodsBilled: 10,
          renewalPrice: 79,
          convertedAt: null,
          endsAt: null,
        },
      }),
    )!

    expect(notice.periodsLeft).toBe(2)
    expect(notice.standardFrom).toBeNull()
  })

  it('reports a completed offer by the date the server recorded, and nothing without one', () => {
    const notice = foundingNotice(
      subscription({
        founding: {
          price: 29,
          periods: 12,
          periodsBilled: 12,
          renewalPrice: 79,
          convertedAt: '2027-10-01T00:00:00Z',
          endsAt: null,
        },
      }),
    )!

    expect(notice.converted).toBe(true)
    expect(notice.periodsLeft).toBe(0)
    expect(notice.standardFrom).toEqual(new Date('2027-10-01T00:00:00Z'))
    expect(foundingNotice(subscription())).toBeNull()
  })
})

describe('the payment-failed banner', () => {
  const now = new Date('2026-09-10T12:00:00Z')

  it('says nothing while the account is healthy, or where nothing is billed', () => {
    expect(paymentBanner(subscription(), true, now)).toBeNull()
    expect(
      paymentBanner(
        subscription({ enabled: false, graceEndsAt: '2026-09-20T00:00:00Z' }),
        true,
        now,
      ),
    ).toBeNull()
    expect(paymentBanner(undefined, true, now)).toBeNull()
  })

  it('counts down the grace period, and tells a member whom to ask', () => {
    const inGrace = subscription({
      graceEndsAt: '2026-09-20T12:00:00Z',
      paymentFailedAt: '2026-09-06T12:00:00Z',
    })

    const owner = paymentBanner(inGrace, true, now)!
    const member = paymentBanner(inGrace, false, now)!

    expect(owner.tone).toBe('warning')
    expect(owner.message).toContain('read-only in 10 days')
    expect(owner.action).toBe('manage')
    expect(member.action).toBe('ask-owner')
    expect(member.message).toContain('an owner updates the payment method')
  })

  it('warns on a failed payment even before a grace window is known', () => {
    const banner = paymentBanner(
      subscription({ paymentFailedAt: '2026-09-09T12:00:00Z' }),
      true,
      now,
    )!
    expect(banner.tone).toBe('warning')
    expect(banner.message).toContain('A payment failed')
  })

  it('turns red once the organization is read-only', () => {
    const banner = paymentBanner(
      subscription({ graceEndsAt: '2026-09-01T00:00:00Z', readOnly: true }),
      false,
      now,
    )!
    expect(banner.tone).toBe('danger')
    expect(banner.message).toContain('read-only until an owner')
  })

  it('offers owners the way to fix it and members only the warning', () => {
    const lapsed = subscription({ graceEndsAt: '2026-09-01T00:00:00Z', readOnly: true })
    const render = (isOwner: boolean) =>
      mount(PaymentBanner, {
        props: { slug: 'acme', subscription: lapsed, isOwner },
        global: { stubs: { RouterLink: RouterLinkStub } },
      })

    const owner = render(true)
    expect(owner.find('[role="alert"]').exists()).toBe(true)
    expect(owner.findComponent(RouterLinkStub).props('to')).toBe('/o/acme/settings/billing')
    expect(render(false).findComponent(RouterLinkStub).exists()).toBe(false)
    expect(
      mount(PaymentBanner, { props: { slug: 'acme', subscription: subscription(), isOwner: true } })
        .find('[role="alert"]')
        .exists(),
    ).toBe(false)
  })
})

describe('the evaluation banner', () => {
  const now = new Date('2026-09-21T12:00:00Z')
  const expired = {
    startedAt: '2026-08-01T00:00:00Z',
    endsAt: '2026-09-01T00:00:00Z',
    expired: true,
  }

  it('stays quiet until the last week, then counts down', () => {
    // Nine days left: still nothing across the top of every page.
    expect(shellBanner(evaluating(), true, now)).toBeNull()

    const banner = shellBanner(
      evaluating({
        evaluation: {
          startedAt: '2026-08-22T00:00:00Z',
          endsAt: '2026-09-26T00:00:00Z',
          expired: false,
        },
      }),
      true,
      now,
    )!
    expect(banner.tone).toBe('warning')
    expect(banner.message).toContain('ends in 5 days')
    expect(banner.message).toContain('Nothing is charged automatically')
    expect(banner.action).toBe('plan')
  })

  it('says writes have stopped once it expires, and whom a member should ask', () => {
    const lapsed = evaluating({ evaluation: expired, readOnly: true })

    const owner = shellBanner(lapsed, true, now)!
    expect(owner.tone).toBe('danger')
    expect(owner.message).toContain('evaluation has ended')
    expect(owner.message).toContain('new changes and agent runs are paused')
    expect(owner.message).toContain('Subscribe to continue.')
    expect(owner.action).toBe('plan')
    expect(shellBanner(lapsed, false, now)!.action).toBe('ask-owner')
    expect(shellBanner(lapsed, false, now)!.message).toContain('Ask an owner to subscribe')
  })

  it('names the cause that actually stopped the organization', () => {
    // Both causes end in the same `readOnly`; a declined card must not be reported as an
    // expired evaluation, since updating the card is what fixes one and not the other.
    const declined = subscription({
      evaluation: expired,
      paymentFailedAt: '2026-08-20T00:00:00Z',
      graceEndsAt: '2026-09-01T00:00:00Z',
      readOnly: true,
    })
    const evaluationOnly = evaluating({ evaluation: expired, readOnly: true })

    expect(shellBanner(declined, true, now)!.message).toContain('A payment failed')
    expect(shellBanner(declined, true, now)!.action).toBe('manage')
    expect(shellBanner(evaluationOnly, true, now)!.message).not.toContain('payment')
    expect(shellBanner(evaluationOnly, true, now)!.action).toBe('plan')
  })

  it('offers owners the plans and members only the warning', () => {
    const render = (isOwner: boolean) =>
      mount(PaymentBanner, {
        props: {
          slug: 'acme',
          subscription: evaluating({ evaluation: expired, readOnly: true }),
          isOwner,
        },
        global: { stubs: { RouterLink: RouterLinkStub } },
      })

    const owner = render(true)
    expect(owner.find('[role="alert"]').exists()).toBe(true)
    expect(owner.findComponent(RouterLinkStub).text()).toBe('View plans')
    expect(owner.findComponent(RouterLinkStub).props('to')).toBe('/o/acme/settings/billing')
    expect(render(false).findComponent(RouterLinkStub).exists()).toBe(false)
  })
})

describe('the free plan banners', () => {
  const now = new Date('2026-09-21T12:00:00Z')

  it('says why a free organization over the people limit is read-only, and who can fix it', () => {
    const over = onFree({ readOnly: true, readOnlyReason: 'free_people' })

    const owner = shellBanner(over, true, now)!
    expect(owner.tone).toBe('danger')
    expect(owner.action).toBe('plan')
    expect(owner.message).toBe(
      'This organization is read-only: the free plan allows 3 people across your free organizations, owner included. Remove someone or revoke an invitation, or upgrade to Hosted.',
    )
    // Not the expired evaluation it also carries: that is not what stopped it.
    expect(owner.message).not.toContain('evaluation')

    const member = shellBanner(over, false, now)!
    expect(member.action).toBe('ask-owner')
    expect(member.message).toContain('Ask an owner')
  })

  it('still names a failed payment first', () => {
    const declined = onFree({
      readOnly: true,
      readOnlyReason: 'free_people',
      paymentFailedAt: '2026-08-20T00:00:00Z',
      graceEndsAt: '2026-09-01T00:00:00Z',
    })
    expect(shellBanner(declined, true, now)!.message).toContain('A payment failed')
  })

  it('says nothing once an evaluation has simply moved the organization to Free', () => {
    expect(shellBanner(onFree(), true, now)).toBeNull()
  })

  it('counts down to the free plan rather than to read-only', () => {
    const banner = shellBanner(
      evaluating({
        evaluation: {
          startedAt: '2026-08-22T00:00:00Z',
          endsAt: '2026-09-26T00:00:00Z',
          expired: false,
        },
        freeTier: freeTier(),
      }),
      true,
      now,
    )!
    expect(banner.tone).toBe('warning')
    expect(banner.message).toContain('ends in 5 days')
    expect(banner.message).toContain('moves to the free plan: 3 people and 200 MiB of attachments')
    expect(banner.message).toContain('Subscribe to keep Hosted.')
    expect(banner.message).not.toContain('read-only')
  })

  it('offers owners the plans from the banner', () => {
    const wrapper = mount(PaymentBanner, {
      props: {
        slug: 'acme',
        subscription: onFree({ readOnly: true, readOnlyReason: 'free_people' }),
        isOwner: true,
      },
      global: { stubs: { RouterLink: RouterLinkStub } },
    })
    expect(wrapper.get('[role="alert"]').text()).toContain('the free plan allows 3 people')
    expect(wrapper.findComponent(RouterLinkStub).props('to')).toBe('/o/acme/settings/billing')
  })

  it('states sizes in the unit that reads naturally', () => {
    expect(sizeLabel(200 * MIB)).toBe('200 MiB')
    expect(sizeLabel(10 * GIB)).toBe('10 GiB')
    expect(sizeLabel(6_000)).toBe('5.9 KiB')
    expect(sizeLabel(0)).toBe('0 MiB')
    expect(sizeLabel(1.5 * GIB)).toBe('1.5 GiB')
  })
})

describe('free-plan refusals', () => {
  const peopleRefusal = {
    status: 402,
    body: {
      status: 402,
      title: 'Plan limit reached.',
      type: PLAN_LIMIT,
      detail:
        'The free plan allows 3 people, owner included, across all the free organizations an owner has; this would make 4. Remove someone or revoke an invitation, or upgrade this organization to Hosted.',
      limit: 'free_people',
      upgradeUrl: '/settings/billing',
    },
  }
  const passthrough = { template: '<div><slot /></div>' }
  const dialogStubs = {
    RouterLink: RouterLinkStub,
    Dialog: passthrough,
    DialogContent: passthrough,
    DialogDescription: passthrough,
    DialogFooter: passthrough,
    DialogHeader: passthrough,
    DialogTitle: passthrough,
  }

  it('shows a refused invitation in the server’s words, with the way to Hosted', async () => {
    stubFetch(() => peopleRefusal)
    const wrapper = mount(InviteMembersDialog, {
      props: { slug: 'acme', open: true },
      global: { stubs: dialogStubs },
    })

    await wrapper.get('#invite-emails').setValue('dora@example.com')
    await wrapper.get('form#invite-people').trigger('submit')
    await flushPromises()

    expect(wrapper.text()).toContain('dora@example.com')
    expect(wrapper.text()).toContain('The free plan allows 3 people')
    const link = wrapper.getComponent(RouterLinkStub)
    expect(link.props('to')).toBe('/o/acme/settings/billing')
    expect(link.text()).toBe('Upgrade to Hosted')
  })

  it('offers no upgrade for a refusal Hosted would not lift', async () => {
    stubFetch(() => ({
      status: 422,
      body: { status: 422, title: 'That address is already a member.' },
    }))
    const wrapper = mount(InviteMembersDialog, {
      props: { slug: 'acme', open: true },
      global: { stubs: dialogStubs },
    })

    await wrapper.get('#invite-emails').setValue('ana@example.com')
    await wrapper.get('form#invite-people').trigger('submit')
    await flushPromises()

    expect(wrapper.text()).toContain('That address is already a member.')
    expect(wrapper.findComponent(RouterLinkStub).exists()).toBe(false)
  })

  it('keeps a refused runner registration in the dialog, with the way to Hosted', async () => {
    const organization: Organization = {
      id: 'org-1',
      slug: 'acme',
      name: 'Acme',
      role: 'owner',
      canOperateFactory: true,
      plan: 'hosted_free',
      timeZone: 'UTC',
      weekStart: 'monday',
      membersCanCreateProjects: true,
      createdAt: '2026-01-01T00:00:00Z',
      version: 1,
    }
    const scope: OrgScope = {
      slug: ref('acme'),
      record: ref(organization),
      loading: ref(false),
      notFound: ref(false),
      reload: async () => {},
      set: () => {},
    }
    stubFetch((_url, init) =>
      init?.method === 'POST'
        ? {
            status: 402,
            body: {
              status: 402,
              title: 'Plan limit reached.',
              type: PLAN_LIMIT,
              detail:
                'The free plan allows 2 registered runners per organization. Delete a runner, or upgrade to Hosted for unlimited runners.',
              limit: 'runners',
              upgradeUrl: '/settings/billing',
            },
          }
        : { body: [] },
    )
    const wrapper = mount(FactoryRunnersView, {
      global: { plugins: [createPinia()], provide: { [orgScopeKey]: scope }, stubs: dialogStubs },
    })
    await flushPromises()

    await wrapper.get('#runner-name').setValue('vps-3')
    await wrapper.get('form#register-runner').trigger('submit')
    await flushPromises()

    const notice = wrapper.get('[data-testid="plan-limit"]')
    expect(notice.text()).toContain('The free plan allows 2 registered runners per organization.')
    expect(notice.getComponent(RouterLinkStub).props('to')).toBe('/o/acme/settings/billing')
  })
})

describe('the Plan page', () => {
  const summary = {
    mode: 'saas',
    plan: 'starter',
    evaluation: null,
    limits: { seatsHuman: 20, seatsAgent: 5, projects: 20, storageBytes: 5 * GIB },
    usage: { humans: 1, agents: 0, projects: 4, storageBytes: 0 },
  }
  const hostedSummary = {
    mode: 'saas',
    plan: 'hosted',
    evaluation: null,
    limits: {
      seatsHuman: null,
      seatsAgent: null,
      projects: null,
      storageBytes: 10 * GIB,
      runLogDays: 90,
      analyticsDays: 365,
    },
    usage: { humans: 3, agents: 5, projects: 4, storageBytes: 2 * GIB },
  }
  const hosted = subscription({ plan: 'hosted', subscribedPlan: 'hosted' })

  function respond(options: {
    summary?: Record<string, unknown>
    subscription?: Subscription
    checkout?: { status?: number; body: unknown }
  }): Responder {
    return (url) => {
      if (url.endsWith('/billing')) return { body: options.summary ?? summary }
      if (url.endsWith('/billing/subscription'))
        return { body: options.subscription ?? subscription() }
      if (url.endsWith('/billing/checkout')) return options.checkout ?? { body: {} }
      if (url.endsWith('/billing/portal'))
        return { body: { url: 'https://billing.stripe.test/p', changed: false } }
      return { status: 404, body: {} }
    }
  }

  async function renderPage(role: 'owner' | 'member' = 'owner') {
    const pinia = createPinia()
    setActivePinia(pinia)
    const organizations = useOrganizationsStore()
    organizations.organizations = [
      { id: 'org-1', slug: 'acme', name: 'Acme', role, canOperateFactory: true },
    ]
    organizations.select('acme')
    const wrapper = mount(OrgBillingView, { global: { plugins: [pinia] } })
    await flushPromises()
    return wrapper
  }

  const button = (wrapper: Awaited<ReturnType<typeof renderPage>>, testId: string) =>
    wrapper.get(`[data-testid="${testId}"] button`)

  it('offers the hosted plan and never a retired one the organization is not on', async () => {
    stubFetch(respond({}))
    const wrapper = await renderPage()

    // Starter is this organization's own plan, so it stays on the page…
    expect(wrapper.get('[data-testid="plan-starter"]').text()).toContain('$9 per human / month')
    expect(wrapper.get('[data-testid="plan-starter"]').text()).toContain('No longer available')
    expect(button(wrapper, 'plan-starter').attributes('disabled')).toBeDefined()
    // …and the other retired plans are not offered to it at all, `free` included: the API
    // calls it purchasable because checkout downgrades to it, which is not a reason to put a
    // three-project per-seat plan in front of anyone.
    expect(wrapper.find('[data-testid="plan-team"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="plan-free"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="plan-enterprise"]').exists()).toBe(false)
    expect(button(wrapper, 'plan-hosted').text()).toBe('Upgrade to Hosted')
  })

  it('sends the hosted checkout to the URL the API returns', async () => {
    stubFetch(
      respond({
        checkout: { body: { url: 'https://checkout.stripe.test/c/pay/1', changed: false } },
      }),
    )
    const assign = vi.spyOn(navigation, 'assign').mockImplementation(() => {})
    const wrapper = await renderPage()

    await button(wrapper, 'plan-hosted').trigger('click')
    await flushPromises()

    expect(assign).toHaveBeenCalledWith('https://checkout.stripe.test/c/pay/1')
  })

  it('explains a refused change instead of navigating anywhere', async () => {
    stubFetch(
      respond({
        checkout: {
          status: 409,
          body: {
            status: 409,
            title: 'The organization uses more than that plan allows.',
            type: 'https://aictiq.com/problems/plan-downgrade-blocked',
            exceeded: [{ limit: 'storage_bytes', used: 30 * GIB, allowed: 10 * GIB }],
          },
        },
      }),
    )
    const assign = vi.spyOn(navigation, 'assign').mockImplementation(() => {})
    const wrapper = await renderPage()

    await button(wrapper, 'plan-hosted').trigger('click')
    await flushPromises()

    const blocked = wrapper.get('[data-testid="downgrade-blocked"]')
    expect(blocked.text()).toContain('Delete 20.00 GiB of attachments (30.00 of 10.00 GiB used)')
    expect(blocked.text().toLowerCase()).not.toContain('upgrade to')
    expect(assign).not.toHaveBeenCalled()
  })

  it('opens the Customer Portal for invoices', async () => {
    stubFetch(respond({}))
    const assign = vi.spyOn(navigation, 'assign').mockImplementation(() => {})
    const wrapper = await renderPage()

    await wrapper.get('[data-testid="manage-billing"]').trigger('click')
    await flushPromises()

    expect(assign).toHaveBeenCalledWith('https://billing.stripe.test/p')
  })

  it('offers nothing to buy, and shows no quotas, on a self-hosted instance', async () => {
    stubFetch((url) =>
      url.endsWith('/billing')
        ? {
            body: {
              ...hostedSummary,
              mode: 'self_hosted',
              plan: 'self_hosted',
              limits: { seatsHuman: null, seatsAgent: null, projects: null, storageBytes: null },
            },
          }
        : {
            body: subscription({
              mode: 'self_hosted',
              enabled: false,
              plans: [],
              hasBillingAccount: false,
            }),
          },
    )
    const wrapper = await renderPage()

    expect(wrapper.text()).toContain('self-hosted instance')
    expect(wrapper.find('[data-testid="plan-hosted"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="manage-billing"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="usage-storage"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="service-allowances"]').exists()).toBe(false)
    expect(wrapper.findAll('button')).toHaveLength(0)
  })

  it('prices the hosted plan per organization, never per seat', async () => {
    stubFetch(respond({ summary: hostedSummary, subscription: hosted }))
    const wrapper = await renderPage()

    const card = wrapper.get('[data-testid="plan-hosted"]')
    expect(card.text()).toContain('$79 per organization / month')
    expect(card.text()).not.toContain('per human')
    expect(wrapper.get('[data-testid="current-price"]').text()).toBe('$79 per organization / month')
  })

  it('states what the hosted plan includes', async () => {
    stubFetch(respond({ summary: hostedSummary, subscription: hosted }))
    const wrapper = await renderPage()

    const card = wrapper.get('[data-testid="plan-hosted"]')
    expect(card.text()).toContain('Unlimited people, agents and projects')
    expect(card.text()).toContain('10 GiB attachments')
    expect(card.text()).toContain('90 days of run logs')
    expect(card.text()).toContain('365 days of analytics')
  })

  it('shows the retention this organization actually has', async () => {
    stubFetch(respond({ summary: hostedSummary, subscription: hosted }))
    const wrapper = await renderPage()

    expect(wrapper.get('[data-testid="allowance-logs"]').text()).toContain('Finished-run raw logs')
    expect(wrapper.get('[data-testid="allowance-logs"]').text()).toContain('90 days')
    expect(wrapper.get('[data-testid="allowance-analytics"]').text()).toContain('365 days')
    expect(wrapper.get('[data-testid="service-allowances"]').text()).toContain(
      'cannot be brought back',
    )
  })

  it('shows attachment usage against the allowance, and Unlimited where nothing is metered', async () => {
    stubFetch(respond({ summary: hostedSummary, subscription: hosted }))
    const wrapper = await renderPage()

    const humans = wrapper.get('[data-testid="usage-humans"]')
    expect(humans.text()).toContain('Unlimited')
    expect(humans.find('.bg-muted').exists()).toBe(false)

    const storage = wrapper.get('[data-testid="usage-storage"]')
    expect(storage.text().replace(/\s+/g, ' ')).toContain('2.00 GiB / 10.00 GiB')
    expect(storage.find('.bg-muted').exists()).toBe(true)
  })

  it('says how to regain space when over the allowance, and offers no upgrade', async () => {
    stubFetch(
      respond({
        summary: { ...hostedSummary, usage: { ...hostedSummary.usage, storageBytes: 12 * GIB } },
        subscription: hosted,
      }),
    )
    const wrapper = await renderPage()

    const storage = wrapper.get('[data-testid="usage-storage"]')
    expect(storage.text()).toContain(
      'Delete attachments to free space - existing files stay readable',
    )
    expect(storage.text()).toContain('There is no larger allowance to buy.')
    expect(storage.findAll('a')).toHaveLength(0)
  })

  it('counts down the evaluation, says when it ends, and still sells the plan', async () => {
    vi.useFakeTimers({ toFake: ['Date'], now: new Date('2026-09-21T12:00:00Z') })
    stubFetch(respond({ summary: hostedSummary, subscription: evaluating() }))
    const wrapper = await renderPage()

    const panel = wrapper.get('[data-testid="evaluation-panel"]')
    expect(panel.text()).toContain('Evaluation')
    expect(panel.text()).toContain('9 days left')
    expect(panel.text()).toContain(`ends ${localDate('2026-09-30T00:00:00Z')}`)
    expect(panel.text()).toContain('Nothing is charged when it ends')
    expect(wrapper.find('[data-testid="evaluation-cta"]').exists()).toBe(false)

    // The entitlement is hosted, but nothing has been bought yet: the plan is still on sale.
    const cta = button(wrapper, 'plan-hosted')
    expect(cta.text()).toBe('Subscribe')
    expect(cta.attributes('disabled')).toBeUndefined()
  })

  it('drops the evaluation once the organization has subscribed', async () => {
    vi.useFakeTimers({ toFake: ['Date'], now: new Date('2026-09-21T12:00:00Z') })
    stubFetch(
      respond({
        summary: hostedSummary,
        subscription: evaluating({
          subscribedPlan: 'hosted',
          status: 'active',
          currentPeriodEnd: '2026-10-21T00:00:00Z',
          hasBillingAccount: true,
        }),
      }),
    )
    const wrapper = await renderPage()

    expect(wrapper.text()).toContain('Current plan')
    expect(wrapper.find('[data-testid="evaluation-panel"]').exists()).toBe(false)
  })

  it('warns when the evaluation has ended, with its date and the way out for owners', async () => {
    vi.useFakeTimers({ toFake: ['Date'], now: new Date('2026-09-21T12:00:00Z') })
    stubFetch(
      respond({
        summary: hostedSummary,
        subscription: evaluating({
          evaluation: {
            startedAt: '2026-08-01T00:00:00Z',
            endsAt: '2026-09-01T00:00:00Z',
            expired: true,
          },
          readOnly: true,
        }),
      }),
    )
    const wrapper = await renderPage()

    const panel = wrapper.get('[data-testid="evaluation-panel"]')
    expect(panel.text()).toContain(`Your evaluation ended on ${localDate('2026-09-01T00:00:00Z')}`)
    expect(panel.text()).toContain('reading, downloading and exporting still work')
    expect(wrapper.get('[data-testid="evaluation-cta"]').text()).toBe('Choose a plan')

    const member = await renderPage('member')
    expect(member.get('[data-testid="evaluation-panel"]').text()).toContain('Ask an owner')
    expect(member.find('[data-testid="evaluation-cta"]').exists()).toBe(false)
  })

  it('shows the founding price, what is left of it, and what it becomes', async () => {
    stubFetch(
      respond({
        summary: hostedSummary,
        subscription: subscription({
          plan: 'hosted',
          subscribedPlan: 'hosted',
          currentPeriodEnd: '2026-10-10T00:00:00Z',
          founding: {
            price: 29,
            periods: 12,
            periodsBilled: 4,
            renewalPrice: 79,
            convertedAt: null,
            endsAt: '2027-06-10T00:00:00Z',
          },
        }),
      }),
    )
    const wrapper = await renderPage()

    const offer = wrapper.get('[data-testid="founding-offer"]')
    expect(offer.text()).toContain('$29 per organization / month for 12 monthly billing periods')
    expect(offer.text()).toContain('4 of 12 billed')
    expect(offer.text()).toContain(
      `$79 per organization / month from ${localDate('2027-06-10T00:00:00Z')}`,
    )
    expect(offer.text()).toContain('cannot be requested here')
    // While it runs, the header quotes what this organization is actually charged.
    expect(wrapper.get('[data-testid="current-price"]').text()).toBe('$29 per organization / month')
    // The card keeps the standard price: the discount is this subscription's, not the plan's.
    expect(wrapper.get('[data-testid="plan-hosted"]').text()).toContain(
      '$79 per organization / month',
    )
  })

  it('says when the founding offer has completed and what the organization pays now', async () => {
    stubFetch(
      respond({
        summary: hostedSummary,
        subscription: subscription({
          plan: 'hosted',
          subscribedPlan: 'hosted',
          founding: {
            price: 29,
            periods: 12,
            periodsBilled: 12,
            renewalPrice: 79,
            convertedAt: '2027-10-01T00:00:00Z',
            endsAt: null,
          },
        }),
      }),
    )
    const wrapper = await renderPage()

    const offer = wrapper.get('[data-testid="founding-offer"]')
    expect(offer.text()).toContain(`Completed on ${localDate('2027-10-01T00:00:00Z')}`)
    expect(offer.text()).toContain('now pays $79 per organization / month')
    expect(wrapper.get('[data-testid="current-price"]').text()).toBe('$79 per organization / month')
  })

  it('shows nothing about an evaluation or a founding offer where there is none', async () => {
    stubFetch(respond({ summary: hostedSummary, subscription: hosted }))
    const wrapper = await renderPage()

    expect(wrapper.find('[data-testid="evaluation-panel"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="founding-offer"]').exists()).toBe(false)
  })

  const freeSummary = {
    ...hostedSummary,
    plan: 'hosted_free',
    limits: {
      seatsHuman: null,
      seatsAgent: null,
      projects: null,
      storageBytes: 200 * MIB,
      runLogDays: 30,
      analyticsDays: 365,
    },
    usage: { humans: 2, agents: 1, projects: 1, storageBytes: 10 * MIB },
  }

  it('shows the free plan with its per-owner usage and offers Hosted', async () => {
    stubFetch(respond({ summary: freeSummary, subscription: onFree() }))
    const wrapper = await renderPage()

    expect(wrapper.get('[data-testid="current-plan"]').text()).toBe('Free')
    const panel = wrapper.get('[data-testid="free-tier"]')
    expect(panel.get('[data-testid="free-people"]').text()).toContain(
      '2 of 3 across your free organizations (owner included)',
    )
    expect(panel.get('[data-testid="free-storage"]').text().replace(/\s+/g, ' ')).toContain(
      '50 MiB of 200 MiB pooled across your free organizations',
    )
    expect(panel.get('[data-testid="free-runners"]').text()).toContain('Up to 2 registered')
    expect(wrapper.get('[data-testid="allowance-logs"]').text()).toContain('30 days')
    // Counted per owner in the panel, so not a second time against the organization alone.
    expect(wrapper.find('[data-testid="usage-humans"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="usage-storage"]').exists()).toBe(false)
    // An evaluation that moved to Free is not a problem to report.
    expect(wrapper.get('[data-testid="evaluation-panel"]').text()).toContain('on the free plan now')
    expect(wrapper.find('[role="alert"]').exists()).toBe(false)

    expect(wrapper.get('[data-testid="plan-hosted_free"]').text()).toContain(
      '3 people across your free organizations',
    )
    expect(button(wrapper, 'plan-hosted_free').text()).toBe('Current plan')
    expect(button(wrapper, 'plan-hosted_free').attributes('disabled')).toBeDefined()
    expect(button(wrapper, 'plan-hosted').text()).toBe('Upgrade to Hosted')
    expect(wrapper.get('[data-testid="plan-hosted"]').text()).toContain(
      '$79 per organization / month',
    )
    expect(wrapper.get('[data-testid="free-upgrade"]').text()).toContain('Upgrade to Hosted')
  })

  it('upgrades a free organization through checkout', async () => {
    const fetchMock = stubFetch(
      respond({
        summary: freeSummary,
        subscription: onFree(),
        checkout: { body: { url: 'https://checkout.stripe.test/c/pay/2', changed: false } },
      }),
    )
    const assign = vi.spyOn(navigation, 'assign').mockImplementation(() => {})
    const wrapper = await renderPage()

    await wrapper.get('[data-testid="free-upgrade"]').trigger('click')
    await flushPromises()

    const checkout = fetchMock.mock.calls.find(([url]) =>
      String(url).endsWith('/billing/checkout'),
    )!
    expect(JSON.parse(String(checkout[1]!.body))).toEqual({ plan: 'hosted' })
    expect(assign).toHaveBeenCalledWith('https://checkout.stripe.test/c/pay/2')
  })

  it('says why a free organization over the people limit is read-only', async () => {
    stubFetch(
      respond({
        summary: freeSummary,
        subscription: onFree({
          readOnly: true,
          readOnlyReason: 'free_people',
          freeTier: freeTier({ people: 4 }),
        }),
      }),
    )
    const wrapper = await renderPage()

    expect(wrapper.get('[data-testid="free-people-alert"]').text()).toContain(
      'the free plan allows 3 people across your free organizations',
    )
    expect(wrapper.get('[data-testid="free-people"]').text()).toContain('4 of 3')

    const member = await renderPage('member')
    expect(member.get('[data-testid="free-people-alert"]').text()).toContain('Ask an owner')
    expect(member.find('[data-testid="free-upgrade"]').exists()).toBe(false)
  })

  it('tells an evaluating organization it moves to Free when the evaluation ends', async () => {
    vi.useFakeTimers({ toFake: ['Date'], now: new Date('2026-09-21T12:00:00Z') })
    stubFetch(
      respond({
        summary: hostedSummary,
        subscription: evaluating({ freeTier: freeTier({ people: 0, storedBytes: 0 }) }),
      }),
    )
    const wrapper = await renderPage()

    const panel = wrapper.get('[data-testid="evaluation-panel"]')
    expect(panel.text()).toContain('moves to the free plan')
    expect(panel.text()).toContain('200 MiB')
    expect(wrapper.find('[data-testid="free-tier"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="plan-hosted_free"]').exists()).toBe(false)
  })
})
