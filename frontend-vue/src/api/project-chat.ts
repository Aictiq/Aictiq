import { apiFetch } from '@/utils/api'

/**
 * Project chat: a factory operator asks the project's default agent a question without
 * filing a ticket. Each question is answered by a project chat run, an ordinary factory
 * run with no item; the answer comes back as Markdown and may link items the agent created.
 * One question is in progress at a time, and only the last few are kept.
 */

export type ProjectChatStatus = 'pending' | 'answered' | 'failed' | 'cancelled' | 'timedOut'

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
}

export interface ProjectChatView {
  /** Oldest first, at most 10. */
  messages: ProjectChatMessage[]
  /** Any usable runner that offers the harness is online right now. */
  runnerOnline: boolean
}

const chatPath = (slug: string, projectKey: string) =>
  `/orgs/${slug}/projects/${projectKey}/chat`

export const getProjectChat = (slug: string, projectKey: string) =>
  apiFetch<ProjectChatView>(chatPath(slug, projectKey))

/** 409 while another question is in progress; 422 when the project has no default agent. */
export const askProjectChat = (slug: string, projectKey: string, question: string) =>
  apiFetch<ProjectChatView>(chatPath(slug, projectKey), { method: 'POST', body: { question } })

/** Clears the history and cancels a question in progress. */
export const clearProjectChat = (slug: string, projectKey: string) =>
  apiFetch<void>(chatPath(slug, projectKey), { method: 'DELETE' })
