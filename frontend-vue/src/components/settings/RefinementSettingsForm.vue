<script setup lang="ts">
import { Loader2, Sparkles } from '@lucide/vue'
import { computed, ref, watch } from 'vue'

import type { Agent } from '@/api/agents'
import { listPlaybooks, type Playbook } from '@/api/playbooks'
import {
  createRefinePlaybook,
  getRefinementSettings,
  saveRefinementSettings,
  type RefinementSettings,
} from '@/api/refinement'
import { listWorkflows, type WorkflowState } from '@/api/workflows'
import UiPageState from '@/components/UiPageState.vue'
import { Button } from '@/components/ui/button'
import { useToast } from '@/composables/useToast'
import { ConflictError } from '@/utils/api'

/**
 * How this project refines tickets: the playbook and agent a refine run uses, what the agent
 * should know about the product, and where a confirmed ticket goes. The four context fields
 * are put in front of every refine run's prompt, so each project's tickets follow its own
 * conventions.
 */
const props = defineProps<{
  slug: string
  projectKey: string
  /** Active agents that can open this project. */
  agents: Agent[]
  mayManage: boolean
}>()

const emit = defineEmits<{ loaded: [] }>()
const toast = useToast()
const loading = ref(true)
const failed = ref(false)
const saving = ref(false)
const creatingPlaybook = ref(false)
const settings = ref<RefinementSettings | null>(null)
const playbooks = ref<Playbook[]>([])
const states = ref<WorkflowState[]>([])

const playbookId = ref('')
const agentId = ref('')
const productDescription = ref('')
const writingInstructions = ref('')
const namingConventions = ref('')
const platforms = ref('')
const refinedStateId = ref('')

const hasRefinePlaybook = computed(() =>
  playbooks.value.some((playbook) => playbook.name.toLowerCase() === 'refine'),
)

function apply(value: RefinementSettings) {
  settings.value = value
  playbookId.value = value.playbookId ?? ''
  agentId.value = value.agentId ?? ''
  productDescription.value = value.productDescription
  writingInstructions.value = value.writingInstructions
  namingConventions.value = value.namingConventions
  platforms.value = value.platforms
  refinedStateId.value = value.refinedStateId ?? ''
}

async function load() {
  loading.value = true
  failed.value = false
  try {
    const [loaded, projectPlaybooks, workflows] = await Promise.all([
      getRefinementSettings(props.slug, props.projectKey),
      listPlaybooks(props.slug, props.projectKey),
      listWorkflows(props.slug, props.projectKey),
    ])
    playbooks.value = projectPlaybooks
    states.value = workflows.flatMap((workflow) => workflow.states)
    apply(loaded)
  } catch {
    failed.value = true
  } finally {
    loading.value = false
    emit('loaded')
  }
}

watch(() => [props.slug, props.projectKey], load, { immediate: true })

async function save() {
  if (!settings.value || !props.mayManage) return
  saving.value = true
  try {
    apply(
      await saveRefinementSettings(props.slug, props.projectKey, {
        playbookId: playbookId.value || null,
        agentId: agentId.value || null,
        productDescription: productDescription.value,
        writingInstructions: writingInstructions.value,
        namingConventions: namingConventions.value,
        platforms: platforms.value,
        refinedStateId: refinedStateId.value || null,
        version: settings.value.version,
      }),
    )
    toast.saved('Refinement settings saved.')
  } catch (error) {
    if (error instanceof ConflictError) {
      toast.error(new Error('Someone changed these settings first. The latest values are shown.'))
      await load()
    } else {
      toast.error(error)
    }
  } finally {
    saving.value = false
  }
}

async function createPlaybook() {
  creatingPlaybook.value = true
  try {
    await createRefinePlaybook(props.slug, props.projectKey)
    toast.saved('Refine playbook created.')
    await load()
  } catch (error) {
    toast.error(error)
  } finally {
    creatingPlaybook.value = false
  }
}
</script>

<template>
  <UiPageState v-if="loading" state="loading" />
  <UiPageState
    v-else-if="failed"
    state="error"
    title="Could not load refinement settings"
    description="Try the request again."
  >
    <Button variant="secondary" @click="load">Try again</Button>
  </UiPageState>
  <form
    v-else-if="settings"
    class="space-y-5"
    data-testid="refinement-settings"
    @submit.prevent="save"
  >
    <div class="space-y-1.5">
      <label for="refinement-playbook" class="text-sm font-medium">Refine playbook</label>
      <div class="flex gap-2">
        <select
          id="refinement-playbook"
          v-model="playbookId"
          class="border-input bg-background h-9 min-w-0 flex-1 rounded-md border px-2 text-sm disabled:opacity-50"
          :disabled="!mayManage"
        >
          <option value="">Off - no ticket refinement</option>
          <option v-for="playbook in playbooks" :key="playbook.id" :value="playbook.id">
            {{ playbook.name }}
          </option>
        </select>
        <Button
          v-if="mayManage && !hasRefinePlaybook"
          type="button"
          variant="outline"
          data-testid="refinement-create-playbook"
          :disabled="creatingPlaybook"
          @click="createPlaybook"
        >
          <Loader2 v-if="creatingPlaybook" class="animate-spin" aria-hidden="true" />
          <Sparkles v-else aria-hidden="true" />
          Create Refine playbook
        </Button>
      </div>
      <p class="text-muted-foreground text-xs">
        Choosing one adds <strong>Refine ticket</strong> to ticket details. Its instructions say how
        each item type is written up; a refine run reads the code but never changes it.
      </p>
    </div>

    <div class="space-y-1.5">
      <label for="refinement-agent" class="text-sm font-medium">Agent</label>
      <select
        id="refinement-agent"
        v-model="agentId"
        class="border-input bg-background h-9 w-full rounded-md border px-2 text-sm disabled:opacity-50"
        :disabled="!mayManage"
      >
        <option value="">The project's default agent</option>
        <option v-for="agent in agents" :key="agent.userId" :value="agent.userId">
          {{ agent.displayName }}
        </option>
      </select>
    </div>

    <div class="space-y-1.5">
      <label for="refinement-state" class="text-sm font-medium">Refined tickets go to</label>
      <select
        id="refinement-state"
        v-model="refinedStateId"
        class="border-input bg-background h-9 w-full rounded-md border px-2 text-sm disabled:opacity-50"
        :disabled="!mayManage"
      >
        <option value="">Stay where they were created</option>
        <option v-for="state in states" :key="state.id" :value="state.id">{{ state.name }}</option>
      </select>
      <p class="text-muted-foreground text-xs">
        The state, and so the board column, a ticket moves to when someone confirms it.
      </p>
    </div>

    <div class="space-y-1.5">
      <label for="refinement-product" class="text-sm font-medium">Product description</label>
      <textarea
        id="refinement-product"
        v-model="productDescription"
        rows="3"
        maxlength="8000"
        class="border-input bg-background w-full rounded-md border px-2 py-1.5 text-sm disabled:opacity-50"
        :disabled="!mayManage"
        placeholder="What the product is, who uses it, and the parts a ticket usually touches."
      />
    </div>
    <div class="space-y-1.5">
      <label for="refinement-instructions" class="text-sm font-medium">
        Ticket-writing instructions
      </label>
      <textarea
        id="refinement-instructions"
        v-model="writingInstructions"
        rows="3"
        maxlength="8000"
        class="border-input bg-background w-full rounded-md border px-2 py-1.5 text-sm disabled:opacity-50"
        :disabled="!mayManage"
        placeholder="Tone, required sections, how acceptance criteria are phrased…"
      />
    </div>
    <div class="space-y-1.5">
      <label for="refinement-naming" class="text-sm font-medium">Naming conventions</label>
      <textarea
        id="refinement-naming"
        v-model="namingConventions"
        rows="2"
        maxlength="8000"
        class="border-input bg-background w-full rounded-md border px-2 py-1.5 text-sm disabled:opacity-50"
        :disabled="!mayManage"
        placeholder="[DS - …] for design system work, [IDA - …] for the identity app…"
      />
    </div>
    <div class="space-y-1.5">
      <label for="refinement-platforms" class="text-sm font-medium">
        Supported platforms and devices
      </label>
      <textarea
        id="refinement-platforms"
        v-model="platforms"
        rows="2"
        maxlength="8000"
        class="border-input bg-background w-full rounded-md border px-2 py-1.5 text-sm disabled:opacity-50"
        :disabled="!mayManage"
        placeholder="Web (Chrome, Safari), iOS 17+, Android 12+…"
      />
    </div>

    <div v-if="mayManage" class="flex justify-end">
      <Button type="submit" :disabled="saving" data-testid="refinement-settings-save">
        <Loader2 v-if="saving" class="animate-spin" aria-hidden="true" />
        Save refinement settings
      </Button>
    </div>
  </form>
</template>
