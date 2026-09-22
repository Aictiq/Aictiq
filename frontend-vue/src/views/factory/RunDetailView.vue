<script setup lang="ts">
import { ExternalLink, Hand } from '@lucide/vue'
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import { useRoute } from 'vue-router'

import { cancelRun, getRun } from '@/api/runs'
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
  formatCost,
  formatTokens,
  isLiveRun,
  projectKeyOf,
  runDuration,
  runRequesterLabel,
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
            <dd class="text-foreground inline">{{ run.runnerName ?? '-' }}</dd>
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
