<script setup lang="ts">
import { Loader2, MoreHorizontal } from '@lucide/vue'
import { computed, ref, watch } from 'vue'
import { RouterLink } from 'vue-router'

import { deleteRule, listRules, ruleSkipReasonText, updateRule, type Rule } from '@/api/rules'
import { listLabels, type Label } from '@/api/labels'
import { listProjects, type Project } from '@/api/projects'
import { listWorkflows, type WorkflowState } from '@/api/workflows'
import EmptyState from '@/components/common/EmptyState.vue'
import FactoryDocsLink from '@/components/factory/FactoryDocsLink.vue'
import RuleDialog from '@/components/factory/RuleDialog.vue'
import SettingsSection from '@/components/settings/SettingsSection.vue'
import UiPageState from '@/components/UiPageState.vue'
import { Button } from '@/components/ui/button'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'
import { useOrgScope } from '@/composables/useSettingsScope'
import { useToast } from '@/composables/useToast'
import { factoryRulePath } from '@/router/paths'
import { ApiError, ConflictError } from '@/utils/api'

/**
 * Factory → Rules: "when an item enters this state, optionally with this label, run this
 * playbook as this agent" — grouped by project, one section per project the caller
 * administers. A project contributes a (possibly empty) section here for the same reason
 * Playbooks lists every visible project rather than only the ones with playbooks already:
 * an Admin needs somewhere to create the first rule.
 */
interface RuleGroup {
  project: Project
  rules: Rule[]
  states: WorkflowState[]
  labels: Label[]
}

const org = useOrgScope()
const toast = useToast()

const groups = ref<RuleGroup[]>([])
const loading = ref(true)
const failed = ref(false)
const busyId = ref<string | null>(null)
const editing = ref<{ group: RuleGroup; rule: Rule | null } | null>(null)
const confirmingDelete = ref<{ group: RuleGroup; rule: Rule } | null>(null)

async function load() {
  loading.value = true
  failed.value = false
  try {
    // Archived projects are read-only, not hidden — their rules still show, just without
    // the actions to change them.
    const projects = (await listProjects(org.slug.value, true)).filter(
      (project) => project.role === 'admin',
    )
    groups.value = await Promise.all(
      projects.map(async (project) => {
        const [rules, workflows, labels] = await Promise.all([
          listRules(org.slug.value, project.key),
          listWorkflows(org.slug.value, project.key),
          listLabels(org.slug.value, project.key),
        ])
        return {
          project,
          rules: [...rules].sort((left, right) => left.name.localeCompare(right.name)),
          states: workflows.find((workflow) => workflow.isDefault)?.states ?? [],
          labels,
        }
      }),
    )
  } catch (error) {
    groups.value = []
    failed.value = true
    toast.error(error)
  } finally {
    loading.value = false
  }
}

watch(() => org.slug.value, load, { immediate: true })

const hasAnyRule = computed(() => groups.value.some((group) => group.rules.length > 0))

function stateName(group: RuleGroup, id: string): string {
  return group.states.find((state) => state.id === id)?.name ?? 'State unavailable'
}

function labelName(group: RuleGroup, id: string | null): string | null {
  if (!id) return null
  return group.labels.find((label) => label.id === id)?.name ?? 'Label unavailable'
}

function mayManage(group: RuleGroup): boolean {
  return group.project.role === 'admin' && !group.project.isArchived
}

function replace(group: RuleGroup, saved: Rule) {
  group.rules = group.rules
    .map((entry) => (entry.id === saved.id ? saved : entry))
    .sort((left, right) => left.name.localeCompare(right.name))
}

async function toggleEnabled(group: RuleGroup, rule: Rule) {
  if (busyId.value) return
  busyId.value = rule.id
  try {
    const saved = await updateRule(org.slug.value, group.project.key, rule.id, {
      enabled: !rule.enabled,
      version: rule.version,
    })
    replace(group, saved)
  } catch (error) {
    if (error instanceof ConflictError) {
      toast.error(new Error('Someone changed this rule first. The list has been refreshed.'))
      await load()
    } else {
      toast.error(error)
    }
  } finally {
    busyId.value = null
  }
}

function onSaved(group: RuleGroup, saved: Rule) {
  if (editing.value?.rule) replace(group, saved)
  else
    group.rules = [...group.rules, saved].sort((left, right) => left.name.localeCompare(right.name))
  editing.value = null
}

async function remove() {
  const pending = confirmingDelete.value
  if (!pending) return
  busyId.value = pending.rule.id
  try {
    await deleteRule(org.slug.value, pending.group.project.key, pending.rule.id)
    pending.group.rules = pending.group.rules.filter((entry) => entry.id !== pending.rule.id)
    confirmingDelete.value = null
    toast.success(`${pending.rule.name} deleted.`)
  } catch (error) {
    if (error instanceof ApiError && error.status === 404) {
      pending.group.rules = pending.group.rules.filter((entry) => entry.id !== pending.rule.id)
      confirmingDelete.value = null
    } else {
      toast.error(error)
    }
  } finally {
    busyId.value = null
  }
}
</script>

<template>
  <SettingsSection wide>
    <header class="pb-3">
      <h2 class="text-sm font-medium">Rules</h2>
      <p class="text-muted-foreground mt-0.5 text-xs">
        When an item enters a state — optionally carrying a label — a rule hands it to an agent
        automatically. Moving an item twice starts one run, not two.
      </p>
    </header>

    <UiPageState v-if="loading" state="loading" />
    <UiPageState
      v-else-if="failed"
      state="error"
      title="Could not load rules"
      description="Try the request again."
    >
      <Button variant="secondary" @click="load">Try again</Button>
    </UiPageState>
    <EmptyState
      v-else-if="groups.length === 0"
      title="No projects to automate"
      description="Rules belong to projects where you are an Admin."
      icon="◇"
    >
      <FactoryDocsLink label="Learn about automation rules" />
    </EmptyState>

    <div v-else class="space-y-6" data-testid="rules-groups">
      <section v-for="group in groups" :key="group.project.id">
        <div class="mb-2 flex flex-wrap items-center justify-between gap-3">
          <div>
            <h3 class="text-sm font-medium">{{ group.project.name }}</h3>
            <p class="text-muted-foreground text-xs">{{ group.project.key }}</p>
          </div>
          <Button
            v-if="mayManage(group)"
            size="sm"
            data-testid="new-rule"
            @click="editing = { group, rule: null }"
          >
            New rule
          </Button>
        </div>

        <div
          v-if="group.rules.length === 0"
          class="border-border text-muted-foreground rounded-lg border border-dashed p-5 text-sm"
        >
          No rules in this project yet.
          <span v-if="!mayManage(group)">A project Admin can create one.</span>
          <div class="mt-3">
            <FactoryDocsLink label="Learn about automation rules" />
          </div>
        </div>

        <ul v-else class="border-border divide-border overflow-hidden divide-y rounded-lg border">
          <li v-for="rule in group.rules" :key="rule.id" class="flex items-start gap-3 px-3 py-3">
            <button
              type="button"
              role="switch"
              :aria-checked="rule.enabled"
              :aria-label="`${rule.enabled ? 'Disable' : 'Enable'} ${rule.name}`"
              :disabled="!mayManage(group) || busyId !== null"
              class="mt-0.5 inline-flex h-5 w-9 shrink-0 items-center rounded-full border px-0.5 transition-colors disabled:opacity-50"
              :class="rule.enabled ? 'bg-primary border-primary' : 'bg-muted border-border'"
              data-testid="rule-toggle"
              @click="toggleEnabled(group, rule)"
            >
              <span
                class="bg-background inline-block size-4 rounded-full shadow transition-transform"
                :class="rule.enabled ? 'translate-x-4' : 'translate-x-0'"
              />
            </button>

            <div class="min-w-0 flex-1">
              <RouterLink
                :to="factoryRulePath(org.slug.value, rule.id)"
                class="font-medium hover:underline"
              >
                {{ rule.name }}
              </RouterLink>
              <p class="text-muted-foreground mt-1 text-xs">
                When an item enters
                <span class="text-foreground font-medium">{{
                  stateName(group, rule.triggerStateId)
                }}</span
                ><template v-if="labelName(group, rule.requiredLabelId)">
                  with label
                  <span class="text-foreground font-medium">{{
                    labelName(group, rule.requiredLabelId)
                  }}</span></template
                >
                → run
                <span class="text-foreground font-medium">{{ rule.playbookName }}</span> as
                <span class="text-foreground font-medium">{{ rule.agentName ?? 'an agent' }}</span>
              </p>
              <p v-if="rule.lastFiring" class="text-muted-foreground mt-1 text-xs">
                Last fired {{ new Date(rule.lastFiring.at).toLocaleString() }} on
                {{ rule.lastFiring.itemKey }} ·
                <RouterLink
                  v-if="rule.lastFiring.runId"
                  class="text-primary hover:underline"
                  :to="`/o/${org.slug.value}/factory/runs/${rule.lastFiring.runId}`"
                >
                  view run
                </RouterLink>
                <span v-else-if="rule.lastFiring.skipReason">{{
                  ruleSkipReasonText(rule.lastFiring.skipReason)
                }}</span>
              </p>
              <p v-else class="text-muted-foreground mt-1 text-xs">Never fired yet.</p>
            </div>

            <Loader2
              v-if="busyId === rule.id"
              class="text-muted-foreground size-4 animate-spin"
              aria-hidden="true"
            />
            <DropdownMenu v-else-if="mayManage(group)">
              <DropdownMenuTrigger as-child>
                <Button variant="ghost" size="icon" :aria-label="`Manage ${rule.name}`">
                  <MoreHorizontal class="size-4" aria-hidden="true" />
                </Button>
              </DropdownMenuTrigger>
              <DropdownMenuContent align="end">
                <DropdownMenuItem @select="editing = { group, rule }">Edit</DropdownMenuItem>
                <DropdownMenuItem
                  variant="destructive"
                  @select="confirmingDelete = { group, rule }"
                >
                  Delete
                </DropdownMenuItem>
              </DropdownMenuContent>
            </DropdownMenu>
          </li>
        </ul>
      </section>

      <EmptyState
        v-if="!hasAnyRule"
        title="No rules yet"
        icon="◇"
        description="A rule watches one state. When an item lands there — with the right label, if you set one — it starts a run the same way Hand to agent would."
      >
        <FactoryDocsLink label="Learn about automation rules" />
      </EmptyState>
    </div>

    <RuleDialog
      v-if="editing"
      :slug="org.slug.value"
      :project-key="editing.group.project.key"
      :states="editing.group.states"
      :labels="editing.group.labels"
      :rule="editing.rule"
      :open="editing !== null"
      @update:open="(open: boolean) => !open && (editing = null)"
      @saved="(saved: Rule) => editing && onSaved(editing.group, saved)"
    />

    <Dialog
      :open="confirmingDelete !== null"
      @update:open="
        (open: boolean) => {
          if (!open) confirmingDelete = null
        }
      "
    >
      <DialogContent class="sm:max-w-md">
        <DialogHeader>
          <DialogTitle>Delete {{ confirmingDelete?.rule.name }}?</DialogTitle>
          <DialogDescription>
            Items already moved through {{ confirmingDelete?.rule.name }} keep their runs. Nothing
            new fires once this is gone.
          </DialogDescription>
        </DialogHeader>
        <DialogFooter>
          <Button variant="ghost" @click="confirmingDelete = null">Cancel</Button>
          <Button variant="destructive" :disabled="busyId !== null" @click="remove">Delete</Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  </SettingsSection>
</template>
