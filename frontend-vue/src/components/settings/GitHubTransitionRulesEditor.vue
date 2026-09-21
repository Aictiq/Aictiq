<script setup lang="ts">
import { computed, ref, watch } from 'vue'

import type { RepoBinding } from '@/api/github'
import type { WorkflowState } from '@/api/workflows'
import StateBadge from '@/components/common/StateBadge.vue'
import { Button } from '@/components/ui/button'

const props = defineProps<{ binding: RepoBinding; states: WorkflowState[]; saving?: boolean }>()
const emit = defineEmits<{
  save: [rules: Pick<RepoBinding, 'onPullRequestOpenedStateId' | 'onPullRequestMergedStateId'>]
}>()

const opened = ref<string>('')
const merged = ref<string>('')
const orderedStates = computed(() => [...props.states].sort((a, b) => a.position - b.position))

watch(
  () => props.binding,
  (binding) => {
    opened.value = binding.onPullRequestOpenedStateId ?? ''
    merged.value = binding.onPullRequestMergedStateId ?? ''
  },
  { immediate: true },
)

function save() {
  emit('save', {
    onPullRequestOpenedStateId: opened.value || null,
    onPullRequestMergedStateId: merged.value || null,
  })
}
</script>

<template>
  <form class="grid gap-3 border-t pt-3" @submit.prevent="save">
    <div class="grid gap-1.5">
      <label class="text-xs font-medium" :for="`github-opened-${binding.repoId}`"
        >When a pull request opens</label
      >
      <select
        :id="`github-opened-${binding.repoId}`"
        v-model="opened"
        class="border-input bg-background h-8 rounded border px-2 text-sm"
      >
        <option value="">Do not change item state</option>
        <option v-for="state in orderedStates" :key="state.id" :value="state.id">
          {{ state.name }}
        </option>
      </select>
    </div>
    <div class="grid gap-1.5">
      <label class="text-xs font-medium" :for="`github-merged-${binding.repoId}`"
        >When a closing pull request merges</label
      >
      <select
        :id="`github-merged-${binding.repoId}`"
        v-model="merged"
        class="border-input bg-background h-8 rounded border px-2 text-sm"
      >
        <option value="">Use the first Resolved state</option>
        <option v-for="state in orderedStates" :key="state.id" :value="state.id">
          {{ state.name }}
        </option>
      </select>
    </div>
    <div class="flex items-center justify-between gap-3">
      <div class="flex flex-wrap gap-1">
        <StateBadge
          v-for="state in orderedStates"
          :key="state.id"
          :name="state.name"
          :category="state.category"
        />
      </div>
      <Button size="sm" :disabled="saving">{{ saving ? 'Saving…' : 'Save rules' }}</Button>
    </div>
  </form>
</template>
