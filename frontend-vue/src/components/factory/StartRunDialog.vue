<script setup lang="ts">
import { Loader2 } from '@lucide/vue'
import { computed, ref, watch } from 'vue'

import type { Run } from '@/api/runs'
import { dispatchRun } from '@/api/runs'
import { listAgents, type Agent } from '@/api/agents'
import { getFactorySettings, listPlaybooks, type Playbook } from '@/api/playbooks'
import { listProjectMembers } from '@/api/projects'
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
import { useToast } from '@/composables/useToast'
import { orgSettingsPath, projectSettingsPath } from '@/router/paths'
import { readRunChoice, writeRunChoice } from '@/lib/runs'
import { ApiError } from '@/utils/api'

/**
 * "Hand to agent" in two clicks: a playbook and an agent, both preselected from what this
 * project used last (remembered per project, because a team runs the same recipe on item
 * after item). Dispatching claims the item for the agent in the same transaction, so a
 * claim that got there first is the server's 409, shown here rather than guessed at.
 */
const props = defineProps<{
  slug: string
  projectKey: string
  itemKey: string
  open: boolean
}>()

const emit = defineEmits<{
  'update:open': [open: boolean]
  dispatched: [run: Run]
}>()

const toast = useToast()

const loading = ref(false)
const loadFailed = ref(false)
const playbooks = ref<Playbook[]>([])
const agents = ref<Agent[]>([])
const playbookId = ref<string | null>(null)
const agentId = ref<string | null>(null)
const submitting = ref(false)
const error = ref<string | null>(null)
const fieldErrors = ref<Record<string, string[]>>({})

const assignable = computed(() =>
  agents.value.filter((agent) => agent.isActive).sort((a, b) => a.displayName.localeCompare(b.displayName)),
)

watch(
  () => props.open,
  async (open) => {
    if (!open) return
    error.value = null
    fieldErrors.value = {}
    loadFailed.value = false
    loading.value = true
    try {
      const [projectPlaybooks, projectAgents, settings, members] = await Promise.all([
        listPlaybooks(props.slug, props.projectKey),
        listAgents(props.slug),
        getFactorySettings(props.slug, props.projectKey).catch(() => null),
        listProjectMembers(props.slug, props.projectKey),
      ])
      playbooks.value = projectPlaybooks
      const memberIds = new Set(members.map((member) => member.userId))
      agents.value = projectAgents.filter((agent) => memberIds.has(agent.userId))

      // The choice this project used last, if it still names things that exist.
      const remembered = readRunChoice(props.projectKey)
      playbookId.value =
        remembered?.playbookId && projectPlaybooks.some((p) => p.id === remembered.playbookId)
          ? remembered.playbookId
          : (projectPlaybooks.find((p) => p.isDefault)?.id ?? projectPlaybooks[0]?.id ?? null)
      const defaultAgent =
        settings?.defaultAgentId && assignable.value.some((a) => a.userId === settings.defaultAgentId)
          ? settings.defaultAgentId
          : null
      agentId.value =
        remembered?.agentId && assignable.value.some((a) => a.userId === remembered.agentId)
          ? remembered.agentId
          : (defaultAgent ?? assignable.value[0]?.userId ?? null)
    } catch {
      loadFailed.value = true
    } finally {
      loading.value = false
    }
  },
  { immediate: true },
)

async function submit() {
  if (submitting.value || !playbookId.value || !agentId.value) return
  submitting.value = true
  error.value = null
  fieldErrors.value = {}
  try {
    const run = await dispatchRun(props.slug, props.itemKey, {
      playbookId: playbookId.value,
      agentId: agentId.value,
    })
    writeRunChoice(props.projectKey, { playbookId: playbookId.value, agentId: agentId.value })
    toast.success(
      'Run queued',
      `It starts when a runner picks it up - follow it from the item's Runs section.`,
    )
    emit('update:open', false)
    emit('dispatched', run)
  } catch (caught) {
    if (caught instanceof ApiError && Object.keys(caught.fieldErrors).length > 0) {
      fieldErrors.value = caught.fieldErrors
    } else if (caught instanceof ApiError && caught.problem?.type?.endsWith('item-claimed')) {
      error.value = 'This item already has a live claim or run.'
    } else if (caught instanceof ApiError && caught.status === 404) {
      error.value = 'The playbook’s page is gone or cannot be read, so there is nothing to run.'
    } else if (caught instanceof ApiError) {
      error.value = caught.problem?.detail ?? caught.title
    } else {
      error.value = 'Starting the run failed.'
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
        <DialogTitle>Hand {{ itemKey }} to an agent</DialogTitle>
        <DialogDescription>
          The agent claims the item, follows the playbook on a runner, and reports back here
          with its pull request. It starts as soon as a runner is free.
        </DialogDescription>
      </DialogHeader>

      <UiPageState v-if="loading" state="loading" />

      <EmptyState
        v-else-if="loadFailed"
        title="Could not load the factory settings"
        description="The playbooks and agents for this project could not be read. Try again in a moment."
        icon="◇"
      />

      <EmptyState
        v-else-if="playbooks.length === 0"
        title="No playbook yet"
        description="A playbook says what the agent should do: its page on the wiki, its harness, and where success and failure leave the item."
        icon="◇"
      >
        <Button
          data-testid="start-run-create-playbook"
          @click="$router.push(projectSettingsPath(props.slug, props.projectKey, 'factory'))"
        >
          Create a playbook
        </Button>
      </EmptyState>

      <EmptyState
        v-else-if="assignable.length === 0"
        title="No agent on this project"
        description="An agent is an account a person owns, added to this project like any member. Add one in organization settings, then start the run."
        icon="◇"
      >
        <Button @click="$router.push(orgSettingsPath(props.slug, 'agents'))">Manage agents</Button>
      </EmptyState>

      <form v-else id="start-run" class="space-y-4" novalidate @submit.prevent="submit">
        <div class="space-y-1.5">
          <label for="start-run-playbook" class="text-sm font-medium">Playbook</label>
          <select
            id="start-run-playbook"
            v-model="playbookId"
            class="border-input bg-background w-full rounded border px-2 py-1.5 text-sm"
          >
            <option v-for="playbook in playbooks" :key="playbook.id" :value="playbook.id">
              {{ playbook.name }}<template v-if="playbook.isDefault"> · default</template>
            </option>
          </select>
        </div>
        <div class="space-y-1.5">
          <label for="start-run-agent" class="text-sm font-medium">Agent</label>
          <select
            id="start-run-agent"
            v-model="agentId"
            class="border-input bg-background w-full rounded border px-2 py-1.5 text-sm"
          >
            <option v-for="agent in assignable" :key="agent.userId" :value="agent.userId">
              {{ agent.displayName }}
            </option>
          </select>
          <p v-for="message in fieldErrors.agentId" :key="message" class="text-destructive text-xs">
            {{ message }}
          </p>
        </div>
        <p v-if="error" class="text-destructive text-sm">{{ error }}</p>
      </form>

      <DialogFooter v-if="!loading && !loadFailed && playbooks.length > 0 && assignable.length > 0">
        <Button
          type="button"
          variant="ghost"
          :disabled="submitting"
          @click="emit('update:open', false)"
        >
          Cancel
        </Button>
        <Button type="submit" form="start-run" :disabled="submitting || !playbookId || !agentId">
          <Loader2 v-if="submitting" class="animate-spin" aria-hidden="true" />
          Hand to agent
        </Button>
      </DialogFooter>
    </DialogContent>
  </Dialog>
</template>
