<script setup lang="ts">
import { Loader2 } from '@lucide/vue'
import { computed, ref, watch } from 'vue'

import { listAgents, type Agent } from '@/api/agents'
import type { Label } from '@/api/labels'
import { listPlaybooks, type Playbook } from '@/api/playbooks'
import { listProjectMembers } from '@/api/projects'
import { createRule, updateRule, type Rule, type UpdateRuleBody } from '@/api/rules'
import type { WorkflowState } from '@/api/workflows'
import EmptyState from '@/components/common/EmptyState.vue'
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
import { Input } from '@/components/ui/input'
import { useToast } from '@/composables/useToast'
import { orgSettingsPath, projectSettingsPath } from '@/router/paths'
import { ApiError, ConflictError } from '@/utils/api'

/**
 * Create or edit one rule. States and labels come from the caller (the Rules tab already
 * loaded them to render every row's sentence) — only the playbooks and the assignable
 * agents are this dialog's own fetch, the same two lists Hand to agent loads.
 *
 * Editing sends only the fields that changed, plus the version read with the rule — the
 * same discipline `StartRunDialog` and the playbook editor already follow.
 */
const props = defineProps<{
  slug: string
  projectKey: string
  states: WorkflowState[]
  labels: Label[]
  /** Null creates a new rule; otherwise the rule being edited. */
  rule: Rule | null
  open: boolean
}>()

const emit = defineEmits<{
  'update:open': [open: boolean]
  saved: [rule: Rule]
}>()

const toast = useToast()

const loading = ref(false)
const loadFailed = ref(false)
const playbooks = ref<Playbook[]>([])
const agents = ref<Agent[]>([])

const name = ref('')
const triggerStateId = ref('')
const requiredLabelId = ref('')
const playbookId = ref('')
const agentId = ref('')
const enabled = ref(true)

const submitting = ref(false)
const error = ref<string | null>(null)
const fieldErrors = ref<Record<string, string[]>>({})

const assignable = computed(() =>
  agents.value.filter((agent) => agent.isActive).sort((a, b) => a.displayName.localeCompare(b.displayName)),
)

function resetFields() {
  const rule = props.rule
  name.value = rule?.name ?? ''
  triggerStateId.value = rule?.triggerStateId ?? ''
  requiredLabelId.value = rule?.requiredLabelId ?? ''
  playbookId.value = rule?.playbookId ?? ''
  agentId.value = rule?.agentId ?? ''
  enabled.value = rule?.enabled ?? true
}

watch(
  () => props.open,
  async (open) => {
    if (!open) return
    error.value = null
    fieldErrors.value = {}
    loadFailed.value = false
    resetFields()
    loading.value = true
    try {
      const [projectPlaybooks, projectAgents, members] = await Promise.all([
        listPlaybooks(props.slug, props.projectKey),
        listAgents(props.slug),
        listProjectMembers(props.slug, props.projectKey),
      ])
      playbooks.value = projectPlaybooks
      const memberIds = new Set(members.map((member) => member.userId))
      agents.value = projectAgents.filter((agent) => memberIds.has(agent.userId))

      if (!props.rule) {
        playbookId.value = playbooks.value.find((p) => p.isDefault)?.id ?? playbooks.value[0]?.id ?? ''
        agentId.value = assignable.value[0]?.userId ?? ''
      }
    } catch {
      loadFailed.value = true
    } finally {
      loading.value = false
    }
  },
  { immediate: true },
)

const canSubmit = computed(
  () =>
    name.value.trim().length > 0 &&
    triggerStateId.value.length > 0 &&
    playbookId.value.length > 0 &&
    agentId.value.length > 0,
)

async function submit() {
  if (!canSubmit.value || submitting.value) return
  submitting.value = true
  error.value = null
  fieldErrors.value = {}
  try {
    let saved: Rule
    if (props.rule) {
      const original = props.rule
      const body: UpdateRuleBody = { version: original.version }
      if (name.value.trim() !== original.name) body.name = name.value.trim()
      if (triggerStateId.value !== original.triggerStateId) body.triggerStateId = triggerStateId.value
      if ((requiredLabelId.value || null) !== original.requiredLabelId) {
        body.requiredLabelId = requiredLabelId.value || null
      }
      if (playbookId.value !== original.playbookId) body.playbookId = playbookId.value
      if (agentId.value !== original.agentId) body.agentId = agentId.value
      if (enabled.value !== original.enabled) body.enabled = enabled.value
      saved = await updateRule(props.slug, props.projectKey, original.id, body)
    } else {
      saved = await createRule(props.slug, props.projectKey, {
        name: name.value.trim(),
        triggerStateId: triggerStateId.value,
        requiredLabelId: requiredLabelId.value || null,
        playbookId: playbookId.value,
        agentId: agentId.value,
        enabled: enabled.value,
      })
    }
    toast.success(`${saved.name} ${props.rule ? 'updated' : 'created'}.`)
    // Saved first: a parent that closes the dialog on update:open may drop the context
    // (which project's list to add to) it needs to handle the saved rule.
    emit('saved', saved)
    emit('update:open', false)
  } catch (caught) {
    if (caught instanceof ApiError && Object.keys(caught.fieldErrors).length > 0) {
      fieldErrors.value = caught.fieldErrors
    } else if (caught instanceof ConflictError) {
      error.value = 'Someone changed this rule first. Close this dialog and try again.'
    } else if (caught instanceof ApiError) {
      error.value = caught.problem?.detail ?? caught.title
    } else {
      error.value = 'Saving the rule failed.'
    }
  } finally {
    submitting.value = false
  }
}
</script>

<template>
  <Dialog :open="open" @update:open="(value: boolean) => emit('update:open', value)">
    <DialogContent class="sm:max-w-md">
      <DialogHeader>
        <DialogTitle>{{ rule ? 'Edit rule' : 'New rule' }}</DialogTitle>
        <DialogDescription>
          When an item enters the state below — carrying the label too, if one is set — it is
          handed to the agent automatically.
        </DialogDescription>
      </DialogHeader>

      <UiPageState v-if="loading" state="loading" />

      <EmptyState
        v-else-if="loadFailed"
        title="Could not load playbooks and agents"
        description="Try again in a moment."
        icon="◇"
      />

      <EmptyState
        v-else-if="playbooks.length === 0"
        title="No playbook yet"
        description="A rule needs a playbook to run. Create one first."
        icon="◇"
      >
        <Button @click="$router.push(projectSettingsPath(props.slug, props.projectKey, 'factory'))">
          Create a playbook
        </Button>
      </EmptyState>

      <EmptyState
        v-else-if="assignable.length === 0"
        title="No agent on this project"
        description="Add an agent to this project before automating it."
        icon="◇"
      >
        <Button @click="$router.push(orgSettingsPath(props.slug, 'agents'))">Manage agents</Button>
      </EmptyState>

      <form v-else id="rule-form" class="space-y-4" novalidate @submit.prevent="submit">
        <div class="space-y-1.5">
          <label for="rule-name" class="text-sm font-medium">Name</label>
          <Input
            id="rule-name"
            v-model="name"
            required
            placeholder="Start implementation"
            :aria-invalid="Boolean(fieldErrors.name)"
          />
          <p v-for="message in fieldErrors.name" :key="message" class="text-destructive text-xs">
            {{ message }}
          </p>
        </div>

        <div class="space-y-1.5">
          <label for="rule-state" class="text-sm font-medium">When an item enters</label>
          <select
            id="rule-state"
            v-model="triggerStateId"
            class="border-input bg-background h-9 w-full rounded-md border px-2 text-sm"
            :aria-invalid="Boolean(fieldErrors.triggerStateId)"
          >
            <option value="" disabled>Choose a state</option>
            <option v-for="state in states" :key="state.id" :value="state.id">
              {{ state.name }}
            </option>
          </select>
          <p
            v-for="message in fieldErrors.triggerStateId"
            :key="message"
            class="text-destructive text-xs"
          >
            {{ message }}
          </p>
        </div>

        <div class="space-y-1.5">
          <label for="rule-label" class="text-sm font-medium">Carrying the label</label>
          <select
            id="rule-label"
            v-model="requiredLabelId"
            class="border-input bg-background h-9 w-full rounded-md border px-2 text-sm"
            :aria-invalid="Boolean(fieldErrors.requiredLabelId)"
          >
            <option value="">No label needed</option>
            <option v-for="label in labels" :key="label.id" :value="label.id">
              {{ label.name }}
            </option>
          </select>
          <p
            v-for="message in fieldErrors.requiredLabelId"
            :key="message"
            class="text-destructive text-xs"
          >
            {{ message }}
          </p>
        </div>

        <div class="space-y-1.5">
          <label for="rule-playbook" class="text-sm font-medium">Run playbook</label>
          <select
            id="rule-playbook"
            v-model="playbookId"
            class="border-input bg-background h-9 w-full rounded-md border px-2 text-sm"
            :aria-invalid="Boolean(fieldErrors.playbookId)"
          >
            <option v-for="playbook in playbooks" :key="playbook.id" :value="playbook.id">
              {{ playbook.name }}<template v-if="playbook.isDefault"> · default</template>
            </option>
          </select>
          <p
            v-for="message in fieldErrors.playbookId"
            :key="message"
            class="text-destructive text-xs"
          >
            {{ message }}
          </p>
        </div>

        <div class="space-y-1.5">
          <label for="rule-agent" class="text-sm font-medium">As agent</label>
          <select
            id="rule-agent"
            v-model="agentId"
            class="border-input bg-background h-9 w-full rounded-md border px-2 text-sm"
            :aria-invalid="Boolean(fieldErrors.agentId)"
          >
            <option v-for="agent in assignable" :key="agent.userId" :value="agent.userId">
              {{ agent.displayName }}
            </option>
          </select>
          <p v-for="message in fieldErrors.agentId" :key="message" class="text-destructive text-xs">
            {{ message }}
          </p>
        </div>

        <label class="flex cursor-pointer items-center gap-2 text-sm">
          <input v-model="enabled" type="checkbox" />
          Enabled
        </label>

        <p v-if="error" class="text-destructive text-sm">{{ error }}</p>
      </form>

      <DialogFooter v-if="!loading && !loadFailed && playbooks.length > 0 && assignable.length > 0">
        <Button type="button" variant="ghost" :disabled="submitting" @click="emit('update:open', false)">
          Cancel
        </Button>
        <Button type="submit" form="rule-form" :disabled="submitting || !canSubmit">
          <Loader2 v-if="submitting" class="animate-spin" aria-hidden="true" />
          {{ rule ? 'Save changes' : 'Create rule' }}
        </Button>
      </DialogFooter>
    </DialogContent>
  </Dialog>
</template>
