/** Normalizes the grammar so URLs, saved views and API requests round-trip identically. */
export function parseItemFilter(value: string): string[] {
  return value.trim().split(/\s+/).filter(Boolean)
}
export function serializeItemFilter(tokens: Iterable<string>): string {
  return [...tokens].map((token) => token.trim()).filter(Boolean).join(' ')
}
