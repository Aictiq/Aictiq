import { computed } from 'vue'
import { useRoute, useRouter } from 'vue-router'

import { ValidationError } from '@/utils/api'

/**
 * The filter (`f`), search (`q`) and sort (`s`) of an item surface, kept in the URL so a
 * filtered list, backlog or board can be bookmarked, shared and survives a reload. Every
 * change resets `page`: page 3 of a different result is not a place anyone asked to go.
 */
export function useItemQueryParams() {
  const route = useRoute()
  const router = useRouter()
  const read = (name: string) => {
    const value = route.query[name]
    return typeof value === 'string' ? value : ''
  }
  const filter = computed(() => read('f'))
  const search = computed(() => read('q'))
  const sort = computed(() => read('s'))

  function update(next: { f?: string; q?: string; s?: string }) {
    const query = { ...route.query, ...next, page: undefined }
    void router.replace({
      query: Object.fromEntries(
        Object.entries(query).filter(([, value]) => value !== undefined && value !== ''),
      ),
    })
  }

  return { filter, search, sort, update }
}

/** The server's message for a bad filter, search or sort, to show beside the bar. */
export function itemQueryError(error: unknown): string | null {
  if (!(error instanceof ValidationError)) return null
  const fields = error.fieldErrors
  return fields.filter?.[0] ?? fields.q?.[0] ?? fields.sort?.[0] ?? null
}
