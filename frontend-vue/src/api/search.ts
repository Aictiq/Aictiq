import { apiFetch } from '@/utils/api'

export interface SearchItem {
  id: string
  key: string
  title: string
  /** HTML-escaped text with server-owned <mark> delimiters. */
  snippet: string
  rank: number
}

export interface SearchComment {
  id: string
  itemKey: string
  itemTitle: string
  /** HTML-escaped text with server-owned <mark> delimiters. */
  snippet: string
  rank: number
}

/** Wiki pages have no item key, so callers distinguish them by their result group. */
export interface SearchPage {
  id: string
  projectId: string
  slug: string
  title: string
  /** HTML-escaped text with server-owned <mark> delimiters. */
  snippet: string
  rank: number
}

export interface SearchResponse {
  items: SearchItem[]
  comments: SearchComment[]
  pages: SearchPage[]
}

export function searchOrganization(
  slug: string,
  q: string,
  options: { types?: 'items' | 'comments'; limit?: number } = {},
) {
  return apiFetch<SearchResponse>(`/orgs/${slug}/search`, {
    query: { q, types: options.types, limit: options.limit },
  })
}

export function searchProject(
  slug: string,
  projectKey: string,
  q: string,
  options: { types?: 'items' | 'comments'; limit?: number } = {},
) {
  return apiFetch<SearchResponse>(`/orgs/${slug}/projects/${projectKey}/search`, {
    query: { q, types: options.types, limit: options.limit },
  })
}
