<script setup lang="ts">
import { computed, defineAsyncComponent } from 'vue'
import { useRoute } from 'vue-router'

import UiPageState from '@/components/UiPageState.vue'
// The wrapper, not vue-sonner's own Toaster: it dresses the toast in the app's popover
// tokens and its icon set, so a toast belongs to the same screen it appears over.
import { Toaster } from '@/components/ui/sonner'
import { useDocumentTitle } from '@/composables/useDocumentTitle'
import { activityToasterId } from '@/composables/useToast'
import { useOrganizationsStore } from '@/stores/organizations'
import { useProjectsStore } from '@/stores/projects'
import { useSessionStore } from '@/stores/session'
import { useUiStore } from '@/stores/ui'

// The router guard resolves the session before the first navigation completes, so this
// only covers the frame before that: without it the app flashes the login page at
// someone who is, in fact, signed in.
const session = useSessionStore()
// A toast is a card over the page, so it follows the page's theme rather than sonner's
// own default of light.
const ui = useUiStore()
// The tab names the page and the project, so a row of Aictiq tabs can be told apart.
useDocumentTitle()

// The project chat floats over a project's work screens (the routes flagged
// `projectChat`). It lives here rather than in each page's shell so it stays open, and keeps
// its conversation, while moving between those screens. `/board` names no project in its
// URL, so the remembered organization and project stand in.
const ProjectChatPanel = defineAsyncComponent(() => import('@/components/ProjectChatPanel.vue'))
const route = useRoute()
const organizations = useOrganizationsStore()
const projects = useProjectsStore()
const chatScope = computed(() => {
  if (!route.meta.projectChat || !session.isAuthenticated) return null
  const slug = typeof route.params.slug === 'string' ? route.params.slug : organizations.currentSlug
  const projectKey =
    typeof route.params.projectKey === 'string' ? route.params.projectKey : projects.currentKey
  return slug && projectKey ? { slug, projectKey } : null
})
</script>

<template>
  <UiPageState v-if="!session.isResolved" state="loading" />
  <RouterView v-else />
  <ProjectChatPanel v-if="chatScope" :slug="chatScope.slug" :project-key="chatScope.projectKey" />
  <Toaster :theme="ui.resolvedTheme" position="bottom-right" rich-colors close-button />
  <Toaster :id="activityToasterId" :theme="ui.resolvedTheme" position="top-right" rich-colors close-button />
</template>
