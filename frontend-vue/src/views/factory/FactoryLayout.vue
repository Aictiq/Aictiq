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
import { factoryLinks } from '@/router/paths'
import { useOrganizationsStore } from '@/stores/organizations'
import { ApiError } from '@/utils/api'

/**
 * The AI software factory: where an organization's runners, runs and playbooks live
 * (phase 10). It is a top-level area rather than a settings tab because this is where work
 * happens; agent identities and their tokens stay in organization settings.
 *
 * It shares the settings hubs' frame and their one rule about a pasted link: an organization
 * you cannot see renders "not found", never a hint about whether it exists. The tabs read the
 * organization (and the caller's role in it) through the same scope the settings tabs use.
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
  } catch (error) {
    record.value = null
    if (error instanceof ApiError && error.status === 404) {
      notFound.value = true
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

const links = computed(() => factoryLinks(slug.value))
</script>

<template>
  <SettingsShell
    eyebrow="Factory"
    :title="record?.name ?? slug"
    :description="
      record
        ? 'The machines that run your agents, what they are running, and the playbooks they follow.'
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

    <!-- A stakeholder never sees the factory. The sidebar hides the way in; this
         is what a pasted link gets, and the API refuses the work itself regardless. -->
    <EmptyState
      v-else-if="record && !record.canOperateFactory"
      title="The factory is not available to you"
      description="An administrator decides who may start and watch AI work in this organization."
      icon="◇"
    >
      <Button variant="secondary" @click="$router.push('/')">Back to my work</Button>
    </EmptyState>

    <RouterView v-else-if="record" />
  </SettingsShell>
</template>
