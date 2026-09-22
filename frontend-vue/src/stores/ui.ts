import { defineStore } from 'pinia'
import { computed, ref, watch } from 'vue'

/**
 * Presentation preferences: theme, density, sidebar. All per-browser, none of it worth a
 * round trip, so `localStorage` is the right home - and every read is guarded because a
 * private window, cleared site data, or a browser set to block storage makes it throw
 * rather than return null.
 */

export type ThemePreference = 'light' | 'dark' | 'system'
export type Density = 'comfortable' | 'compact'

const STORAGE_KEY = 'aictiq.ui'

interface Persisted {
  theme: ThemePreference
  density: Density
  sidebarCollapsed: boolean
}

const defaults: Persisted = {
  // System, not dark: the app should look like the rest of the machine on first run.
  theme: 'system',
  density: 'comfortable',
  sidebarCollapsed: false,
}

function read(): Persisted {
  try {
    const raw = localStorage.getItem(STORAGE_KEY)
    if (!raw) return { ...defaults }
    const parsed = JSON.parse(raw) as Partial<Persisted>
    return {
      theme: parsed.theme ?? defaults.theme,
      density: parsed.density ?? defaults.density,
      sidebarCollapsed: parsed.sidebarCollapsed ?? defaults.sidebarCollapsed,
    }
  } catch {
    // Unreadable or unparseable storage is not worth failing a page load over.
    return { ...defaults }
  }
}

function write(state: Persisted) {
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(state))
  } catch {
    // Storage may be blocked entirely. The preference still applies for this session.
  }
}

export const useUiStore = defineStore('ui', () => {
  const initial = read()

  const theme = ref<ThemePreference>(initial.theme)
  const density = ref<Density>(initial.density)
  const sidebarCollapsed = ref(initial.sidebarCollapsed)
  // The phone drawer is transient UI, not a preference. Persisting it would make a
  // reload reopen a modal surface over the page, and it is independent from the
  // desktop rail's collapsed state.
  const mobileSidebarOpen = ref(false)

  /** Tracks the OS setting so `theme: 'system'` follows it live, not just at load. */
  const prefersDark = ref(
    typeof window !== 'undefined' && window.matchMedia('(prefers-color-scheme: dark)').matches,
  )

  if (typeof window !== 'undefined') {
    window
      .matchMedia('(prefers-color-scheme: dark)')
      .addEventListener('change', (event) => (prefersDark.value = event.matches))
  }

  const resolvedTheme = computed<'light' | 'dark'>(() =>
    theme.value === 'system' ? (prefersDark.value ? 'dark' : 'light') : theme.value,
  )

  function apply() {
    if (typeof document === 'undefined') return
    const root = document.documentElement
    root.classList.toggle('dark', resolvedTheme.value === 'dark')
    root.dataset.density = density.value
    root.style.colorScheme = resolvedTheme.value
  }

  watch([resolvedTheme, density], apply, { immediate: true })

  watch([theme, density, sidebarCollapsed], () =>
    write({
      theme: theme.value,
      density: density.value,
      sidebarCollapsed: sidebarCollapsed.value,
    }),
  )

  /** Cycles light → dark → system, which is what a single toggle command should do. */
  function cycleTheme() {
    theme.value = theme.value === 'light' ? 'dark' : theme.value === 'dark' ? 'system' : 'light'
  }

  function setTheme(next: ThemePreference) {
    theme.value = next
  }

  function setDensity(next: Density) {
    density.value = next
  }

  function toggleSidebar() {
    sidebarCollapsed.value = !sidebarCollapsed.value
  }

  function setMobileSidebarOpen(open: boolean) {
    mobileSidebarOpen.value = open
  }

  function toggleMobileSidebar() {
    mobileSidebarOpen.value = !mobileSidebarOpen.value
  }

  return {
    theme,
    density,
    sidebarCollapsed,
    mobileSidebarOpen,
    resolvedTheme,
    cycleTheme,
    setTheme,
    setDensity,
    toggleSidebar,
    setMobileSidebarOpen,
    toggleMobileSidebar,
  }
})
