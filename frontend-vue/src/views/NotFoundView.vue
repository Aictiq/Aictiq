<script setup lang="ts">
import { RouterLink } from 'vue-router'
</script>

<template>
  <main class="grid min-h-screen place-items-center px-6">
    <div class="space-y-2 text-center">
      <p class="text-muted-foreground font-mono text-sm">404</p>
      <h1 class="text-2xl font-semibold tracking-tight">Page not found</h1>
      <RouterLink to="/" class="text-sm underline underline-offset-4">Go home</RouterLink>
    </div>
  </main>
</template>
