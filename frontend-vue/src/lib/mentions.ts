/** Someone a comment can tag. */
export interface Mentionable {
  id: string
  name: string
  avatarSrc?: string | null
  isAgent?: boolean
  /** Shown beside the name when someone else has the same one, e.g. their email. */
  detail?: string | null
}

/**
 * The text written after "@" for a person: their display name with everything but
 * letters, digits and hyphens removed, so "Ana Kovač" becomes "AnaKovač". The API resolves
 * a token by the same rule (CommentEndpoints.MentionToken), and the two must agree.
 */
export function mentionToken(name: string) {
  return name.replace(/[^\p{L}\p{N}-]+/gu, '')
}

/** True when someone other than `person` among `people` writes the same token. */
export function hasNamesake(person: Mentionable, people: Mentionable[]) {
  const token = mentionToken(person.name).toLowerCase()
  return people.some(
    (other) => other.id !== person.id && mentionToken(other.name).toLowerCase() === token,
  )
}

/**
 * What to write after "@" so the tag names exactly `person`. A namesake among `people` would
 * make the plain token fit both, which the API refuses to guess at, so it gets the first eight
 * hex digits of the person's id: "@AnaKovač-5d7f9d82". The API reads the same suffix
 * (CommentEndpoints.IdSuffix).
 */
export function uniqueMentionToken(person: Mentionable, people: Mentionable[]) {
  const token = mentionToken(person.name)
  if (!hasNamesake(person, people)) return token
  return `${token}-${person.id.replace(/-/g, '').slice(0, 8)}`
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
