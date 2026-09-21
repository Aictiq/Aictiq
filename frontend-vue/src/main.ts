import { VueQueryPlugin, type VueQueryPluginOptions } from '@tanstack/vue-query'
import { createPinia } from 'pinia'
import { createApp } from 'vue'
import { use } from 'echarts/core'
import { CanvasRenderer } from 'echarts/renderers'
import { BarChart, LineChart, ScatterChart } from 'echarts/charts'
import { GridComponent, LegendComponent, TooltipComponent } from 'echarts/components'

import App from './App.vue'
import router from './router'
import { useOnboardingStore } from './stores/onboarding'
import { useOrganizationsStore } from './stores/organizations'
import { useProjectsStore } from './stores/projects'
import { useSessionStore } from './stores/session'
import { useTeamsStore } from './stores/teams'
import { setUnauthenticatedHandler } from './utils/api'

import './assets/index.css'

// Keep ECharts tree-shakeable: analytics routes lazy-load their view, and only the chart
// types used by Aictiq are registered once the app is evaluated.
use([CanvasRenderer, BarChart, LineChart, ScatterChart, GridComponent, LegendComponent, TooltipComponent])

const queryOptions: VueQueryPluginOptions = {
  queryClientConfig: {
    defaultOptions: {
      queries: {
        // Server state is authoritative and cheap to refetch; keep it briefly fresh so
        // navigating back to a list does not flash a spinner.
        staleTime: 30_000,
        // A 401 or 404 will not fix itself by asking again — and the api client has
        // already tried a token refresh by the time an error reaches here.
        retry: (failureCount, error) => {
          const status = (error as { status?: number }).status ?? 0
          return status >= 500 && failureCount < 2
        },
      },
    },
  },
}

const app = createApp(App)
const pinia = createPinia()

app.use(pinia).use(router).use(VueQueryPlugin, queryOptions)

// The api client cannot import the router or a store without a cycle, so the reaction to
// a session that could not be refreshed is wired in here instead.
setUnauthenticatedHandler(() => {
  useSessionStore(pinia).clear()
  // The next person at this browser is not them: drop the organization and project lists
  // and the remembered choices along with the session.
  useOrganizationsStore(pinia).clear()
  useProjectsStore(pinia).clear()
  useTeamsStore(pinia).clear()
  useOnboardingStore(pinia).clear()

  const current = router.currentRoute.value
  // A public page (the auth pages themselves) must not bounce.
  if (current.meta.requiresAuth) {
    void router.replace({ name: 'login', query: { next: current.fullPath } })
  }
})

app.mount('#app')
