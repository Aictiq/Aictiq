<script setup lang="ts">
import { Archive, Settings2 } from '@lucide/vue'
import { computed, onMounted, ref, watch } from 'vue'
import { RouterLink, useRouter } from 'vue-router'

import { getOrganization } from '@/api/organizations'
import { canCreateProjects, listProjects, type Project } from '@/api/projects'
import EmptyState from '@/components/common/EmptyState.vue'
import ProjectBadge from '@/components/common/ProjectBadge.vue'
import AppShell from '@/components/shell/AppShell.vue'
import CreateProjectDialog from '@/components/shell/CreateProjectDialog.vue'
import UiPageState from '@/components/UiPageState.vue'
import { Button } from '@/components/ui/button'
import { useToast } from '@/composables/useToast'
import { projectItemsPath, projectSettingsPath } from '@/router/paths'
import { useOrganizationsStore } from '@/stores/organizations'
import { useProjectsStore } from '@/stores/projects'

/**
 * Every project the caller can see. Private ones they are not on are simply absent — the
 * same answer the API gives their URLs, and for the same reason.
 */
const organizations = useOrganizationsStore()
const projects = useProjectsStore()
const router = useRouter()
const toast = useToast()

const showArchived = ref(false)
const archived = ref<Project[]>([])
const loading = ref(true)
const creating = ref(false)
const membersCanCreate = ref(true)

const slug = computed(() => organizations.currentSlug)
const myRole = computed(() => organizations.current?.role)
const mayCreate = computed(() => canCreateProjects(myRole.value, membersCanCreate.value))

const rows = computed(() => (showArchived.value ? archived.value : projects.projects))

async function load() {
  loading.value = true
  try {
    await organizations.load()
    if (!slug.value) return

    await projects.load()
    // Whether *this* person may start a project is the organization's own setting, so it
    // has to be read rather than guessed from their role.
    membersCanCreate.value = (await getOrganization(slug.value)).membersCanCreateProjects
  } catch (error) {
    toast.error(error)
  } finally {
    loading.value = false
  }
}

async function loadArchived() {
  if (!slug.value) return
  try {
    const all = await listProjects(slug.value, true)
    archived.value = all.filter((project) => project.isArchived)
  } catch (error) {
    toast.error(error)
  }
}

onMounted(load)
watch(slug, load)
watch(showArchived, (on) => {
  if (on) void loadArchived()
})

async function open(projectKey: string) {
  projects.select(projectKey)
  if (slug.value) await router.push(projectItemsPath(slug.value, projectKey))
}
</script>

<template>
  <AppShell>
    <div class="w-full px-6 py-6">
      <header class="border-border flex items-center justify-between border-b pb-3">
        <div>
          <h1 class="text-xl font-semibold tracking-tight">Projects</h1>
          <p class="text-muted-foreground mt-1 text-[12.5px]">
            Each one has its own items, board, workflow and wiki.
          </p>
        </div>
        <Button v-if="mayCreate" size="sm" @click="creating = true">New project</Button>
      </header>

      <CreateProjectDialog v-model:open="creating" @created="open" />

      <div class="border-border flex items-center gap-4 border-b" role="tablist">
        <button
          v-for="option in [
            { id: false, label: 'Active' },
            { id: true, label: 'Archived' },
          ]"
          :key="String(option.id)"
          type="button"
          role="tab"
          :aria-selected="showArchived === option.id"
          class="-mb-px border-b-2 px-1 py-2 text-[12.5px]"
          :class="
            showArchived === option.id
              ? 'border-primary text-foreground font-medium'
              : 'text-muted-foreground hover:text-foreground border-transparent'
          "
          @click="showArchived = option.id"
        >
          {{ option.label }}
        </button>
      </div>

      <UiPageState v-if="loading" state="loading" />

      <EmptyState
        v-else-if="rows.length === 0"
        :title="showArchived ? 'Nothing archived' : 'No projects yet'"
        :description="
          showArchived
            ? 'Archiving keeps a project readable without it cluttering the list.'
            : mayCreate
              ? 'A project is where the work lives. Create one to get started.'
              : 'Nobody has created one you can see yet.'
        "
        icon="◇"
      >
        <Button v-if="mayCreate && !showArchived" @click="creating = true">New project</Button>
      </EmptyState>

      <ul v-else class="grid gap-2 py-4 sm:grid-cols-2">
        <li
          v-for="project in rows"
          :key="project.id"
          class="border-border hover:bg-accent/40 rounded-lg border p-3"
        >
          <div class="flex items-start gap-2.5">
            <ProjectBadge :project="project" size="md" />
            <div class="min-w-0 flex-1">
              <button
                type="button"
                class="hover:underline focus-visible:ring-ring truncate text-sm font-medium focus-visible:ring-2 focus-visible:outline-none"
                @click="open(project.key)"
              >
                {{ project.name }}
              </button>
              <div class="text-muted-foreground flex items-center gap-1.5 text-[11px]">
                <span class="font-mono">{{ project.key }}</span>
                <span aria-hidden="true">·</span>
                <span class="capitalize">{{ project.role }}</span>
                <template v-if="project.visibility === 'private'">
                  <span aria-hidden="true">·</span>
                  <span>Private</span>
                </template>
                <template v-if="project.isArchived">
                  <Archive class="size-3" aria-hidden="true" />
                  <span>Archived</span>
                </template>
              </div>
              <p
                v-if="project.description"
                class="text-muted-foreground mt-1.5 line-clamp-2 text-xs"
              >
                {{ project.description }}
              </p>
            </div>
            <RouterLink
              v-if="project.role === 'admin'"
              :to="projectSettingsPath(slug!, project.key)"
              class="text-muted-foreground hover:text-foreground rounded p-1"
              :aria-label="`Settings for ${project.name}`"
            >
              <Settings2 class="size-3.5" aria-hidden="true" />
            </RouterLink>
          </div>
        </li>
      </ul>
    </div>
  </AppShell>
</template>
