<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRoute } from 'vue-router'

import {
  getSubscription,
  openBillingPortal,
  startCheckout,
  type ExceededLimit,
  type PlanOption,
  type Subscription,
} from '@/api/billing'
import { getBillingSummary, type BillingSummary } from '@/api/organizations'
import UiPageState from '@/components/UiPageState.vue'
import { Button } from '@/components/ui/button'
import { useToast } from '@/composables/useToast'
import {
  describeExceeded,
  evaluationNotice,
  exceededFrom,
  foundingNotice,
  navigation,
  offeredPlans,
  planChange,
  planPriceLabel,
} from '@/lib/billing'
import { useOrganizationsStore } from '@/stores/organizations'

/**
 * The organization's plan: usage against its allowances and, on a SaaS instance
 * with Stripe configured, the way to change it. Choosing a plan with no
 * subscription goes to Stripe Checkout; with one, the API changes it in place and Stripe's
 * webhook confirms it.
 *
 * The hosted offer is one flat organization price with a 30-day evaluation,
 * and the page states all six of the things the offer is made of: attachment usage against
 * the allowance, how long raw run logs and analytics history are kept, when the evaluation
 * ends, what the organization pays, and when a founding offer returns to the standard
 * price. The legacy per-seat plans appear only for an organization already on one - they
 * are not for sale, so offering them to anyone else would be advertising a price we will
 * not honour. A self-hosted instance shows no quotas and nothing to buy.
 */
const route = useRoute()
const toast = useToast()
const organizations = useOrganizationsStore()
const slug = computed(() => String(route.params.slug))

const billing = ref<BillingSummary>()
const subscription = ref<Subscription>()
const error = ref<string>()
const busy = ref<string | null>(null)
const blocked = ref<{ plan: string; exceeded: ExceededLimit[] } | null>(null)

const checkoutState = computed(() => route.query.checkout)

async function load() {
  try {
    ;[billing.value, subscription.value] = await Promise.all([
      getBillingSummary(slug.value),
      getSubscription(slug.value),
    ])
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : 'Could not load billing.'
  }
}
onMounted(load)

const selfHosted = computed(() => subscription.value?.mode === 'self_hosted')

type UsageEntry = {
  key: string
  label: string
  used: number
  limit: number | null | undefined
  storage: boolean
}
/** Nothing at all on a self-hosted instance: there are no commercial quotas to report. */
const entries = (): UsageEntry[] =>
  billing.value && !selfHosted.value
    ? [
        {
          key: 'humans',
          label: 'People',
          used: billing.value.usage.humans,
          limit: billing.value.limits.seatsHuman,
          storage: false,
        },
        {
          key: 'agents',
          label: 'Agent identities',
          used: billing.value.usage.agents,
          limit: billing.value.limits.seatsAgent,
          storage: false,
        },
        {
          key: 'projects',
          label: 'Projects',
          used: billing.value.usage.projects,
          limit: billing.value.limits.projects,
          storage: false,
        },
        {
          key: 'storage',
          label: 'Attachments',
          used: billing.value.usage.storageBytes,
          limit: billing.value.limits.storageBytes,
          storage: true,
        },
      ]
    : []

const GIB = 1_073_741_824
const display = (value: number, storage: boolean) =>
  storage ? `${(value / GIB).toFixed(2)} GiB` : value.toLocaleString()
const gib = (bytes: number) => {
  const value = bytes / GIB
  return Number.isInteger(value) ? String(value) : value.toFixed(1)
}
const days = (value: number) => `${value} day${value === 1 ? '' : 's'}`

/**
 * Retention is part of what the subscription buys, so it belongs on this page and not only
 * in the plan card. Absent means "not metered on this instance" rather than zero.
 */
const serviceAllowances = computed(() => {
  if (!billing.value || selfHosted.value) return []
  const { runLogDays, analyticsDays } = billing.value.limits
  return [
    ...(runLogDays != null
      ? [{ key: 'logs', label: 'Finished-run raw logs', value: days(runLogDays) }]
      : []),
    ...(analyticsDays != null
      ? [{ key: 'analytics', label: 'Analytics history', value: days(analyticsDays) }]
      : []),
  ]
})

const selling = computed(() => subscription.value?.mode === 'saas' && subscription.value.enabled)
const plans = computed(() => subscription.value?.plans ?? [])
const currentPlan = computed(
  () => subscription.value?.subscribedPlan ?? subscription.value?.plan ?? '',
)
/** A running evaluation is an entitlement, not a subscription: there is still something to buy. */
const subscribed = computed(() => subscription.value?.subscribedPlan != null)
/** A retired plan is shown only to the organization that is on it; nobody else is offered it. */
const offered = computed(() => offeredPlans(plans.value, currentPlan.value))
const isOwner = computed(() => organizations.current?.role === 'owner')
const evaluation = computed(() => evaluationNotice(subscription.value))
const founding = computed(() => foundingNotice(subscription.value))
/** What this organization actually pays each month - the founding price while one is running. */
const currentPrice = computed(() => {
  const plan = plans.value.find((option) => option.code === currentPlan.value)
  if (!plan) return null
  const offer = founding.value
  return offer && !offer.converted ? `$${offer.price} per organization / month` : planPriceLabel(plan)
})

function scrollToPlans() {
  document.getElementById('plans')?.scrollIntoView({ behavior: 'smooth', block: 'start' })
}

function changeLabel(plan: PlanOption) {
  const change = planChange(currentPlan.value, plan, plans.value)
  if (change !== 'current') return change === 'upgrade' ? `Upgrade to ${plan.code}` : `Downgrade to ${plan.code}`
  return subscribed.value ? 'Current plan' : 'Subscribe'
}

const changeDisabled = (plan: PlanOption) =>
  !selling.value ||
  !plan.purchasable ||
  busy.value !== null ||
  (planChange(currentPlan.value, plan, plans.value) === 'current' && subscribed.value)

const go = (url: string) => navigation.assign(url)

async function choose(plan: PlanOption) {
  busy.value = plan.code
  blocked.value = null
  try {
    const result = await startCheckout(slug.value, plan.code)
    if (result.url) {
      go(result.url)
      return
    }
    toast.success(
      `Moving to ${plan.code}.`,
      'Stripe confirms the change in a moment; refresh to see it.',
    )
    await load()
  } catch (cause) {
    const exceeded = exceededFrom(cause)
    if (exceeded) blocked.value = { plan: plan.code, exceeded }
    else toast.error(cause)
  } finally {
    busy.value = null
  }
}

async function manage() {
  busy.value = 'portal'
  try {
    const result = await openBillingPortal(slug.value)
    if (result.url) go(result.url)
  } catch (cause) {
    toast.error(cause)
  } finally {
    busy.value = null
  }
}

const formatDate = (value: string | Date | null) =>
  value ? new Date(value).toLocaleDateString() : ''

/** How much of the founding offer is left and what the organization pays when it ends. */
const foundingLine = computed(() => {
  const offer = founding.value
  if (!offer) return ''
  const from = offer.standardFrom ? formatDate(offer.standardFrom) : null
  const standard = `$${offer.renewalPrice} per organization / month`
  if (offer.converted) {
    return `Completed${from ? ` on ${from}` : ''} - this organization now pays ${standard}.`
  }
  return `${offer.periodsBilled} of ${offer.periods} billed · ${standard}${from ? ` from ${from}` : ''}.`
})

/** What a plan includes, said as allowances rather than caps - nulls are the unlimited case. */
const allowances = (plan: PlanOption): string[] => {
  const lines: string[] = []
  const { seatsHuman, seatsAgent, projects, storageBytes, runLogDays, analyticsDays } = plan.limits
  if (seatsHuman == null && seatsAgent == null && projects == null) {
    lines.push('Unlimited people, agents and projects')
  } else {
    lines.push(`${seatsHuman ?? 'Unlimited'} humans · ${seatsAgent ?? 'unlimited'} agents`)
    lines.push(`${projects ?? 'Unlimited'} projects`)
  }
  if (plan.includedAgentsPerHuman) lines.push(`${plan.includedAgentsPerHuman} agents included per human`)
  if (storageBytes != null) lines.push(`${gib(storageBytes)} GiB attachments`)
  if (runLogDays != null) lines.push(`${days(runLogDays)} of run logs`)
  if (analyticsDays != null) lines.push(`${days(analyticsDays)} of analytics`)
  return lines
}
</script>

<template>
  <UiPageState v-if="error" state="error" title="Billing could not be loaded." :description="error" />
  <UiPageState v-else-if="!billing || !subscription" state="loading" />
  <section v-else class="max-w-3xl space-y-8">
    <div>
      <h1 class="text-xl font-semibold">Plan</h1>
      <p v-if="selfHosted" class="text-muted-foreground mt-1 text-sm">
        This is a self-hosted instance: every organization has the full product, there are no quotas and there is
        nothing to pay for.
      </p>
      <p v-else class="text-muted-foreground mt-1 text-sm">
        Current plan: <strong class="text-foreground" data-testid="current-plan">{{ billing.plan }}</strong>
        <template v-if="currentPrice"> · <span data-testid="current-price">{{ currentPrice }}</span></template>
        <template v-if="subscription.currentPeriodEnd">
          · {{ subscription.cancelAtPeriodEnd ? 'ends' : 'renews' }} {{ formatDate(subscription.currentPeriodEnd) }}
        </template>
      </p>
    </div>

    <div v-if="evaluation" data-testid="evaluation-panel">
      <template v-if="!evaluation.expired">
        <p role="status" class="text-sm">
          <strong class="text-foreground">Evaluation</strong>
          <span class="text-muted-foreground">
            · {{ evaluation.daysLeft }} day{{ evaluation.daysLeft === 1 ? '' : 's' }} left · ends
            {{ formatDate(evaluation.endsAt) }}
          </span>
        </p>
        <p class="text-muted-foreground mt-1 text-xs">
          Nothing is charged when it ends. Subscribe before then to keep writing; reading and exporting keep working
          either way.
        </p>
      </template>
      <div v-else role="alert" class="border-warning/40 bg-warning/10 rounded-md border px-3 py-2 text-sm">
        <p>
          Your evaluation ended on {{ formatDate(evaluation.endsAt) }} - reading, downloading and exporting still
          work, but new changes and agent runs are paused. Subscribe to continue.
        </p>
        <Button v-if="isOwner" class="mt-2" size="sm" data-testid="evaluation-cta" @click="scrollToPlans">
          Choose a plan
        </Button>
        <p v-else class="text-muted-foreground mt-2 text-xs">Ask an owner to choose a plan.</p>
      </div>
    </div>

    <div v-if="founding" class="border-border rounded-md border px-3 py-2 text-sm" data-testid="founding-offer">
      <p>
        <strong>Founding offer</strong>
        <span class="text-muted-foreground">
          · ${{ founding.price }} per organization / month for {{ founding.periods }} monthly billing periods
        </span>
      </p>
      <p class="text-muted-foreground mt-1 text-xs">{{ foundingLine }}</p>
      <p class="text-muted-foreground mt-1 text-xs">
        The offer is granted by Aictiq and cannot be requested here. It is the same hosted plan either way.
      </p>
    </div>

    <p
      v-if="checkoutState === 'success'"
      role="status"
      class="border-primary/35 bg-primary/5 rounded-md border px-3 py-2 text-sm"
    >
      Thanks - Stripe has your payment. The new plan applies as soon as Stripe confirms it, usually within seconds.
    </p>
    <p v-else-if="checkoutState === 'canceled'" role="status" class="text-muted-foreground text-sm">
      Checkout was cancelled; nothing changed.
    </p>

    <p
      v-if="subscription.graceEndsAt"
      role="alert"
      data-testid="payment-alert"
      :class="[
        'rounded-md border px-3 py-2 text-sm',
        subscription.readOnly
          ? 'border-destructive/40 bg-destructive/10 text-destructive'
          : 'border-warning/40 bg-warning/10',
      ]"
    >
      <template v-if="subscription.readOnly">
        A payment failed and the grace period ended on {{ formatDate(subscription.graceEndsAt) }}. The organization is
        read-only until the payment method is updated.
      </template>
      <template v-else>
        A payment failed. Update the payment method before {{ formatDate(subscription.graceEndsAt) }} or the
        organization becomes read-only.
      </template>
    </p>

    <div v-if="entries().length" class="space-y-4">
      <div v-for="entry in entries()" :key="entry.key" class="space-y-2" :data-testid="`usage-${entry.key}`">
        <div class="flex justify-between text-sm">
          <span>{{ entry.label }}</span>
          <span>
            {{ display(entry.used, entry.storage) }}
            <template v-if="entry.limit != null"> / {{ display(entry.limit, entry.storage) }}</template>
            <template v-else>· Unlimited</template>
          </span>
        </div>
        <template v-if="entry.limit != null">
          <div class="bg-muted h-2 overflow-hidden rounded">
            <div
              class="bg-primary h-full"
              :style="{ width: `${Math.min(100, (entry.used / entry.limit) * 100)}%` }"
            />
          </div>
          <p
            v-if="entry.storage && entry.used > entry.limit"
            class="text-muted-foreground text-xs"
            data-testid="storage-over"
          >
            Delete attachments to free space - existing files stay readable and downloadable. There is no larger
            allowance to buy.
          </p>
        </template>
      </div>

      <dl v-if="serviceAllowances.length" class="space-y-1" data-testid="service-allowances">
        <div
          v-for="allowance in serviceAllowances"
          :key="allowance.key"
          class="flex justify-between text-sm"
          :data-testid="`allowance-${allowance.key}`"
        >
          <dt>{{ allowance.label }}</dt>
          <dd>{{ allowance.value }}</dd>
        </div>
        <p class="text-muted-foreground pt-1 text-xs">
          Older raw logs are deleted and cannot be brought back by subscribing later. Run records, summaries, linked
          pull requests and item history are kept regardless.
        </p>
      </dl>
    </div>

    <template v-if="subscription.mode === 'saas'">
      <p v-if="!subscription.enabled" class="text-muted-foreground text-sm" data-testid="billing-unavailable">
        Plans cannot be changed here: billing is not configured on this instance.
      </p>

      <div v-else id="plans" class="space-y-3">
        <div class="flex flex-wrap items-center justify-between gap-2">
          <h2 class="font-medium">Change plan</h2>
          <Button
            v-if="subscription.hasBillingAccount"
            variant="outline"
            size="sm"
            :disabled="busy !== null"
            data-testid="manage-billing"
            @click="manage"
          >
            Manage billing &amp; invoices
          </Button>
        </div>

        <div
          v-if="blocked"
          role="alert"
          class="border-warning/40 bg-warning/10 rounded-md border px-3 py-2 text-sm"
          data-testid="downgrade-blocked"
        >
          <p class="font-medium">To move to {{ blocked.plan }}, first:</p>
          <ul class="mt-1 list-disc pl-5">
            <li v-for="limit in blocked.exceeded" :key="limit.limit">{{ describeExceeded(limit) }}</li>
          </ul>
        </div>

        <div class="grid gap-3 sm:grid-cols-2">
          <div
            v-for="plan in offered"
            :key="plan.code"
            class="border-border flex flex-col gap-2 rounded-lg border p-4"
            :data-testid="`plan-${plan.code}`"
          >
            <div class="flex items-baseline justify-between">
              <strong class="capitalize">{{ plan.code }}</strong>
              <span class="text-muted-foreground text-xs">{{ planPriceLabel(plan) }}</span>
            </div>
            <span v-if="!plan.purchasable" class="text-muted-foreground text-xs">
              No longer available - it stays until you move off it.
            </span>
            <ul class="text-muted-foreground text-xs">
              <li v-for="line in allowances(plan)" :key="line">{{ line }}</li>
            </ul>
            <Button
              class="mt-auto"
              size="sm"
              :variant="planChange(currentPlan, plan, plans) === 'upgrade' ? 'default' : 'outline'"
              :disabled="changeDisabled(plan)"
              @click="choose(plan)"
            >
              {{ busy === plan.code ? 'Working…' : changeLabel(plan) }}
            </Button>
          </div>
        </div>
      </div>
    </template>
  </section>
</template>
