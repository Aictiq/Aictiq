<script setup lang="ts">
import { computed, provide, ref, watch } from 'vue'
import { RouterView, useRoute } from 'vue-router'

import { getProject, type Project } from '@/api/projects'
import EmptyState from '@/components/common/EmptyState.vue'
import SettingsShell from '@/components/settings/SettingsShell.vue'
import UiPageState from '@/components/UiPageState.vue'
import { Button } from '@/components/ui/button'
import { projectScopeKey, type ProjectScope } from '@/composables/useSettingsScope'
import { useToast } from '@/composables/useToast'
import { projectSettingsLinks } from '@/router/paths'
import { useProjectsStore } from '@/stores/projects'
import { ApiError } from '@/utils/api'

/**
 * The project settings hub, and the twin of the organization one - same single fetch,
 * same 404-not-403 rule. A private project someone is not on is simply not found, which
 * is the answer its URL gives too.
 */
const route = useRoute()
const projects = useProjectsStore()
const toast = useToast()

const slug = computed(() => String(route.params.slug ?? ''))
const projectKey = computed(() => String(route.params.projectKey ?? ''))

const record = ref<Project | null>(null)
const loading = ref(true)
const notFound = ref(false)

async function reload() {
  if (!slug.value || !projectKey.value) return

  loading.value = true
  notFound.value = false
  try {
    record.value = await getProject(slug.value, projectKey.value)
    projects.replace(record.value)
  } catch (error) {
    record.value = null
    if (error instanceof ApiError && error.status === 404) {
      notFound.value = true
      projects.remove(projectKey.value)
    } else {
      toast.error(error)
    }
  } finally {
    loading.value = false
  }
}

watch([slug, projectKey], reload, { immediate: true })

const scope: ProjectScope = {
  slug,
  projectKey,
  record,
  loading,
  notFound,
  reload,
  set: (updated) => {
    record.value = updated
    projects.replace(updated)
  },
}

provide(projectScopeKey, scope)

const links = computed(() => projectSettingsLinks(slug.value, projectKey.value))
</script>

<template>
  <SettingsShell
    :eyebrow="`Project · ${projectKey}`"
    :title="record?.name ?? projectKey"
    :description="record?.description ?? undefined"
    :links="links"
  >
    <p
      v-if="record?.isArchived"
      class="border-border text-muted-foreground mb-5 rounded-lg border border-dashed px-3 py-2 text-xs"
    >
      This project is archived. Everything here still reads; nothing here can be changed
      until it is restored under General.
    </p>

    <UiPageState v-if="loading" state="loading" />

    <EmptyState
      v-else-if="notFound"
      title="Project not found"
      description="It does not exist, or it is private and you are not on it. Those look the same from here on purpose."
      icon="◇"
    >
      <Button variant="secondary" @click="$router.push('/projects')">All projects</Button>
    </EmptyState>

    <RouterView v-else-if="record" />
  </SettingsShell>
</template>
