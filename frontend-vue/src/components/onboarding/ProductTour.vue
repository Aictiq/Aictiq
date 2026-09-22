<script setup lang="ts">
import { useMediaQuery, useWindowSize } from '@vueuse/core'
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue'
import { useRouter } from 'vue-router'

import { Button } from '@/components/ui/button'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { useShortcut } from '@/composables/useShortcuts'
import { useOnboardingStore } from '@/stores/onboarding'

/**
 * The welcome dialog and the spotlight callout of the product tour, hosted in
 * one component mounted once by the shell. Everything it shows comes from the onboarding
 * store; the step copy and routing live in `lib/onboarding.ts`.
 *
 * The overlay never blocks the page: only the callout card takes the pointer, because a
 * tour people can't click around is a tour they dismiss. A missing anchor (a section the
 * person has not got yet, or the phone's hidden rail) falls back to a centered card, and
 * on small screens the card is a bottom sheet.
 */
const onboarding = useOnboardingStore()
const router = useRouter()
const isMobile = useMediaQuery('(max-width: 767px)')
const viewport = useWindowSize()

const steps = computed(() => onboarding.steps)
const step = computed(() => steps.value.find((s) => s.id === onboarding.currentStepId))
const stepIndex = computed(() => steps.value.findIndex((s) => s.id === onboarding.currentStepId))
const isFirst = computed(() => stepIndex.value <= 0)
const isLast = computed(() => stepIndex.value === steps.value.length - 1)

/** The live target element and its rect, once found. */
const target = ref<HTMLElement | null>(null)
const rect = ref<DOMRect | null>(null)
const cardEl = ref<HTMLElement | null>(null)
const cardSize = ref({ width: 320, height: 176 })
/** The step wanted an anchor and the wait ran out - the fallback card offers Retry. */
const anchorMissing = ref(false)

let pollTimer: ReturnType<typeof setTimeout> | undefined
let detachTarget: (() => void) | null = null

function measure() {
  const el = target.value
  if (el) rect.value = el.getBoundingClientRect()
}

function watchTarget(el: HTMLElement) {
  target.value = el
  measure()
  // Capture, because every scroll surface in the app is an inner element, not the window.
  window.addEventListener('scroll', measure, true)
  window.addEventListener('resize', measure)
  detachTarget = () => {
    window.removeEventListener('scroll', measure, true)
    window.removeEventListener('resize', measure)
  }
}

function stopWatchingTarget() {
  detachTarget?.()
  detachTarget = null
  target.value = null
}

/**
 * Activates the current step: navigate where it points, then wait (up to 5s) for its
 * anchor to appear. A cancelled navigation or a missing anchor leaves the rect unset,
 * which renders the centered fallback - never an advance, never an error.
 */
async function activate() {
  stopWatchingTarget()
  if (pollTimer) clearTimeout(pollTimer)
  rect.value = null
  anchorMissing.value = false

  const current = step.value
  if (!current || !onboarding.tourActive) return
  const token = current.id

  const destination = current.route?.(onboarding.ctx) ?? null
  if (destination) {
    const path = router.resolve(destination).path
    if (path !== router.currentRoute.value.path) {
      const moved = await router.push(destination).then(
        () => true,
        () => false,
      )
      if (!moved || step.value?.id !== token) return
    }
  }

  const anchor = current.anchor(onboarding.ctx)
  if (!anchor) return

  let waited = 0
  while (step.value?.id === token && onboarding.tourActive && waited < 5000) {
    const el = document.querySelector<HTMLElement>(`[data-tour="${anchor}"]`)
    if (el && el.getClientRects().length > 0) {
      watchTarget(el)
      return
    }
    await new Promise<void>((resolve) => {
      pollTimer = setTimeout(resolve, 100)
    })
    waited += 100
  }
  // Still nothing: the control is behind the mobile drawer, or on a screen this person
  // does not have. The card reads on its own, and Retry looks again once they open it.
  if (step.value?.id === token && onboarding.tourActive) anchorMissing.value = true
}

watch(
  () => [onboarding.tourActive, onboarding.currentStepId] as const,
  () => void activate(),
  { immediate: true },
)

onBeforeUnmount(() => {
  stopWatchingTarget()
  if (pollTimer) clearTimeout(pollTimer)
})

/**
 * The control the tour was started from, so Finish, Skip or Later can hand focus back
 * rather than dropping it at the top of the document.
 */
let returnFocusTo: HTMLElement | null = null

watch(
  () => onboarding.tourActive,
  (active, wasActive) => {
    if (active && !wasActive) {
      const origin = document.activeElement
      returnFocusTo = origin instanceof HTMLElement ? origin : null
      return
    }
    if (!active && wasActive) {
      const target = returnFocusTo
      returnFocusTo = null
      if (target?.isConnected) target.focus()
    }
  },
)

// Each step takes focus, so the callout is reachable without hunting for it, and the
// announced heading is what a screen reader lands on.
watch(
  () => [onboarding.currentStepId, cardEl.value] as const,
  async () => {
    if (!onboarding.tourActive) return
    await nextTick()
    const card = cardEl.value
    if (card && !card.contains(document.activeElement)) card.focus()
  },
)

// The card's size is only known once rendered; positions clamp against it.
watch([cardEl, rect, step], async () => {
  await nextTick()
  const el = cardEl.value
  if (el) cardSize.value = { width: el.offsetWidth, height: el.offsetHeight }
})

const MARGIN = 8
const GAP = 12

function clamp(value: number, min: number, max: number): number {
  return Math.min(Math.max(value, min), Math.max(min, max))
}

const spotlightStyle = computed(() => {
  const r = rect.value
  if (!r || isMobile.value) return null
  return { left: `${r.left}px`, top: `${r.top}px`, width: `${r.width}px`, height: `${r.height}px` }
})

const cardStyle = computed(() => {
  if (isMobile.value) return {}
  const width = Math.min(cardSize.value.width, viewport.width.value - MARGIN * 2)
  const height = cardSize.value.height
  const r = rect.value

  if (!r) {
    return {
      left: `${Math.max(MARGIN, (viewport.width.value - width) / 2)}px`,
      top: `${Math.max(MARGIN, (viewport.height.value - height) / 2)}px`,
      width: `${width}px`,
    }
  }

  const left = clamp(
    r.left + r.width / 2 - width / 2,
    MARGIN,
    viewport.width.value - width - MARGIN,
  )
  let top = r.bottom + GAP
  if (top + height > viewport.height.value - MARGIN) {
    const above = r.top - GAP - height
    if (above >= MARGIN) {
      top = above
    } else {
      // Beside the target: right first, then left, vertically pinned to the target.
      let beside = left
      const onRight = r.right + GAP
      const onLeft = r.left - GAP - width
      if (onRight + width <= viewport.width.value - MARGIN) beside = onRight
      else if (onLeft >= MARGIN) beside = onLeft
      return { left: `${beside}px`, top: `${clamp(r.top, MARGIN, viewport.height.value - height - MARGIN)}px`, width: `${width}px` }
    }
  }
  return { left: `${left}px`, top: `${top}px`, width: `${width}px` }
})

const cta = computed(() => step.value?.cta?.(onboarding.ctx) ?? null)

function runCta() {
  const action = cta.value
  if (!action) return
  onboarding.pause()
  if (action.run === 'open-checklist') onboarding.openChecklist()
}

/** Closing the dialog from Escape or the overlay is "later", not "never". */
function onWelcomeOpenChange(open: boolean) {
  if (!open && onboarding.welcomeOpen) void onboarding.defer()
}

useShortcut(
  'escape',
  () => void onboarding.defer(),
  { allowInInput: true, when: () => onboarding.tourActive },
)
</script>

<template>
  <Dialog :open="onboarding.welcomeOpen" @update:open="onWelcomeOpenChange">
    <DialogContent class="sm:max-w-md">
      <DialogHeader>
        <DialogTitle>Welcome to Aictiq</DialogTitle>
        <DialogDescription>
          Find your way around, prepare a ticket, and learn how to hand it to an agent.
        </DialogDescription>
      </DialogHeader>
      <div class="flex flex-wrap items-center justify-end gap-2">
        <Button type="button" variant="ghost" @click="onboarding.dismiss()">Skip tour</Button>
        <Button type="button" variant="secondary" @click="onboarding.defer()">Later</Button>
        <Button type="button" @click="onboarding.startTour()">Start tour</Button>
      </div>
    </DialogContent>
  </Dialog>

  <div v-if="onboarding.tourActive && step" class="pointer-events-none fixed inset-0 z-60">
    <div
      v-if="spotlightStyle"
      aria-hidden="true"
      class="pointer-events-none fixed z-60 rounded-md shadow-lg ring-2 ring-primary/70 transition-all motion-reduce:transition-none"
      :style="spotlightStyle"
    />
    <div
      ref="cardEl"
      role="dialog"
      aria-modal="false"
      tabindex="-1"
      :aria-label="step.title"
      class="bg-popover border-border pointer-events-auto fixed z-60 rounded-lg border p-4 shadow-2xl outline-none"
      :class="isMobile ? 'inset-x-0 bottom-0 rounded-b-none' : ''"
      :style="isMobile ? undefined : cardStyle"
    >
      <p class="font-label text-muted-foreground" aria-live="polite">
        Step {{ stepIndex + 1 }} of {{ steps.length }}
      </p>
      <h2 class="mt-1 text-sm font-semibold">{{ step.title }}</h2>
      <p class="text-muted-foreground mt-1.5 text-[13px] leading-relaxed">
        {{ step.body(onboarding.ctx) }}
      </p>
      <div v-if="cta || anchorMissing" class="mt-3 flex flex-wrap items-center gap-2">
        <Button v-if="cta" type="button" variant="secondary" size="sm" @click="runCta">
          {{ cta.label }}
        </Button>
        <Button v-if="anchorMissing" type="button" variant="outline" size="sm" @click="activate">
          Retry
        </Button>
      </div>
      <div class="mt-3 flex flex-wrap items-center justify-between gap-2">
        <div class="flex items-center gap-1">
          <Button type="button" variant="ghost" size="sm" @click="onboarding.dismiss()">
            Skip tour
          </Button>
          <Button type="button" variant="ghost" size="sm" @click="onboarding.defer()">
            Later
          </Button>
        </div>
        <div class="flex items-center gap-2">
          <Button type="button" variant="ghost" size="sm" :disabled="isFirst" @click="onboarding.back()">
            Back
          </Button>
          <Button type="button" size="sm" @click="onboarding.next()">
            {{ isLast ? 'Finish tour' : 'Next' }}
          </Button>
        </div>
      </div>
    </div>
  </div>
</template>
