import { apiFetch } from '@/utils/api'

export interface WikiTreePage { id: string; slug: string; title: string; parentId: string | null; position: number; updatedAt: string }
export interface WikiPage extends WikiTreePage { projectId: string; contentMarkdown: string; contentHtml: string; revisionNumber: number; summary: string | null; version: number }
export interface WikiRevisionAuthor { id: string; displayName: string; avatarKey: string | null; isAgent: boolean }
export interface WikiRevision { number: number; authorId: string; author: WikiRevisionAuthor; summary: string | null; at: string; sizeDelta: number; isCurrent: boolean }
export interface WikiRevisionDetail { number: number; contentMarkdown: string; contentHtml: string; authorId: string; at: string; summary: string | null }
export interface WikiDiffLine { kind: 'inserted' | 'deleted' | 'unchanged' | string; text: string; oldLine: number | null; newLine: number | null }
export interface WikiDiff { from: number; to: number; hunks: { oldStart: number; oldLines: number; newStart: number; newLines: number; lines: WikiDiffLine[] }[] }
export interface WikiPermission { subjectKind: 'team' | 'user' | 'projectRole'; subjectId: string; access: 'read' | 'write' }

const projectBase = (slug: string, key: string) => `/orgs/${slug}/projects/${key}/wiki`
const pageBase = (slug: string, id: string) => `/orgs/${slug}/wiki/pages/${id}`
export const wikiTree = (slug: string, key: string) => apiFetch<WikiTreePage[]>(`${projectBase(slug, key)}/tree`)
export const createWikiPage = (slug: string, key: string, body: { parentId?: string | null; title: string; contentMd: string }) => apiFetch<WikiPage>(`${projectBase(slug, key)}/pages`, { method: 'POST', body })
export const getWikiPage = (slug: string, id: string) => apiFetch<WikiPage>(pageBase(slug, id))
export const updateWikiPage = (slug: string, id: string, body: { title?: string; contentMd?: string; version: number; summary?: string }) => apiFetch<WikiPage>(pageBase(slug, id), { method: 'PATCH', body })
export const deleteWikiPage = (slug: string, id: string) => apiFetch<void>(pageBase(slug, id), { method: 'DELETE' })
export const moveWikiPage = (slug: string, id: string, body: { parentId: string | null; position: number; version: number }) => apiFetch<WikiPage>(`${pageBase(slug, id)}/move`, { method: 'POST', body })
export const wikiRevisions = (slug: string, id: string) => apiFetch<WikiRevision[]>(`${pageBase(slug, id)}/revisions`)
export const wikiRevision = (slug: string, id: string, number: number) => apiFetch<WikiRevisionDetail>(`${pageBase(slug, id)}/revisions/${number}`)
export const wikiDiff = (slug: string, id: string, from: number, to: number) => apiFetch<WikiDiff>(`${pageBase(slug, id)}/diff?from=${from}&to=${to}`)
export const restoreWikiRevision = (slug: string, id: string, number: number) => apiFetch<WikiPage>(`${pageBase(slug, id)}/revisions/${number}/restore`, { method: 'POST' })
export const wikiPermissions = (slug: string, id: string) => apiFetch<WikiPermission[]>(`${pageBase(slug, id)}/permissions`)
export const replaceWikiPermissions = (slug: string, id: string, rules: WikiPermission[]) => apiFetch<WikiPermission[]>(`${pageBase(slug, id)}/permissions`, { method: 'PUT', body: { rules } })
