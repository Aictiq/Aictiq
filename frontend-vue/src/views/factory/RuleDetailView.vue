<script setup lang="ts">
import { MoreHorizontal } from '@lucide/vue'
import { computed, ref, watch } from 'vue'
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import { useRoute, useRouter } from 'vue-router'

import { listLabels, type Label } from '@/api/labels'
import {
  deleteRule,
  listOrgRules,
  listRuleFirings,
  ruleSkipReasonText,
  type Rule,
} from '@/api/rules'
import { listWorkflows, type WorkflowState } from '@/api/workflows'
import EmptyState from '@/components/common/EmptyState.vue'
import KeyChip from '@/components/common/KeyChip.vue'
import RuleDialog from '@/components/factory/RuleDialog.vue'
import RunStatusBadge from '@/components/factory/RunStatusBadge.vue'
import SettingsSection from '@/components/settings/SettingsSection.vue'
import UiPageState from '@/components/UiPageState.vue'
import { Button } from '@/components/ui/button'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'
import { useToast } from '@/composables/useToast'
import type { RunStatus } from '@/api/runs'
import { factoryPath, factoryRunPath } from '@/router/paths'
import { ApiError } from '@/utils/api'

/**
 * One rule: what it watches for, and its last 50 firings. There is no
 * `GET /orgs/{slug}/rules/{id}` — a deep link resolves the rule (and, from it, the
 * project the project-scoped endpoints need) out of the same grouped listing the Rules
 * tab already uses, the same way a pasted rule id only ever means something to an Admin
 * of the project it belongs to.
 */
const route = useRoute()
const router = useRouter()
const toast = useToast()
const client = useQueryClient()

const slug = computed(() => String(route.params.slug ?? ''))
const ruleId = computed(() => String(route.params.ruleId ?? ''))

const rulesQuery = useQuery({
  queryKey: computed(() => [slug.value, 'rules']),
  queryFn: () => listOrgRules(slug.value),
})
const rule = computed<Rule | null>(
  () => rulesQuery.data.value?.find((entry) => entry.id === ruleId.value) ?? null,
)
const notFound = computed(
  () =>
    !rulesQuery.isPending.value &&
    ((rulesQuery.isError.value &&
      rulesQuery.error.value instanceof ApiError &&
      rulesQuery.error.value.status === 404) ||
      (rulesQuery.isSuccess.value && rule.value === null)),
)

const states = ref<WorkflowState[]>([])
const labels = ref<Label[]>([])
watch(
  rule,
  async (current) => {
    if (!current) return
    const [workflows, projectLabels] = await Promise.all([
      listWorkflows(slug.value, current.projectKey),
      listLabels(slug.value, current.projectKey),
    ])
    states.value = workflows.find((workflow) => workflow.isDefault)?.states ?? []
    labels.value = projectLabels
  },
  { immediate: true },
)

const stateName = computed(
  () => states.value.find((state) => state.id === rule.value?.triggerStateId)?.name ?? '—',
)
const labelName = computed(() =>
  rule.value?.requiredLabelId
    ? (labels.value.find((label) => label.id === rule.value?.requiredLabelId)?.name ?? '—')
    : null,
)

const firingsQuery = useQuery({
  queryKey: computed(() => [slug.value, 'rules', ruleId.value, 'firings']),
  queryFn: () => listRuleFirings(slug.value, rule.value!.projectKey, ruleId.value),
  enabled: computed(() => rule.value !== null),
})
const firings = computed(() => firingsQuery.data.value ?? [])

const editing = ref(false)
const deleting = ref(false)

function onSaved() {
  editing.value = false
  void client.invalidateQueries({ queryKey: [slug.value, 'rules'] })
}

async function remove() {
  const current = rule.value
  if (!current) return
  deleting.value = true
  try {
    await deleteRule(slug.value, current.projectKey, current.id)
    toast.success(`${current.name} deleted.`)
    await router.push(factoryPath(slug.value, 'rules'))
  } catch (error) {
    toast.error(error)
  } finally {
    deleting.value = false
  }
}
</script>

<template>
  <SettingsSection wide>
    <UiPageState v-if="rulesQuery.isPending.value" state="loading" />

    <EmptyState
      v-else-if="notFound"
      title="Rule not found"
      description="It does not exist, or you cannot see it. Those look the same from here on purpose."
      icon="◇"
    >
      <Button variant="secondary" @click="router.push(factoryPath(slug, 'rules'))">
        Back to rules
      </Button>
    </EmptyState>

    <p v-else-if="rulesQuery.isError.value" class="text-destructive p-3 text-sm">
      The rule could not be loaded.
    </p>

    <div v-else-if="rule" class="space-y-5">
      <div class="border-border rounded-lg border p-4">
        <div class="flex flex-wrap items-start justify-between gap-3">
          <div class="min-w-0">
            <h2 class="text-sm font-medium">{{ rule.name }}</h2>
            <p class="text-muted-foreground mt-0.5 text-xs">
              {{ rule.projectName }} ({{ rule.projectKey }})
            </p>
          </div>
          <DropdownMenu>
            <DropdownMenuTrigger as-child>
              <Button variant="ghost" size="icon" :aria-label="`Manage ${rule.name}`">
                <MoreHorizontal class="size-4" aria-hidden="true" />
              </Button>
            </DropdownMenuTrigger>
            <DropdownMenuContent align="end">
              <DropdownMenuItem @select="editing = true">Edit</DropdownMenuItem>
              <DropdownMenuItem variant="destructive" :disabled="deleting" @select="remove">
                Delete
              </DropdownMenuItem>
            </DropdownMenuContent>
          </DropdownMenu>
        </div>

        <p class="text-muted-foreground mt-3 text-sm">
          When an item enters <span class="text-foreground font-medium">{{ stateName }}</span
          ><template v-if="labelName">
            with label <span class="text-foreground font-medium">{{ labelName }}</span></template
          >
          → run <span class="text-foreground font-medium">{{ rule.playbookName }}</span> as
          <span class="text-foreground font-medium">{{ rule.agentName ?? 'an agent' }}</span>
        </p>
        <p class="text-muted-foreground mt-2 text-xs">
          {{ rule.enabled ? 'Enabled' : 'Disabled' }}
        </p>
      </div>

      <section>
        <h3 class="text-sm font-medium">Firings</h3>
        <p class="text-muted-foreground mt-0.5 text-xs">The last 50, newest first.</p>

        <UiPageState v-if="firingsQuery.isPending.value" state="loading" />
        <EmptyState
          v-else-if="firings.length === 0"
          title="Never fired yet"
          description="Nothing has moved into this state and label combination yet."
          icon="◇"
          class="mt-2"
        />
        <ul v-else class="border-border divide-border mt-2 divide-y overflow-hidden rounded-lg border">
          <li
            v-for="firing in firings"
            :key="firing.eventId"
            class="flex flex-wrap items-center gap-x-3 gap-y-1 px-3 py-2.5 text-sm"
          >
            <span class="text-muted-foreground text-xs">{{ new Date(firing.at).toLocaleString() }}</span>
            <RouterLink :to="{ query: { item: firing.itemKey } }" :aria-label="`Open ${firing.itemKey}`">
              <KeyChip :label="firing.itemKey" />
            </RouterLink>
            <span class="ml-auto">
              <RouterLink
                v-if="firing.runId"
                :to="factoryRunPath(slug, firing.runId)"
                class="inline-flex items-center gap-1.5"
              >
                <RunStatusBadge v-if="firing.runStatus" :status="firing.runStatus as RunStatus" />
                <span class="text-primary text-xs hover:underline">Open run</span>
              </RouterLink>
              <span v-else-if="firing.skipReason" class="text-muted-foreground text-xs">
                {{ ruleSkipReasonText(firing.skipReason) }}
              </span>
            </span>
          </li>
        </ul>
      </section>
    </div>

    <RuleDialog
      v-if="rule"
      :slug="slug"
      :project-key="rule.projectKey"
      :states="states"
      :labels="labels"
      :rule="rule"
      :open="editing"
      @update:open="(open: boolean) => (editing = open)"
      @saved="onSaved"
    />
  </SettingsSection>
</template>
