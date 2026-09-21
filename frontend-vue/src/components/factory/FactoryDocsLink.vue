<script setup lang="ts">
import { ExternalLink } from '@lucide/vue'

import { Button } from '@/components/ui/button'
import { factoryDocsUrl } from '@/lib/factory'

withDefaults(defineProps<{ label?: string }>(), {
  label: 'Read the Factory guide',
})
</script>

<template>
  <Button
    as="a"
    :href="factoryDocsUrl"
    target="_blank"
    rel="noopener noreferrer"
    variant="secondary"
    size="sm"
  >
    {{ label }}
    <ExternalLink class="size-3.5" aria-hidden="true" />
  </Button>
</template>
