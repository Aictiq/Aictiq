<script setup lang="ts">
import { computed } from 'vue'
import { useRouter } from 'vue-router'

import { useResolvedItemKeys } from '@/composables/useResolvedItemKeys'
import { renderMarkdown } from '@/lib/markdown'

/**
 * Renders user-written Markdown. `v-html` is safe here and only here because
 * `renderMarkdown` sanitises - never bind unsanitised content this way.
 */
const props = defineProps<{ source: string; slug?: string; projectKey?: string }>()
const router = useRouter()
const itemKeys = useResolvedItemKeys(() => props.source, () => props.slug, () => props.projectKey)

const html = computed(() => renderMarkdown(props.source, props.slug && props.projectKey ? {
  slug: props.slug,
  projectKey: props.projectKey,
  itemKeys: itemKeys.value,
} : undefined))

function navigate(event: MouseEvent) {
  if (event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return
  const link = (event.target as Element).closest('a')
  const href = link?.getAttribute('href')
  if (!href?.startsWith('/o/')) return
  event.preventDefault()
  void router.push(href)
}
</script>

<template>
  <!--
    The one sanctioned v-html in the app: `renderMarkdown` refuses raw HTML at the parser
    and runs the output through DOMPurify. Anywhere else, this rule is right.
  -->
  <!-- eslint-disable-next-line vue/no-v-html -->
  <div class="aictiq-markdown text-sm" @click="navigate" v-html="html" />
</template>
