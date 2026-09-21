<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { FileText, Loader2, Search } from '@lucide/vue'

import { searchOrganization, type SearchResponse } from '@/api/search'
import KeyChip from '@/components/common/KeyChip.vue'
import SearchSnippet from '@/components/common/SearchSnippet.vue'
import AppShell from '@/components/shell/AppShell.vue'
import { useItemModal } from '@/composables/useItemModal'
import { useOrganizationsStore } from '@/stores/organizations'

const route = useRoute()
const router = useRouter()
const organizations = useOrganizationsStore()
const itemModal = useItemModal()
const input = ref(typeof route.query.q === 'string' ? route.query.q : '')
const results = ref<SearchResponse | null>(null)
const loading = ref(false)
const error = ref(false)
const query = computed(() => (typeof route.query.q === 'string' ? route.query.q.trim() : ''))

async function load() {
  const slug = organizations.currentSlug
  if (!slug || !query.value) { results.value = null; return }
  loading.value = true
  error.value = false
  try { results.value = await searchOrganization(slug, query.value, { limit: 50 }) }
  catch { error.value = true }
  finally { loading.value = false }
}

function submit() {
  const q = input.value.trim()
  void router.replace({ name: 'search', query: q ? { q } : {} })
}

watch([query, () => organizations.currentSlug], () => void load(), { immediate: true })
watch(query, (value) => { input.value = value })
</script>

<template>
  <AppShell>
    <section class="w-full px-5 py-8">
      <h1 class="text-xl font-semibold">Search</h1>
      <form class="border-input mt-5 flex items-center gap-2 rounded-md border px-3" @submit.prevent="submit">
        <Search class="text-muted-foreground size-4" aria-hidden="true" />
        <label for="search-query" class="sr-only">Search items, comments and pages</label>
        <input id="search-query" v-model="input" class="h-11 min-w-0 flex-1 bg-transparent text-sm outline-none" placeholder="Search items, comments and pages…" autofocus />
      </form>

      <div v-if="loading" class="text-muted-foreground flex items-center gap-2 py-10 text-sm"><Loader2 class="size-4 animate-spin" /> Searching…</div>
      <p v-else-if="error" class="text-destructive py-10 text-sm">Search could not be loaded. Please try again.</p>
      <p v-else-if="query && results && results.items.length + results.comments.length + results.pages.length === 0" class="text-muted-foreground py-10 text-sm">No items, comments or pages match “{{ query }}”.</p>
      <div v-else-if="results" class="mt-7 space-y-8">
        <section v-if="results.items.length">
          <h2 class="font-label mb-2">Items</h2>
          <article v-for="item in results.items" :key="item.id" class="border-border border-b py-3">
            <button type="button" class="flex items-center gap-2 text-left" @click="itemModal.open(item.key)"><KeyChip :label="item.key" /><span class="text-sm font-medium hover:underline">{{ item.title }}</span></button>
            <p v-if="item.snippet" class="text-muted-foreground mt-1 text-sm"><SearchSnippet :snippet="item.snippet" /></p>
          </article>
        </section>
        <section v-if="results.comments.length">
          <h2 class="font-label mb-2">Comments</h2>
          <article v-for="comment in results.comments" :key="comment.id" class="border-border border-b py-3">
            <button type="button" class="flex items-center gap-2 text-left" @click="itemModal.open(comment.itemKey)"><KeyChip :label="comment.itemKey" /><span class="text-sm font-medium hover:underline">{{ comment.itemTitle }}</span></button>
            <p class="text-muted-foreground mt-1 text-sm"><SearchSnippet :snippet="comment.snippet" /></p>
          </article>
        </section>
        <section v-if="results.pages.length">
          <h2 class="font-label mb-2">Pages</h2>
          <article v-for="page in results.pages" :key="page.id" class="border-border border-b py-3">
            <div class="flex items-center gap-2"><FileText class="text-muted-foreground size-4" aria-hidden="true" /><span class="text-sm font-medium">{{ page.title }}</span></div>
            <p v-if="page.snippet" class="text-muted-foreground mt-1 text-sm"><SearchSnippet :snippet="page.snippet" /></p>
          </article>
        </section>
      </div>
    </section>
  </AppShell>
</template>
