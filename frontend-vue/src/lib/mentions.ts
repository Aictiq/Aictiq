/** Someone a comment can tag. */
export interface Mentionable {
  id: string
  name: string
  avatarSrc?: string | null
  isAgent?: boolean
}

/**
 * The text written after "@" for a person: their display name with everything but
 * letters, digits and hyphens removed, so "Ana Kovač" becomes "AnaKovač". The API resolves
 * a token by the same rule (CommentEndpoints.MentionToken), and the two must agree.
 */
export function mentionToken(name: string) {
  return name.replace(/[^\p{L}\p{N}-]+/gu, '')
}

/** Lower case without accents, so "sar" finds Šarić. */
function fold(value: string) {
  return value.normalize('NFD').replace(/\p{M}/gu, '').toLowerCase()
}

/** People whose name or any word of it starts with what was typed, best matches first. */
export function searchMentionables(people: Mentionable[], query: string, limit = 8) {
  const wanted = fold(query)
  if (!wanted) return people.slice(0, limit)
  const scored = people.flatMap((person) => {
    const name = fold(person.name)
    if (name.startsWith(wanted) || fold(mentionToken(person.name)).startsWith(wanted))
      return [{ person, rank: 0 }]
    if (name.split(/\s+/).some((word) => word.startsWith(wanted))) return [{ person, rank: 1 }]
    if (name.includes(wanted)) return [{ person, rank: 2 }]
    return []
  })
  return scored
    .sort((a, b) => a.rank - b.rank || a.person.name.localeCompare(b.person.name))
    .slice(0, limit)
    .map((entry) => entry.person)
}
