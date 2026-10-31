<script setup lang="ts">
import { Plus, Trash2 } from '@lucide/vue'
import { computed } from 'vue'

import type { RepositoryStep } from '@/api/playbooks'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { multiRepoDocsUrl, workingDirectoryProblem } from '@/lib/repositories'

/**
 * A playbook's repository steps: which of the project's repositories its runs work in, where
 * the agent starts in each, and which steps wait for others. Steps nothing waits on run together.
 */
const props = defineProps<{
  /** The project's aliases, the primary's first. */
  aliases: string[]
  errors: string[]
}>()

const steps = defineModel<RepositoryStep[]>({ required: true })

const unused = computed(() =>
  props.aliases.filter((alias) => !steps.value.some((step) => step.alias === alias)),
)

function add() {
  const alias = unused.value[0]
  if (!alias) return
  steps.value = [...steps.value, { alias, workingDirectory: null, after: [], waitForMerge: false }]
}

function remove(index: number) {
  const gone = steps.value[index]!.alias
  steps.value = steps.value
    .filter((_, position) => position !== index)
    .map((step) => ({ ...step, after: step.after.filter((alias) => alias !== gone) }))
}

function toggleAfter(step: RepositoryStep, alias: string, on: boolean) {
  step.after = on ? [...step.after, alias] : step.after.filter((entry) => entry !== alias)
  if (step.after.length === 0) step.waitForMerge = false
}

function choices(step: RepositoryStep) {
  return props.aliases.filter(
    (alias) => alias === step.alias || !steps.value.some((other) => other.alias === alias),
  )
}
</script>

<template>
  <div class="space-y-2" data-testid="playbook-repository-steps">
    <span class="text-sm font-medium">Repositories</span>
    <p class="text-muted-foreground text-xs">
      The repositories this playbook's runs work in when the ticket names none (<code
        >repo:&lt;alias&gt;</code
      >
      tags). Leave empty for the primary repository only.
      <a
        :href="multiRepoDocsUrl"
        class="text-primary hover:underline"
        target="_blank"
        rel="noopener"
        >Docs</a
      >
    </p>
    <div
      v-for="(step, index) in steps"
      :key="index"
      class="border-border space-y-2 rounded-md border p-2.5"
      data-testid="playbook-repository-step"
    >
      <div class="grid gap-2 sm:grid-cols-2">
        <div class="space-y-1">
          <label :for="`repository-step-alias-${index}`" class="text-xs font-medium"
            >Repository</label
          >
          <select
            :id="`repository-step-alias-${index}`"
            v-model="step.alias"
            class="border-input bg-background h-9 w-full rounded-md border px-2 text-sm"
          >
            <option v-for="alias in choices(step)" :key="alias" :value="alias">{{ alias }}</option>
          </select>
        </div>
        <div class="space-y-1">
          <label :for="`repository-step-directory-${index}`" class="text-xs font-medium"
            >Working directory</label
          >
          <Input
            :id="`repository-step-directory-${index}`"
            :model-value="step.workingDirectory ?? ''"
            placeholder="Repository default"
            @update:model-value="step.workingDirectory = String($event) || null"
          />
          <p
            v-if="workingDirectoryProblem(step.workingDirectory ?? '')"
            class="text-destructive text-xs"
          >
            {{ workingDirectoryProblem(step.workingDirectory ?? '') }}
          </p>
        </div>
      </div>
      <div v-if="steps.length > 1" class="space-y-1">
        <span class="text-xs font-medium">Starts after</span>
        <div class="flex flex-wrap gap-3">
          <label
            v-for="other in steps.filter((entry) => entry.alias !== step.alias)"
            :key="other.alias"
            class="flex items-center gap-1.5 text-xs"
          >
            <input
              type="checkbox"
              :checked="step.after.includes(other.alias)"
              @change="toggleAfter(step, other.alias, ($event.target as HTMLInputElement).checked)"
            />
            {{ other.alias }}
          </label>
        </div>
        <label v-if="step.after.length > 0" class="flex items-center gap-1.5 text-xs">
          <input v-model="step.waitForMerge" type="checkbox" />
          Wait for their pull requests to merge, not only for their runs to finish
        </label>
      </div>
      <div class="flex justify-end">
        <Button
          type="button"
          variant="ghost"
          size="sm"
          :aria-label="`Remove ${step.alias}`"
          @click="remove(index)"
        >
          <Trash2 class="size-4" aria-hidden="true" />
          Remove
        </Button>
      </div>
    </div>
    <Button
      v-if="unused.length > 0"
      type="button"
      variant="secondary"
      size="sm"
      data-testid="playbook-repository-add"
      @click="add"
    >
      <Plus class="size-4" aria-hidden="true" />
      Add repository
    </Button>
    <p v-for="message in errors" :key="message" class="text-destructive text-xs">
      {{ message }}
    </p>
  </div>
</template>
