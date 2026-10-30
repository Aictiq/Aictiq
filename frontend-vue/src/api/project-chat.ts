import { apiFetch } from '@/utils/api'

/**
 * Project chat: a factory operator asks the project's resolved agent a question without
 * filing a ticket. Each question is answered by a project chat run, an ordinary factory
 * run with no item; the answer comes back as Markdown and may link items the agent created.
 * One question is in progress at a time, and only the last few are kept.
 */

export type ProjectChatStatus = 'pending' | 'answered' | 'failed' | 'cancelled' | 'timedOut'

/** A file sent with a question; downloadable by its sender only. */
export interface ProjectChatAttachment {
  id: string
  fileName: string
  contentType: string
  sizeBytes: number
}

/** Mirrors the server's chat file rules: images, PDFs, text and ZIP archives, five per question. */
export const chatAttachmentAccept = [
  'image/png',
  'image/jpeg',
  'image/gif',
  'image/webp',
  'application/pdf',
  'application/zip',
  'text/plain',
  'text/markdown',
  'text/csv',
  '.png',
  '.jpg',
  '.jpeg',
  '.gif',
  '.webp',
  '.pdf',
  '.zip',
  '.txt',
  '.md',
  '.markdown',
  '.csv',
].join(',')

export const MAX_CHAT_ATTACHMENTS = 5

export interface ProjectChatMessage {
  id: string
  question: string
  /** Markdown; null until answered. */
  answer: string | null
  status: ProjectChatStatus
  /** While pending: `queued`, `assigned` or `running`. Null otherwise. */
  runStatus: string | null
  /** Human readable, for a failed, cancelled or timed out question. */
  error: string | null
  runId: string | null
  askedAt: string
  answeredAt: string | null
  /** The files sent with the question, in the order they were attached. */
  attachments: ProjectChatAttachment[]
}

export interface ProjectChatView {
  /** Oldest first, at most 10. */
  messages: ProjectChatMessage[]
  /** Any usable runner that offers the harness is online right now: the default runner, when there is one. */
  runnerOnline: boolean
  /** The project's default runner, when questions go to it. Absent from older servers. */
  runnerName?: string | null
}

const chatPath = (slug: string, projectKey: string) => `/orgs/${slug}/projects/${projectKey}/chat`

export const getProjectChat = (slug: string, projectKey: string) =>
  apiFetch<ProjectChatView>(chatPath(slug, projectKey))

/**
 * 409 while another question is in progress; 400 when no eligible agent can be resolved or a
 * file cannot go along (`attachmentIds`), 402 when the files do not fit the storage allowance.
 * `attachmentIds` are pending uploads made with `uploadAttachment(..., 'chat')`.
 */
export const askProjectChat = (
  slug: string,
  projectKey: string,
  question: string,
  attachmentIds: string[] = [],
) =>
  apiFetch<ProjectChatView>(chatPath(slug, projectKey), {
    method: 'POST',
    body: { question, attachmentIds },
  })

/** Clears the history and cancels a question in progress. */
export const clearProjectChat = (slug: string, projectKey: string) =>
  apiFetch<void>(chatPath(slug, projectKey), { method: 'DELETE' })
