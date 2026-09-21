<script setup lang="ts">
import { computed, provide, ref, watch } from 'vue'
import { RouterView, useRoute } from 'vue-router'

import { getOrganization, type Organization } from '@/api/organizations'
import EmptyState from '@/components/common/EmptyState.vue'
import SettingsShell from '@/components/settings/SettingsShell.vue'
import UiPageState from '@/components/UiPageState.vue'
import { Button } from '@/components/ui/button'
import { orgScopeKey, type OrgScope } from '@/composables/useSettingsScope'
import { useToast } from '@/composables/useToast'
import { orgSettingsLinks } from '@/router/paths'
import { useOrganizationsStore } from '@/stores/organizations'
import { ApiError } from '@/utils/api'

/**
 * The organization settings hub. It owns the one fetch its tabs share and answers the
 * question a pasted link raises before anything else: is this organization one you can
 * see at all.
 *
 * A 404 renders "not found" rather than an error, and never says whether the slug exists
 * — that is the API's own rule (404 for what you cannot see) carried into the UI, and the
 * page would undo it by explaining the difference.
 */
const route = useRoute()
const organizations = useOrganizationsStore()
const toast = useToast()

const slug = computed(() => String(route.params.slug ?? ''))

const record = ref<Organization | null>(null)
const loading = ref(true)
const notFound = ref(false)

async function reload() {
  if (!slug.value) return

  loading.value = true
  notFound.value = false
  try {
    record.value = await getOrganization(slug.value)
    // The switcher's copy of the name and role comes from a list call that may predate a
    // rename made in another tab; this is the fresher one.
    organizations.replace({
      id: record.value.id,
      slug: record.value.slug,
      name: record.value.name,
      role: record.value.role,
      canOperateFactory: record.value.canOperateFactory,
    })
  } catch (error) {
    record.value = null
    if (error instanceof ApiError && error.status === 404) {
      notFound.value = true
      // The membership is gone or never existed. Drop it from the switcher rather than
      // leaving a row that answers 404 every time it is clicked.
      organizations.remove(slug.value)
    } else {
      toast.error(error)
    }
  } finally {
    loading.value = false
  }
}

watch(slug, reload, { immediate: true })

const scope: OrgScope = {
  slug,
  record,
  loading,
  notFound,
  reload,
  set: (updated) => {
    record.value = updated
    organizations.replace({
      id: updated.id,
      slug: updated.slug,
      name: updated.name,
      role: updated.role,
      canOperateFactory: updated.canOperateFactory,
    })
  },
}

provide(orgScopeKey, scope)

const links = computed(() => orgSettingsLinks(slug.value))
</script>

<template>
  <SettingsShell
    eyebrow="Organization"
    :title="record?.name ?? slug"
    :description="
      record
        ? `Everything this organization shares: its people, its agents and its address.`
        : undefined
    "
    :links="links"
  >
    <UiPageState v-if="loading" state="loading" />

    <EmptyState
      v-else-if="notFound"
      title="Organization not found"
      description="It does not exist, or you are not a member of it. Those look the same from here on purpose."
      icon="◇"
    >
      <Button variant="secondary" @click="$router.push('/')">Back to my work</Button>
    </EmptyState>

    <RouterView v-else-if="record" />
  </SettingsShell>
</template>
