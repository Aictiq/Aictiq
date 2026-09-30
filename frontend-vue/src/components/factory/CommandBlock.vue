<script setup lang="ts">
import { Check, Copy } from '@lucide/vue'
import { ref } from 'vue'

import { Button } from '@/components/ui/button'
import { useToast } from '@/composables/useToast'

/** One or more shell lines to paste on the runner machine, with a copy button. */
const props = defineProps<{ command: string; label: string }>()

const toast = useToast()
const copied = ref(false)

async function copy() {
  try {
    await navigator.clipboard.writeText(props.command)
    copied.value = true
    setTimeout(() => (copied.value = false), 1600)
  } catch {
    toast.error(new Error('Could not copy - select the text and copy it manually.'))
  }
}
</script>

<template>
  <div
    class="bg-muted/50 border-border group mt-1.5 flex items-start gap-2 rounded-md border py-1 pr-1 pl-2.5"
  >
    <pre
      class="text-foreground min-w-0 flex-1 py-1 font-mono text-[11px] break-all whitespace-pre-wrap"
      data-testid="setup-command"
    ><span v-for="(line, i) in command.split('\n')" :key="i" class="block"><span class="text-muted-foreground select-none">$ </span>{{ line }}</span></pre>
    <Button variant="ghost" size="icon" class="size-7" :aria-label="`Copy: ${label}`" @click="copy">
      <Check v-if="copied" class="text-success size-3.5" aria-hidden="true" />
      <Copy v-else class="size-3.5" aria-hidden="true" />
    </Button>
  </div>
</template>
