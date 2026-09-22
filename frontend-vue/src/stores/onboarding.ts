import { defineStore } from 'pinia'
import { computed, ref } from 'vue'

import {
  getOnboarding,
  updateOnboarding,
  type OnboardingPreferences,
  type OnboardingStatus,
} from '@/api/onboarding'
import { tourSteps, type TourContext } from '@/lib/onboarding'
import { router } from '@/router'
import { useOrganizationsStore } from '@/stores/organizations'
import { useProjectsStore } from '@/stores/projects'
import { useSessionStore } from '@/stores/session'
import { useTeamsStore } from '@/stores/teams'
import { ConflictError } from '@/utils/api'

/**
 * The welcome dialog, the product tour and the Get started checklist, as one store.
 *
 * The server keeps one small preference record (status, last step, xmin version); this
 * store mirrors it and owns the *transient* surface state on top - what is open, which
 * step is showing. Every write is fire-and-forget: the tour must never block, and a
 * failed save leaves the app usable (Get started offers Retry). A 409 means another tab
 * finished or dismissed first - the server's record wins, and a terminal server state
 * quietly ends a tour this tab is still showing.
 */
export const useOnboardingStore = defineStore('onboarding', () => {
  const session = useSessionStore()
  const organizations = useOrganizationsStore()
  const projects = useProjectsStore()
  const teams = useTeamsStore()

  const status = ref<OnboardingStatus>('not_started')
  const version = ref(0)
  const lastStepId = ref<string | null>(null)
  /** GET succeeded at least once. */
  const loaded = ref(false)
  /** The last server conversation failed - the panel offers Retry. */
  const loadFailed = ref(false)

  const welcomeOpen = ref(false)
  const tourActive = ref(false)
  const currentStepId = ref<string | null>(null)
  const checklistOpen = ref(false)
  /** The tour finished or was dismissed in this tab; the welcome must not re-open until reload. */
  const sessionJustFinished = ref(false)

  const ctx = computed<TourContext>(() => ({
    hasOrganization: organizations.current !== null,
    canOperateFactory: organizations.current?.canOperateFactory ?? false,
    orgRole: organizations.current?.role,
    orgSlug: organizations.currentSlug,
    projectKey: projects.currentKey,
    hasProject: projects.current !== null,
    hasTeam: teams.current !== null,
  }))

  const steps = computed(() => tourSteps(ctx.value))

  /** True when a quiet "Resume tour" entry makes sense. */
  const resumeEligible = computed(
    () => loaded.value && (status.value === 'deferred' || (status.value === 'in_progress' && !tourActive.value)),
  )

  /**
   * Bumped on `clear()` so a response from a session that has already ended is dropped
   * rather than adopted into the next person's store.
   */
  let generation = 0

  function isTerminal(value: OnboardingStatus): boolean {
    return value === 'completed' || value === 'dismissed'
  }

  function adopt(record: OnboardingPreferences) {
    status.value = record.status
    version.value = record.version
    lastStepId.value = record.lastStepId
  }

  let bootstrapInFlight: Promise<void> | null = null

  /**
   * Reads the preference record once. Single-flight, like the session: AppShell is the
   * only caller, but a route change and the mount race. Never throws.
   */
  function bootstrap(): Promise<void> {
    if (!session.isAuthenticated || session.user?.isAgent) return Promise.resolve()
    bootstrapInFlight ??= (async () => {
      const mine = generation
      try {
        const record = await getOnboarding()
        if (mine !== generation) return
        adopt(record)
        loaded.value = true
        loadFailed.value = false
      } catch {
        if (mine !== generation) return
        loaded.value = false
        loadFailed.value = true
      } finally {
        bootstrapInFlight = null
      }
    })()
    return bootstrapInFlight
  }

  let welcomeShown = false

  /** Offered once per tab, only on an authenticated working page, only before anything is saved. */
  function maybeShowWelcome(): void {
    const route = router.currentRoute.value
    if (welcomeShown || welcomeOpen.value) return
    if (!loaded.value || status.value !== 'not_started') return
    if (tourActive.value || sessionJustFinished.value) return
    if (checklistOpen.value) return
    if (!route.meta.requiresAuth || route.name === 'onboarding') return
    // Wait for the first pages to settle so the dialog does not open over an empty shell.
    if (!organizations.isResolved || !projects.isResolved) return
    welcomeShown = true
    welcomeOpen.value = true
  }

  /** Fire-and-forget write; adopts the server's record, and a conflict re-reads it. */
  async function save(patch: { status: OnboardingStatus; lastStepId: string | null }): Promise<void> {
    const mine = generation
    try {
      const record = await updateOnboarding({ ...patch, version: version.value })
      if (mine !== generation) return
      adopt(record)
      loadFailed.value = false
    } catch (error) {
      if (mine !== generation) return
      if (error instanceof ConflictError) {
        // Another tab wrote first. The server's record is the only truthful one; a
        // terminal one ends a tour this tab is still showing.
        try {
          const record = await getOnboarding()
          if (mine !== generation) return
          adopt(record)
          loadFailed.value = false
        } catch {
          if (mine !== generation) return
          loadFailed.value = true
          return
        }
        if (isTerminal(status.value)) {
          tourActive.value = false
          welcomeOpen.value = false
          sessionJustFinished.value = true
        }
      } else {
        // Keep the local state; the checklist offers Retry. Never reject into the UI.
        loadFailed.value = true
      }
    }
  }

  function firstStepId(): string | null {
    return steps.value[0]?.id ?? null
  }

  function startTour(stepId?: string | null): void {
    if (steps.value.length === 0) return
    welcomeOpen.value = false
    checklistOpen.value = false
    tourActive.value = true
    const fromList = stepId ? steps.value.find((step) => step.id === stepId)?.id : undefined
    const target = fromList ?? firstStepId()
    currentStepId.value = target
    // Starting over on a fresh account is the one write a tour start makes. Resuming,
    // and replaying after a terminal status, change nothing on the server.
    if (stepId == null && status.value === 'not_started' && target) {
      void save({ status: 'in_progress', lastStepId: target })
    }
  }

  function goToStep(id: string): void {
    currentStepId.value = id
    if (!isTerminal(status.value)) {
      void save({ status: 'in_progress', lastStepId: id })
    }
  }

  function next(): void {
    const list = steps.value
    if (list.length === 0) return
    const index = list.findIndex((step) => step.id === currentStepId.value)
    if (index === -1) {
      const first = list[0]
      if (first) goToStep(first.id)
      return
    }
    const following = list[index + 1]
    if (following) goToStep(following.id)
    else void complete()
  }

  function back(): void {
    const list = steps.value
    const index = list.findIndex((step) => step.id === currentStepId.value)
    if (index > 0) {
      const previous = list[index - 1]
      if (previous) goToStep(previous.id)
    }
  }

  /**
   * A replay of a tour that already ended is a local session: it must leave the stored
   * preference exactly as it was, on the way out as well as on the way in. Without this,
   * pressing Skip at the end of a replay would trade a recorded completion (and its date)
   * for a dismissal - and so would a tab that has just adopted another tab's terminal
   * record after a 409.
   */
  function terminallyStored(): boolean {
    return isTerminal(status.value)
  }

  async function defer(): Promise<void> {
    const step = currentStepId.value
    tourActive.value = false
    welcomeOpen.value = false
    currentStepId.value = null
    if (terminallyStored()) return
    await save({ status: 'deferred', lastStepId: step })
  }

  async function dismiss(): Promise<void> {
    tourActive.value = false
    welcomeOpen.value = false
    currentStepId.value = null
    sessionJustFinished.value = true
    if (terminallyStored()) return
    await save({ status: 'dismissed', lastStepId: null })
  }

  async function complete(): Promise<void> {
    tourActive.value = false
    currentStepId.value = null
    sessionJustFinished.value = true
    if (terminallyStored()) return
    await save({ status: 'completed', lastStepId: null })
  }

  /**
   * A CTA exit: the person is going off to do what the step asked for. Stops the tour
   * without touching the saved preference - the tour resumes, it does not restart.
   */
  function pause(): void {
    tourActive.value = false
    currentStepId.value = null
  }

  /**
   * Opening Get started *is* taking up the welcome's offer, so the greeting is spent
   * rather than queued behind the sheet: two overlays competing for focus is the worst
   * of both, and on a Dialog/Sheet pair the dialog hides the sheet from assistive
   * technology entirely. A fresh account records `deferred`, which is what turns the
   * account menu's entry into the quiet "Resume tour" the ticket asks for - the tour
   * stays one click away instead of popping up again on the next visit.
   */
  function openChecklist(): void {
    const wasFailed = loadFailed.value
    loadFailed.value = false
    checklistOpen.value = true
    welcomeShown = true
    welcomeOpen.value = false
    if (loaded.value && status.value === 'not_started') {
      void save({ status: 'deferred', lastStepId: null })
    }
    if (wasFailed) void bootstrap()
  }

  function closeChecklist(): void {
    checklistOpen.value = false
  }

  /** On sign-out. Whatever this tab thought about the tour belonged to the last person. */
  function clear(): void {
    generation += 1
    status.value = 'not_started'
    version.value = 0
    lastStepId.value = null
    loaded.value = false
    loadFailed.value = false
    welcomeOpen.value = false
    tourActive.value = false
    currentStepId.value = null
    checklistOpen.value = false
    sessionJustFinished.value = false
    welcomeShown = false
  }

  return {
    status,
    version,
    lastStepId,
    loaded,
    loadFailed,
    welcomeOpen,
    tourActive,
    currentStepId,
    checklistOpen,
    sessionJustFinished,
    ctx,
    steps,
    resumeEligible,
    bootstrap,
    maybeShowWelcome,
    startTour,
    goToStep,
    next,
    back,
    defer,
    dismiss,
    complete,
    pause,
    openChecklist,
    closeChecklist,
    clear,
  }
})
