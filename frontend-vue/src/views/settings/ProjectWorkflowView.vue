<script setup lang="ts">
import { CircleDot, Zap } from '@lucide/vue'
import { computed, ref, watch } from 'vue'

import { listRules, type Rule } from '@/api/rules'
import { listWorkflows, type Workflow } from '@/api/workflows'
import EmptyState from '@/components/common/EmptyState.vue'
import StateBadge from '@/components/common/StateBadge.vue'
import SettingsSection from '@/components/settings/SettingsSection.vue'
import UiPageState from '@/components/UiPageState.vue'
import { Tooltip, TooltipContent, TooltipProvider, TooltipTrigger } from '@/components/ui/tooltip'
import { useProjectScope } from '@/composables/useSettingsScope'
import { useToast } from '@/composables/useToast'

/**
 * A deliberately read-only first look at the project's lifecycle. Editing it
 * to states and transitions; showing the real states here lets everyone share the same
 * vocabulary immediately without implying they can edit it.
 *
 * The rule glyph is read-only for the same reason: it only ever names rules
 * already made on the Rules tab. A caller who is not a project Admin gets a 403 from
 * `/rules` and simply sees no glyphs — no rule can be a project Admin's secret from here.
 */
const project = useProjectScope()
const toast = useToast()

const workflows = ref<Workflow[]>([])
const rules = ref<Rule[]>([])
const loading = ref(true)
const failed = ref(false)

const defaultWorkflow = computed(() => workflows.value.find((workflow) => workflow.isDefault))

function rulesForState(stateId: string): Rule[] {
  return rules.value.filter((rule) => rule.triggerStateId === stateId)
}

async function load() {
  loading.value = true
  failed.value = false
  try {
    workflows.value = await listWorkflows(project.slug.value, project.projectKey.value)
  } catch (error) {
    workflows.value = []
    failed.value = true
    toast.error(error)
  } finally {
    loading.value = false
  }
  // Best-effort: a Member without the rules permission simply sees no glyphs.
  try {
    rules.value = await listRules(project.slug.value, project.projectKey.value)
  } catch {
    rules.value = []
  }
}

watch([project.slug, project.projectKey], load, { immediate: true })
</script>

<template>
  <SettingsSection
    wide
    title="Workflow"
    description="The states that describe how work moves through this project."
  >
    <UiPageState
      v-if="loading"
      state="loading"
    />
    <UiPageState
      v-else-if="failed"
      state="error"
      title="Could not load the workflow"
      description="Try again by refreshing the page."
    />
    <EmptyState
      v-else-if="!defaultWorkflow"
      title="No workflow yet"
      description="A workflow will appear when one is configured for this project."
      icon="◇"
    />
    <div v-else class="border-border overflow-hidden rounded-lg border">
      <div class="bg-muted/40 border-border flex items-center justify-between gap-3 border-b px-3 py-2.5">
        <div>
          <p class="text-sm font-medium">{{ defaultWorkflow.name }}</p>
          <p class="text-muted-foreground mt-0.5 text-xs">Default workflow</p>
        </div>
        <p class="text-muted-foreground text-xs">Editing is not available yet.</p>
      </div>
      <TooltipProvider>
        <ul class="divide-border divide-y" aria-label="Workflow states">
          <li
            v-for="state in defaultWorkflow.states"
            :key="state.id"
            class="flex items-center justify-between gap-3 px-3 py-2.5"
          >
            <div class="flex items-center gap-2">
              <StateBadge :name="state.name" :category="state.category" />
              <Tooltip v-if="rulesForState(state.id).length > 0">
                <TooltipTrigger as-child>
                  <span
                    tabindex="0"
                    role="img"
                    :aria-label="`Rules: ${rulesForState(state.id).map((rule) => rule.name).join(', ')}`"
                    class="inline-flex rounded-sm outline-none focus-visible:ring-2 focus-visible:ring-ring"
                    :data-testid="`workflow-state-rule-glyph-${state.id}`"
                  >
                    <Zap class="text-muted-foreground size-3.5" aria-hidden="true" />
                  </span>
                </TooltipTrigger>
                <TooltipContent>
                  {{ rulesForState(state.id).map((rule) => rule.name).join(', ') }}
                </TooltipContent>
              </Tooltip>
            </div>
            <span
              v-if="state.isInitial"
              class="text-muted-foreground inline-flex items-center gap-1 text-xs"
            >
              <CircleDot class="size-3" aria-hidden="true" /> Initial
            </span>
          </li>
        </ul>
      </TooltipProvider>
    </div>
  </SettingsSection>
</template>
