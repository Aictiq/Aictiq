import { useQueries } from '@tanstack/vue-query'
import { computed, type ComputedRef } from 'vue'

import { getItem } from '@/api/items'
import { referencedItemKeys } from '@/lib/markdown'

/**
 * The `#KEY` references in `source` that the API confirmed exist, so only real tickets
 * become links. Without a slug and project key nothing is looked up.
 */
export function useResolvedItemKeys(
  source: () => string,
  slug: () => string | undefined,
  projectKey: () => string | undefined,
): ComputedRef<string[]> {
  const keys = computed(() => {
    const project = projectKey()
    return slug() && project ? referencedItemKeys(source(), project) : []
  })
  const items = useQueries({
    queries: computed(() => keys.value.map((key) => ({
      queryKey: [slug(), key],
      queryFn: () => getItem(slug()!, key),
      retry: false,
      staleTime: 60_000,
    }))),
  })
  return computed(() => items.value.flatMap((item) => item.data ? [item.data.key] : []))
}
