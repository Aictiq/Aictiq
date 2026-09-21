import { apiFetch } from '@/utils/api'

export interface CommentAuthor {
  id: string
  displayName: string
  avatarKey: string | null
  isAgent: boolean
}

export interface CommentReaction {
  emoji: string
  count: number
  reactedByMe: boolean
}

export interface CommentRevision {
  id: string
  bodyMarkdown: string
  bodyHtml: string
  editedBy: string
  editedAt: string
}

export interface WorkItemComment {
  id: string
  author: CommentAuthor
  bodyMarkdown: string
  bodyHtml: string
  createdAt: string
  editedAt: string | null
  deletedAt: string | null
  mentions: string[]
  reactions: CommentReaction[]
  revisions: CommentRevision[]
}

export interface CommentPage {
  items: WorkItemComment[]
  page: number
  pageSize: number
  total: number
}

const base = (slug: string, itemKey: string) => `/orgs/${slug}/items/${itemKey}/comments`

export const listComments = (slug: string, itemKey: string, page = 1) =>
  apiFetch<CommentPage>(`${base(slug, itemKey)}?page=${page}`)

export const createComment = (slug: string, itemKey: string, bodyMarkdown: string) =>
  apiFetch<WorkItemComment>(base(slug, itemKey), { method: 'POST', body: { bodyMarkdown } })

export const updateComment = (slug: string, itemKey: string, commentId: string, bodyMarkdown: string) =>
  apiFetch<WorkItemComment>(`${base(slug, itemKey)}/${commentId}`, { method: 'PATCH', body: { bodyMarkdown } })

export const deleteComment = (slug: string, itemKey: string, commentId: string) =>
  apiFetch<void>(`${base(slug, itemKey)}/${commentId}`, { method: 'DELETE' })

export const reactToComment = (slug: string, itemKey: string, commentId: string, emoji: string) =>
  apiFetch<void>(`${base(slug, itemKey)}/${commentId}/reactions`, { method: 'PUT', body: { emoji } })

export const unreactFromComment = (slug: string, itemKey: string, commentId: string, emoji: string) =>
  apiFetch<void>(`${base(slug, itemKey)}/${commentId}/reactions/${encodeURIComponent(emoji)}`, { method: 'DELETE' })
