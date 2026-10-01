import type { RouteLocationNormalizedLoaded, RouteLocationRaw } from 'vue-router'

import { projectItemsPath } from '@/router/paths'

export function projectSwitchNeedsTeam(route: RouteLocationNormalizedLoaded) {
  return ['team-backlog', 'backlog', 'team-sprints', 'sprint-detail'].includes(String(route.name))
}

/** Carry the view across projects, without carrying resource ids from the old project. */
export function projectSwitchLocation(
  route: RouteLocationNormalizedLoaded,
  slug: string,
  projectKey: string,
  isAdmin: boolean,
  teamId: string | null,
): RouteLocationRaw {
  const items = projectItemsPath(slug, projectKey)
  const params = { slug, projectKey }
  const name = String(route.name)

  if (name === 'board') return { name }
  if (projectSwitchNeedsTeam(route)) {
    if (!teamId) return items
    return {
      name: name === 'team-backlog' || name === 'backlog' ? 'team-backlog' : 'team-sprints',
      params: { ...params, teamId },
    }
  }
  if (name === 'item-detail' || name === 'items') return items
  if (name === 'project-wiki') return { name, params }
  if (name === 'team-settings') {
    return isAdmin ? { name: 'project-settings-teams', params } : items
  }
  if (name.startsWith('project-settings')) return isAdmin ? { name, params } : items
  if (
    [
      'project-dashboard',
      'project-items',
      'project-board',
      'project-analytics',
      'project-portfolio',
    ].includes(name)
  ) {
    return { name, params }
  }
  // Personal and organization views do not belong to a project. Keep their full URL.
  return route.fullPath
}
