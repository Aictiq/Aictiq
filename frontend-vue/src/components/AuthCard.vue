<script setup lang="ts">
defineProps<{ title: string; description?: string }>()
</script>

<template>
  <main class="grid min-h-screen place-items-center px-6 py-12">
    <div class="w-full max-w-sm space-y-6">
      <header class="space-y-1.5 text-center">
        <h1 class="text-2xl font-semibold tracking-tight">{{ title }}</h1>
        <p v-if="description" class="text-muted-foreground text-sm">{{ description }}</p>
      </header>

      <slot />

      <p v-if="$slots.footer" class="text-muted-foreground text-center text-sm">
        <slot name="footer" />
      </p>
    </div>
  </main>
</template>
