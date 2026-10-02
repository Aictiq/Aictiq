<script setup lang="ts">
import { ExternalLink, Hand, RotateCcw, StepForward } from '@lucide/vue'
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import { useRoute, useRouter } from 'vue-router'

import { cancelRun, continueRun, dispatchRun, getRun } from '@/api/runs'
import type { Run } from '@/api/runs'
import { getProject, hasProjectRole } from '@/api/projects'
import KeyChip from '@/components/common/KeyChip.vue'
import UserAvatar from '@/components/common/UserAvatar.vue'
import EmptyState from '@/components/common/EmptyState.vue'
import RunLog from '@/components/factory/RunLog.vue'
import RunStatusBadge from '@/components/factory/RunStatusBadge.vue'
import UiPageState from '@/components/UiPageState.vue'
import { Button } from '@/components/ui/button'
import { useRunRealtime } from '@/composables/useRunRealtime'
import { useToast } from '@/composables/useToast'
import { useSessionStore } from '@/stores/session'
import {
  canCancelRun,
  canRetryRun,
  chainTotals,
  formatCost,
  formatTokens,
  isLiveRun,
  projectKeyOf,
  runDuration,
  runRequesterLabel,
  runScheduledLabel,
} from '@/lib/runs'
import { ApiError } from '@/utils/api'
import SettingsSection from '@/components/settings/SettingsSection.vue'

/**
 * One run: what was asked (the item, the playbook, the agent), what it did (the log,
 * live while it runs), and what it left behind (a pull request, a cost, a state).
 *
 * The page sits inside the Factory area's operator gate, so everyone here may read the
 * log; cancelling is narrower - the person who dispatched the run, or an Admin.
 */
const route = useRoute()
const router = useRouter()
const session = useSessionStore()
const toast = useToast()
const client = useQueryClient()

const slug = computed(() => String(route.params.slug ?? ''))
const runId = computed(() => String(route.params.runId ?? ''))

const runQuery = useQuery({
  queryKey: computed(() => [slug.value, 'runs', runId.value]),
  queryFn: () => getRun(slug.value, runId.value),
})
const run = computed(() => runQuery.data.value ?? null)
const notFound = computed(
  () =>
    runQuery.isError.value &&
    runQuery.error.value instanceof ApiError &&
    runQuery.error.value.status === 404,
)

// The run's project, for the caller's role in it: a project Admin may cancel anyone's run.
const projectKey = computed(() => (run.value ? projectKeyOf(run.value.itemKey) : ''))
const projectQuery = useQuery({
  queryKey: computed(() => ['projects', slug.value, projectKey.value]),
  queryFn: () => getProject(slug.value, projectKey.value),
  enabled: computed(() => projectKey.value !== ''),
})

const itemPath = computed(() =>
  run.value
    ? `/o/${slug.value}/p/${projectKeyOf(run.value.itemKey)}/items/${run.value.itemKey}`
    : '',
)

// "4 min ago" and a running duration only move when someone asks the time again.
const now = ref(new Date())
let tick: ReturnType<typeof setInterval> | undefined
onMounted(() => {
  tick = setInterval(() => (now.value = new Date()), 30_000)
})
onBeforeUnmount(() => clearInterval(tick))

const cancelling = ref(false)
async function cancel() {
  const current = run.value
  if (!current || cancelling.value) return
  cancelling.value = true
  try {
    await cancelRun(slug.value, current.id)
    toast.info(
      current.status === 'queued'
        ? 'Run cancelled before a runner took it.'
        : 'Cancellation requested - the runner stops at its next check.',
    )
  } catch (error) {
    if (error instanceof ApiError && error.status === 409) {
      toast.info('This run is already finished.')
    } else {
      toast.error(error)
    }
  } finally {
    cancelling.value = false
    await client.invalidateQueries({ queryKey: [slug.value, 'runs', current.id] })
    await client.invalidateQueries({ queryKey: [slug.value, current.itemKey, 'runs'] })
    await client.invalidateQueries({ queryKey: [slug.value, current.itemKey] })
  }
}

// Continue resumes the failed run's session on the runner that kept its workspace; Retry
// starts over from a fresh checkout. Both make a new run and open it.
const starting = ref<'continue' | 'retry' | null>(null)
async function startNext(how: 'continue' | 'retry') {
  const current = run.value
  if (!current || starting.value) return
  starting.value = how
  try {
    const next: Run =
      how === 'continue'
        ? await continueRun(slug.value, current.id)
        : await dispatchRun(slug.value, current.itemKey, {
            playbookId: current.playbookId,
            agentId: current.agentId,
          })
    await client.invalidateQueries({ queryKey: [slug.value, current.itemKey] })
    await router.push(`/o/${slug.value}/factory/runs/${next.id}`)
  } catch (error) {
    if (error instanceof ApiError && error.status === 409) {
      toast.info(error.problem?.detail ?? error.title)
    } else {
      toast.error(error)
    }
    await client.invalidateQueries({ queryKey: [slug.value, 'runs', current.id] })
  } finally {
    starting.value = null
  }
}

const mayRetry = computed(() => (run.value ? canRetryRun(run.value) : false))
const chain = computed(() => run.value?.chain ?? null)
const chainTotal = computed(() => {
  if (!chain.value) return null
  const totals = chainTotals(chain.value)
  const parts = [
    formatCost(totals.costUsd),
    totals.inputTokens !== null || totals.outputTokens !== null
      ? `${formatTokens(totals.inputTokens) ?? '-'} in · ${formatTokens(totals.outputTokens) ?? '-'} out`
      : null,
  ].filter(Boolean)
  return parts.length > 0 ? parts.join(' · ') : null
})

const logRef = ref<InstanceType<typeof RunLog> | null>(null)

// Status changes ride the project group; the log rides the run's own group. Both are
// hints: the run refetches its detail, the log heals its tail through its endpoint.
useRunRealtime({
  organizationSlug: slug,
  runId,
  projectKey: computed(() => projectKey.value || undefined),
  onLog: () => logRef.value?.tail(),
  onChanged: (event) => {
    void client.invalidateQueries({ queryKey: [slug.value, 'runs', event.runId] })
    if (event.status && !isLiveRun(event.status)) logRef.value?.flush()
    if (event.cancelRequested) logRef.value?.tail()
  },
})

const mayCancel = computed(() =>
  run.value
    ? canCancelRun(run.value, {
        userId: session.user?.id,
        isProjectAdmin: hasProjectRole(projectQuery.data.value?.role, 'admin'),
      })
    : false,
)

const requester = computed(() => (run.value ? runRequesterLabel(run.value) : null))
const duration = computed(() => (run.value ? runDuration(run.value, now.value) : null))
const cost = computed(() => (run.value ? formatCost(run.value.costUsd) : null))
const tokens = computed(() => {
  const current = run.value
  if (!current || (current.inputTokens === null && current.outputTokens === null)) return null
  return `${formatTokens(current.inputTokens) ?? '-'} in · ${formatTokens(current.outputTokens) ?? '-'} out`
})
</script>

<template>
  <SettingsSection wide>
    <UiPageState v-if="runQuery.isPending.value" state="loading" />

    <EmptyState
      v-else-if="notFound"
      title="Run not found"
      description="It does not exist, or you cannot see its project. Those look the same from here on purpose."
      icon="◇"
    >
      <Button variant="secondary" @click="$router.push(`/o/${slug}/factory/runs`)">
        Back to runs
      </Button>
    </EmptyState>

    <p v-else-if="runQuery.isError.value" class="text-destructive p-3 text-sm">
      The run could not be loaded.
    </p>

    <div v-else-if="run" class="space-y-5">
      <div class="border-border rounded-lg border p-4">
        <div class="flex flex-wrap items-center gap-x-3 gap-y-2">
          <RunStatusBadge :status="run.status" />
          <span
            v-if="runScheduledLabel(run)"
            class="text-muted-foreground text-xs"
            data-testid="run-scheduled"
            >{{ runScheduledLabel(run) }}</span
          >
          <RouterLink
            :to="itemPath"
            class="hover:bg-muted inline-flex items-center gap-1.5 rounded px-1 py-0.5"
            :title="`Open ${run.itemKey}`"
          >
            <KeyChip :label="run.itemKey" />
          </RouterLink>
          <span class="text-muted-foreground text-xs uppercase">{{ run.harness }}</span>
          <div class="min-w-0">
            <div class="flex items-center gap-1.5">
              <UserAvatar :name="run.agentName ?? run.agentId" is-agent size="sm" />
              <span class="truncate text-sm font-medium">{{ run.agentName ?? 'Agent' }}</span>
            </div>
          </div>

          <div v-if="run.continuable || mayRetry" class="ml-auto flex items-center gap-2">
            <Button
              v-if="run.continuable"
              size="sm"
              :disabled="starting !== null"
              data-testid="run-continue"
              title="Resume the agent's session where it stopped, on the same runner"
              @click="startNext('continue')"
            >
              <StepForward class="size-3.5" aria-hidden="true" />
              Continue
            </Button>
            <Button
              v-if="mayRetry"
              variant="outline"
              size="sm"
              :disabled="starting !== null"
              data-testid="run-retry"
              title="Start a fresh run from a new checkout"
              @click="startNext('retry')"
            >
              <RotateCcw class="size-3.5" aria-hidden="true" />
              Retry
            </Button>
          </div>

          <Button
            v-if="mayCancel"
            variant="outline"
            size="sm"
            class="ml-auto"
            :disabled="cancelling"
            data-testid="run-cancel"
            @click="cancel"
          >
            <Hand class="size-3.5" aria-hidden="true" />
            {{ run.status === 'queued' ? 'Cancel run' : 'Request cancel' }}
          </Button>
        </div>

        <dl class="text-muted-foreground mt-3 grid grid-cols-2 gap-x-6 gap-y-1.5 text-xs sm:grid-cols-3">
          <div>
            <dt class="inline">Playbook&nbsp;</dt>
            <dd class="text-foreground inline">{{ run.playbookName ?? '-' }}</dd>
          </div>
          <div>
            <dt class="inline">Runner&nbsp;</dt>
            <dd class="text-foreground inline" data-testid="run-runner">
              <template v-if="run.runnerName">{{ run.runnerName }}</template>
              <template v-else-if="run.requestedRunnerName">
                {{ run.requestedRunnerName }} <span class="text-muted-foreground">(requested)</span>
              </template>
              <template v-else>-</template>
            </dd>
          </div>
          <div v-if="requester" data-testid="run-requester">
            <dt class="inline">Requested by&nbsp;</dt>
            <dd class="text-foreground inline">{{ requester }}</dd>
          </div>
          <div v-if="duration">
            <dt class="inline">Duration&nbsp;</dt>
            <dd class="text-foreground inline">{{ duration }}</dd>
          </div>
          <div>
            <dt class="inline">Queued&nbsp;</dt>
            <dd class="text-foreground inline">{{ run.queuedAt ? new Date(run.queuedAt).toLocaleString() : '-' }}</dd>
          </div>
          <div v-if="run.scheduledFor">
            <dt class="inline">Start at&nbsp;</dt>
            <dd class="text-foreground inline">{{ new Date(run.scheduledFor).toLocaleString() }}</dd>
          </div>
          <div v-if="run.startedAt">
            <dt class="inline">Started&nbsp;</dt>
            <dd class="text-foreground inline">{{ new Date(run.startedAt).toLocaleString() }}</dd>
          </div>
          <div v-if="run.finishedAt">
            <dt class="inline">Finished&nbsp;</dt>
            <dd class="text-foreground inline">{{ new Date(run.finishedAt).toLocaleString() }}</dd>
          </div>
          <div v-if="cost || tokens">
            <dt class="inline">Cost&nbsp;</dt>
            <dd class="text-foreground inline">
              {{ [cost, tokens].filter(Boolean).join(' · ') }}
            </dd>
          </div>
          <div v-if="run.exitCode !== null">
            <dt class="inline">Exit&nbsp;</dt>
            <dd class="text-foreground inline font-mono">{{ run.exitCode }}</dd>
          </div>
          <div v-if="run.maxMinutes">
            <dt class="inline">Limit&nbsp;</dt>
            <dd class="text-foreground inline">{{ run.maxMinutes }} min</dd>
          </div>
          <div v-if="run.sessionId" data-testid="run-session">
            <dt class="inline">Session&nbsp;</dt>
            <dd class="text-foreground inline font-mono">{{ run.sessionId }}</dd>
          </div>
          <div v-if="run.continuesRunId" data-testid="run-continues">
            <dt class="inline">{{ run.autoContinued ? 'Auto-continues' : 'Continues' }}&nbsp;</dt>
            <dd class="inline">
              <RouterLink
                :to="`/o/${slug}/factory/runs/${run.continuesRunId}`"
                class="text-primary underline underline-offset-2"
              >
                the failed run
              </RouterLink>
            </dd>
          </div>
          <div v-if="run.continuedByRunId" data-testid="run-continued-by">
            <dt class="inline">Continued as&nbsp;</dt>
            <dd class="inline">
              <RouterLink
                :to="`/o/${slug}/factory/runs/${run.continuedByRunId}`"
                class="text-primary underline underline-offset-2"
              >
                the next run
              </RouterLink>
            </dd>
          </div>
        </dl>

        <div v-if="run.pullRequestUrl" class="mt-3">
          <a
            :href="run.pullRequestUrl"
            target="_blank"
            rel="noreferrer"
            class="text-primary inline-flex items-center gap-1.5 text-sm underline underline-offset-2"
            data-testid="run-pr-link"
          >
            <ExternalLink class="size-3.5" aria-hidden="true" />
            Pull request
          </a>
        </div>

        <p v-if="run.outcomeSummary" class="border-border mt-3 rounded border px-2.5 py-2 text-sm">
          {{ run.outcomeSummary }}
        </p>
        <p
          v-if="run.failureReason"
          class="border-destructive/30 bg-destructive/5 text-destructive mt-3 rounded border px-2.5 py-2 text-sm"
          data-testid="run-failure-reason"
        >
          {{ run.failureReason }}
        </p>
      </div>

      <section v-if="chain" class="border-border rounded-lg border" data-testid="run-chain">
        <header class="flex flex-wrap items-baseline justify-between gap-2 px-4 pt-3 pb-2">
          <h2 class="text-sm font-medium">Continued runs</h2>
          <span v-if="chainTotal" class="text-muted-foreground text-xs" data-testid="run-chain-total">
            Total {{ chainTotal }}
          </span>
        </header>
        <ol class="divide-border divide-y border-t">
          <li v-for="(link, index) in chain" :key="link.id">
            <RouterLink
              :to="`/o/${slug}/factory/runs/${link.id}`"
              class="hover:bg-muted flex flex-wrap items-center gap-x-3 gap-y-1 px-4 py-2 text-xs"
              :class="{ 'bg-muted/60': link.id === run.id }"
              :aria-current="link.id === run.id ? 'page' : undefined"
            >
              <span class="text-muted-foreground w-12 shrink-0">
                {{ index === 0 ? 'First' : `#${index + 1}` }}
              </span>
              <RunStatusBadge :status="link.status" />
              <span v-if="link.autoContinued" class="text-muted-foreground italic">automatic</span>
              <span class="text-muted-foreground">{{ new Date(link.queuedAt).toLocaleString() }}</span>
              <span class="text-foreground ml-auto">
                {{
                  [
                    formatCost(link.costUsd),
                    link.inputTokens !== null || link.outputTokens !== null
                      ? `${formatTokens(link.inputTokens) ?? '-'} in · ${formatTokens(link.outputTokens) ?? '-'} out`
                      : null,
                  ]
                    .filter(Boolean)
                    .join(' · ') || '-'
                }}
              </span>
            </RouterLink>
          </li>
        </ol>
      </section>

      <details v-if="run.promptSnapshot" class="border-border rounded-lg border">
        <summary class="cursor-pointer px-4 py-2.5 text-sm font-medium select-none">
          The prompt the agent was given
        </summary>
        <pre
          class="text-muted-foreground overflow-x-auto px-4 pt-1 pb-4 font-mono text-xs whitespace-pre-wrap"
          >{{ run.promptSnapshot }}</pre
        >
      </details>

      <RunLog ref="logRef" :slug="slug" :run="run" />
    </div>
  </SettingsSection>
</template>
