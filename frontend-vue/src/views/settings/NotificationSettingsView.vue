<script setup lang="ts">
import { computed, ref, onMounted } from 'vue'
import { getNotificationPreferences, putNotificationPreferences, type NotificationPreference } from '@/api/notifications'
import SettingsSection from '@/components/settings/SettingsSection.vue'
const preferences = ref<NotificationPreference[]>([])
const kinds = ['assigned', 'mentioned', 'replied', 'commented', 'transitioned', 'claimed', 'sprintStarted', 'sprintCompleted', 'wikiMentioned', 'inviteAccepted']
// Comments on watched items reach the inbox; only mentions and replies are mailed as they
// happen, so "commented" offers the digest or nothing.
const inboxOnly = new Set(['commented'])
const matrix = computed(() => kinds.map(kind => {
  const saved = preferences.value.find(p => p.kind === kind)
  const email = saved?.email ?? 'immediate'
  return { ...(saved ?? { kind, inApp: true }), email: inboxOnly.has(kind) && email === 'immediate' ? 'off' as const : email }
}))
async function save() { preferences.value = await putNotificationPreferences(preferences.value) }
onMounted(async () => { preferences.value = await getNotificationPreferences() })
</script>

<template>
  <SettingsSection title="Notifications" description="Choose where Aictiq sends work updates.">
    <div v-for="preference in matrix" :key="preference.kind" class="mt-3 flex items-center justify-between border-b pb-3 text-sm"><span class="capitalize">{{ preference.kind }}</span><span class="flex gap-3"><label><input v-model="preference.inApp" type="checkbox" @change="preferences = matrix" /> Inbox</label><label>Email <select v-model="preference.email" class="ml-1 border rounded px-1" @change="preferences = matrix"><option value="off">Off</option><option v-if="!inboxOnly.has(preference.kind)" value="immediate">Immediate</option><option value="digest">Daily digest</option></select></label></span></div>
    <button class="mt-4 rounded border px-3 py-2 text-sm" @click="save">Save preferences</button>
  </SettingsSection>
</template>
