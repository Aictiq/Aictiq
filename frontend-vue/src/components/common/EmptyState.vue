<script setup lang="ts">
defineProps<{ title: string; description?: string; icon?: string }>()
</script>

<template>
  <div class="flex flex-col items-center justify-center gap-2 px-6 py-14 text-center">
    <div v-if="icon" class="text-muted-foreground/60 text-2xl" aria-hidden="true">{{ icon }}</div>
    <p class="text-sm font-medium">{{ title }}</p>
    <p v-if="description" class="text-muted-foreground max-w-sm text-xs">{{ description }}</p>
    <!-- The call to action belongs to the caller: an empty list and an empty search
         result look the same but want different next steps. -->
    <div v-if="$slots.default" class="mt-2"><slot /></div>
  </div>
</template>
