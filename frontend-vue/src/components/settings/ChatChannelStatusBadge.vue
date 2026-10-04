<script setup lang="ts">
import type { ChatChannelStatus } from '@/api/notifications'
import { Badge } from '@/components/ui/badge'

/** A chat channel's state in one word: waiting for its code, delivering, or failing. */
defineProps<{ status: ChatChannelStatus }>()
</script>

<template>
  <Badge v-if="status === 'active'" variant="secondary">Active</Badge>
  <Badge v-else-if="status === 'pending'" variant="outline">Pending</Badge>
  <Badge v-else variant="destructive">Broken</Badge>
</template>
