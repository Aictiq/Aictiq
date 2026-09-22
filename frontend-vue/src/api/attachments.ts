import { apiFetch } from '@/utils/api'

/**
 * Files on items and comments. The file is posted to the API, which stores it as a *pending*
 * attachment - images re-encoded as WebP, at most 1920px wide - and `commit` binds it to
 * exactly one owner. The browser never talks to the object store: files are served from the
 * authenticated download route. An upload that is never committed is swept after an hour.
 *
 * A comment has no id until it is posted, so a comment's files stay pending while it is
 * being written (their uploader can already view them) and are committed right after.
 */

export interface Attachment {
  id: string
  projectId: string
  itemId: string | null
  commentId: string | null
  pageId: string | null
  fileName: string
  contentType: string
  sizeBytes: number
  uploadedBy: string
  createdAt: string
}

export type AttachmentOwner = { itemId: string } | { commentId: string } | { pageId: string }

/** Mirrors the server's default `Attachments:AllowedContentTypes`; the server decides. */
export const attachmentContentTypes = [
  'image/png',
  'image/jpeg',
  'image/gif',
  'image/webp',
  'application/pdf',
  'text/plain',
  'text/markdown',
  'application/zip',
]

export const attachmentUrl = (slug: string, attachmentId: string) =>
  `/api/v1/orgs/${slug}/attachments/${attachmentId}/download`

const downloadPattern = /\/api\/v1\/orgs\/[^/]+\/attachments\/([0-9a-f-]{36})\/download/gi

/** The attachment ids a Markdown body still refers to. */
export function referencedAttachmentIds(markdown: string): Set<string> {
  return new Set([...markdown.matchAll(downloadPattern)].map((match) => match[1]!.toLowerCase()))
}

export const listItemAttachments = (slug: string, itemKey: string) =>
  apiFetch<Attachment[]>(`/orgs/${slug}/items/${itemKey}/attachments`)

export const commitAttachment = (slug: string, attachmentId: string, owner: AttachmentOwner) =>
  apiFetch<Attachment>(`/orgs/${slug}/attachments/${attachmentId}/commit`, {
    method: 'POST',
    body: owner,
  })

export const deleteAttachment = (slug: string, attachmentId: string) =>
  apiFetch<void>(`/orgs/${slug}/attachments/${attachmentId}`, { method: 'DELETE' })

/** A pasted screenshot arrives as `image.png` - give it a name worth downloading. */
function fileNameOf(file: File): string {
  if (file.name && file.name !== 'image.png') return file.name
  const extension = file.type.split('/')[1] ?? 'bin'
  return `pasted-${new Date().toISOString().replace(/[:.]/g, '-')}.${extension}`
}

/** Uploads a file as a pending attachment and returns its id. */
export async function uploadAttachment(slug: string, projectKey: string, file: File): Promise<string> {
  const form = new FormData()
  form.append('file', file, fileNameOf(file))
  const attachment = await apiFetch<Attachment>(`/orgs/${slug}/projects/${projectKey}/attachments`, {
    method: 'POST',
    body: form,
  })
  return attachment.id
}

/**
 * Commits every pending upload the body still references to its new owner, and discards the
 * ones that were removed before posting - the sweeper would get them in an hour, but there
 * is no reason to leave them.
 */
export async function settleAttachments(
  slug: string,
  pending: Iterable<string>,
  markdown: string,
  owner: AttachmentOwner,
): Promise<void> {
  const kept = referencedAttachmentIds(markdown)
  await Promise.allSettled(
    [...pending].map((id) =>
      kept.has(id.toLowerCase()) ? commitAttachment(slug, id, owner) : deleteAttachment(slug, id),
    ),
  )
}
