<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import { CircleCheck, Loader2, MessageCircleQuestion, Sparkles, TriangleAlert } from '@lucide/vue'
import { transitionItem, type WorkItem } from '@/api/items'
import { listPlaybooks, playbookHarnesses, type Playbook } from '@/api/playbooks'
import {
  confirmRefinement,
  getRefinement,
  getRefinementSettings,
  refineItem,
  type RefineBody,
} from '@/api/refinement'
import { listRunnerChoices, type RunnerChoice } from '@/api/runners'
import RefineRunPicker from '@/components/items/RefineRunPicker.vue'
import { Button } from '@/components/ui/button'
import { useToast } from '@/composables/useToast'
import { refinementLabels } from '@/lib/refinement'
import { readRefineRunChoice, runnerCanRun, writeRefineRunChoice } from '@/lib/runs'
import { factoryRunPath, projectSettingsPath } from '@/router/paths'
import { useOrganizationsStore } from '@/stores/organizations'

/**
 * Where an item's refinement stands, above its description: the agent at work, its
 * questions with room for the answers, or the refined ticket waiting for the person to
 * review and confirm. When refinement is off, a notice links to its project settings.
 * Every refine run - the first, answers, asking for changes - goes with the runner and
 * harness picked beside its button, preselected from what this project used last.
 */
const props = defineProps<{
  slug: string
  projectKey: string
  item: WorkItem
  /** Unsaved title or description edits: confirming would leave them behind. */
  dirty: boolean
  /** A run is working the item, so it cannot be refined now. */
  busy: boolean
}>()

const client = useQueryClient()
const toast = useToast()
const organizations = useOrganizationsStore()
const canOperate = computed(() => organizations.current?.canOperateFactory === true)

const refinement = useQuery({
  queryKey: computed(() => [props.slug, props.item.key, 'refinement']),
  queryFn: () => getRefinement(props.slug, props.item.key),
  enabled: canOperate,
})
const settings = useQuery({
  queryKey: computed(() => [props.slug, props.projectKey, 'refinement-settings']),
  queryFn: () => getRefinementSettings(props.slug, props.projectKey),
  enabled: canOperate,
})
const current = computed(() => refinement.data.value ?? null)
const status = computed(() => current.value?.status ?? null)
const active = computed(() => status.value !== null && status.value !== 'confirmed')
const canRefine = computed(() => canOperate.value && settings.data.value?.enabled === true)

// Without either list the run still goes to any free runner with the playbook's harness.
const playbooks = useQuery({
  queryKey: computed(() => [props.slug, props.projectKey, 'playbooks']),
  queryFn: () => listPlaybooks(props.slug, props.projectKey).catch(() => [] as Playbook[]),
  enabled: canRefine,
})
const runnerChoices = useQuery({
  queryKey: computed(() => [props.slug, 'runner-choices']),
  queryFn: () => listRunnerChoices(props.slug).catch(() => [] as RunnerChoice[]),
  enabled: canRefine,
})
const runners = computed(() => runnerChoices.data.value ?? [])
const playbookHarness = computed(
  () =>
    playbooks.data.value?.find((p) => p.id === settings.data.value?.playbookId)?.harness ?? null,
)

/** Null is "any free runner". */
const runnerId = ref<string | null>(null)
const harness = ref<string | null>(null)
/** The choice is preselected once per project, then left to the person. */
const choiceReady = ref(false)

watch(
  () => props.projectKey,
  () => {
    choiceReady.value = false
  },
)

watch(
  [() => playbooks.data.value, () => runnerChoices.data.value, choiceReady],
  ([projectPlaybooks, choices, ready]) => {
    if (ready || !projectPlaybooks || !choices) return
    // The choice this project used last, if it still names things that exist and fit.
    const remembered = readRefineRunChoice(props.projectKey)
    harness.value =
      remembered?.harness && (playbookHarnesses as readonly string[]).includes(remembered.harness)
        ? remembered.harness
        : playbookHarness.value
    const rememberedRunner = choices.find((runner) => runner.id === remembered?.runnerId)
    runnerId.value =
      rememberedRunner && runnerCanRun(rememberedRunner, harness.value) ? rememberedRunner.id : null
    choiceReady.value = true
  },
  { immediate: true },
)

// Another harness may rule the chosen runner out.
watch(harness, (chosen) => {
  const runner = runners.value.find((r) => r.id === runnerId.value)
  if (runner && !runnerCanRun(runner, chosen)) runnerId.value = null
})

const answers = ref<string[]>([])
const feedback = ref('')
const askingForChanges = ref(false)
const working = ref<'refine' | 'confirm' | null>(null)

watch(
  () => current.value?.questions,
  (questions) => {
    answers.value = (questions ?? []).map(() => '')
  },
  { immediate: true },
)

const hasAnswer = computed(() => answers.value.some((answer) => answer.trim().length > 0))

async function refresh() {
  await Promise.all([
    client.invalidateQueries({ queryKey: [props.slug, props.item.key] }),
    client.invalidateQueries({ queryKey: [props.slug, props.projectKey] }),
  ])
}

async function refine(body: RefineBody = {}) {
  if (!canRefine.value || working.value) return
  working.value = 'refine'
  try {
    // One runner is no choice; the run goes to it as to any free runner.
    const choice = {
      runnerId: runners.value.length > 1 ? runnerId.value : null,
      harness: harness.value,
    }
    await refineItem(props.slug, props.item.key, { ...body, ...choice })
    writeRefineRunChoice(props.projectKey, { runnerId: runnerId.value, harness: harness.value })
    feedback.value = ''
    askingForChanges.value = false
    await refresh()
  } catch (caught) {
    toast.error(caught, 'Refining could not start.')
  } finally {
    working.value = null
  }
}

function sendAnswers() {
  const questions = current.value?.questions ?? []
  void refine({
    answers: questions.map((question, index) => ({
      question,
      answer: answers.value[index]?.trim() ?? '',
    })),
  })
}

async function confirm() {
  const refined = current.value
  if (!canOperate.value || !refined || working.value) return
  working.value = 'confirm'
  try {
    // The move is the person's own transition, so the workflow's rules apply to it.
    if (refined.refinedStateId && refined.refinedStateId !== props.item.stateId) {
      await transitionItem(props.slug, props.item.key, {
        toStateId: refined.refinedStateId,
        version: props.item.version,
      })
    }
    await confirmRefinement(props.slug, props.item.key, refined.version)
    toast.success(`${props.item.key} confirmed.`)
    await refresh()
  } catch (caught) {
    toast.error(caught, 'The ticket could not be confirmed.')
    await refresh()
  } finally {
    working.value = null
  }
}
</script>

<template>
  <div
    v-if="canOperate && settings.data.value?.enabled === false"
    class="border-border bg-muted/30 text-muted-foreground mt-2 flex items-start gap-2 rounded-md border p-3 text-sm"
    data-testid="refinement-not-configured"
    role="status"
  >
    <Sparkles class="mt-0.5 size-4 shrink-0" aria-hidden="true" />
    <p>
      Ticket refinement is not set up for this project.
      <RouterLink
        :to="`${projectSettingsPath(slug, projectKey, 'factory')}#ticket-refinement`"
        class="text-primary underline underline-offset-2"
      >
        View refinement settings
      </RouterLink>
    </p>
  </div>
  <section
    v-if="canOperate && active && current"
    class="border-border bg-muted/30 mt-5 rounded-md border p-4"
    data-testid="refinement-panel"
    :data-status="current.status"
  >
    <div class="flex flex-wrap items-center gap-2">
      <Loader2 v-if="status === 'refining'" class="text-primary size-4 animate-spin" />
      <MessageCircleQuestion v-else-if="status === 'needsInput'" class="text-primary size-4" />
      <CircleCheck v-else-if="status === 'ready'" class="text-primary size-4" />
      <TriangleAlert v-else class="text-destructive size-4" />
      <p class="font-label">{{ refinementLabels[current.status] }}</p>
      <RouterLink
        v-if="canOperate && current.lastRunId"
        :to="factoryRunPath(slug, current.lastRunId)"
        class="text-muted-foreground ml-auto text-xs underline-offset-2 hover:underline"
        >View run</RouterLink
      >
    </div>

    <p v-if="status === 'refining'" class="text-muted-foreground mt-2 text-sm">
      An agent is reading the description, the attachments and the code, and will rewrite this
      ticket or ask what it needs. It starts as soon as a runner is free.
    </p>
    <p v-else-if="current.summary" class="mt-2 text-sm whitespace-pre-line">
      {{ current.summary }}
    </p>

    <form
      v-if="status === 'needsInput'"
      class="mt-3 space-y-3"
      novalidate
      @submit.prevent="sendAnswers"
    >
      <div v-for="(question, index) in current.questions" :key="index" class="space-y-1.5">
        <label :for="`refinement-answer-${index}`" class="text-sm font-medium">{{
          question
        }}</label>
        <textarea
          :id="`refinement-answer-${index}`"
          v-model="answers[index]"
          rows="2"
          maxlength="4000"
          class="border-input bg-background w-full rounded border px-2 py-1.5 text-sm"
          data-testid="refinement-answer"
        />
      </div>
      <RefineRunPicker
        v-if="canRefine && choiceReady"
        v-model:runner-id="runnerId"
        v-model:harness="harness"
        :runners="runners"
        :disabled="working !== null"
      />
      <div class="flex flex-wrap gap-2">
        <Button
          type="submit"
          size="sm"
          :disabled="!canRefine || props.busy || working !== null || !hasAnswer"
          data-testid="refinement-send-answers"
        >
          <Loader2 v-if="working === 'refine'" class="animate-spin" aria-hidden="true" />
          <Sparkles v-else aria-hidden="true" />
          Answer and refine
        </Button>
        <Button
          type="button"
          size="sm"
          variant="ghost"
          :disabled="working !== null || dirty"
          @click="confirm"
        >
          Confirm as it is
        </Button>
      </div>
    </form>

    <div v-else-if="status === 'ready' || status === 'failed'" class="mt-3 space-y-3">
      <p v-if="status === 'ready'" class="text-muted-foreground text-sm">
        Review the title and description above and edit anything that is off, then confirm the
        ticket.
      </p>
      <p v-else class="text-muted-foreground text-sm">
        The agent stopped without a refined ticket. Try again, or finish the ticket by hand and
        confirm it.
      </p>
      <div v-if="askingForChanges" class="space-y-1.5">
        <label for="refinement-feedback" class="text-sm font-medium">What should change?</label>
        <textarea
          id="refinement-feedback"
          v-model="feedback"
          rows="2"
          maxlength="4000"
          class="border-input bg-background w-full rounded border px-2 py-1.5 text-sm"
        />
      </div>
      <RefineRunPicker
        v-if="canRefine && choiceReady && (askingForChanges || status === 'failed')"
        v-model:runner-id="runnerId"
        v-model:harness="harness"
        :runners="runners"
        :disabled="working !== null"
      />
      <p v-if="dirty" class="text-muted-foreground text-xs">Save your edits before confirming.</p>
      <div class="flex flex-wrap gap-2">
        <Button
          size="sm"
          :disabled="working !== null || dirty"
          data-testid="refinement-confirm"
          @click="confirm"
        >
          <Loader2 v-if="working === 'confirm'" class="animate-spin" aria-hidden="true" />
          Confirm ticket
        </Button>
        <Button
          v-if="canRefine && !askingForChanges"
          size="sm"
          variant="outline"
          :disabled="working !== null || props.busy"
          @click="status === 'failed' ? refine() : (askingForChanges = true)"
        >
          <Sparkles aria-hidden="true" />
          {{ status === 'failed' ? 'Try again' : 'Ask for changes' }}
        </Button>
        <Button
          v-else-if="canRefine"
          size="sm"
          variant="outline"
          :disabled="working !== null || props.busy || !feedback.trim()"
          @click="refine({ feedback })"
        >
          <Loader2 v-if="working === 'refine'" class="animate-spin" aria-hidden="true" />
          <Sparkles v-else aria-hidden="true" />
          Refine again
        </Button>
      </div>
    </div>
  </section>
  <div
    v-else-if="canRefine && !refinement.isPending.value && !props.busy"
    class="mt-2 flex flex-wrap items-end gap-3"
  >
    <RefineRunPicker
      v-if="choiceReady"
      v-model:runner-id="runnerId"
      v-model:harness="harness"
      :runners="runners"
      :disabled="working !== null"
    />
    <Button
      size="sm"
      variant="default"
      data-testid="refinement-start"
      :disabled="working !== null"
      @click="refine()"
    >
      <Loader2 v-if="working === 'refine'" class="animate-spin" aria-hidden="true" />
      <Sparkles v-else aria-hidden="true" />
      Refine ticket
    </Button>
  </div>
</template>
