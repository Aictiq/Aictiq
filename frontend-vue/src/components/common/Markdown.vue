<script setup lang="ts">
import { computed } from 'vue'

import { renderMarkdown } from '@/lib/markdown'

/**
 * Renders user-written Markdown. `v-html` is safe here and only here because
 * `renderMarkdown` sanitises - never bind unsanitised content this way.
 */
const props = defineProps<{ source: string }>()

const html = computed(() => renderMarkdown(props.source))
</script>

<template>
  <!--
    The one sanctioned v-html in the app: `renderMarkdown` refuses raw HTML at the parser
    and runs the output through DOMPurify. Anywhere else, this rule is right.
  -->
  <!-- eslint-disable-next-line vue/no-v-html -->
  <div class="aictiq-markdown text-sm" v-html="html" />
</template>
