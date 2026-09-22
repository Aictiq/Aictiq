<script setup lang="ts">
import { computed } from 'vue'

const props = defineProps<{ snippet: string }>()

interface Segment {
  text: string
  marked: boolean
}

// The API escapes source text before it adds only these two markers. Parsing the markers
// ourselves keeps the app-wide rule intact: raw user content never reaches v-html.
function decode(text: string): string {
  const document = new DOMParser().parseFromString(text, 'text/html')
  return document.documentElement.textContent ?? ''
}

const segments = computed<Segment[]>(() => {
  const parts = props.snippet.split(/(<mark>|<\/mark>)/)
  let marked = false
  return parts.flatMap((part) => {
    if (part === '<mark>') { marked = true; return [] }
    if (part === '</mark>') { marked = false; return [] }
    return part ? [{ text: decode(part), marked }] : []
  })
})
</script>

<template>
  <span>
    <template v-for="(segment, index) in segments" :key="index">
      <mark v-if="segment.marked" class="bg-primary/20 text-foreground rounded-sm px-px">{{ segment.text }}</mark>
      <template v-else>{{ segment.text }}</template>
    </template>
  </span>
</template>
