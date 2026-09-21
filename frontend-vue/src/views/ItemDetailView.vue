<script setup lang="ts">
import { useRouter } from 'vue-router'

import ItemDetail from '@/components/items/ItemDetail.vue'
import AppShell from '@/components/shell/AppShell.vue'

defineProps<{ slug: string; projectKey: string; itemKey: string }>()
const router = useRouter()
</script>

<template>
  <AppShell>
    <ItemDetail :slug="slug" :project-key="projectKey" :item-key="itemKey" @close="router.back()" />
  </AppShell>
</template>
