import { CliError, ExitCode } from '../errors.js'

/**
 * Runner tags (`--tag`): free-form labels the instance routes runs by, so work that needs,
 * say, `gpu` or `docker` reaches a runner that has it. The instance validates them the
 * same way; checking here first turns a typo into a usage error instead of a refused hello.
 */
export const TagPattern = /^[a-z0-9][a-z0-9._:-]{0,49}$/
export const MaxTags = 20

export const InvalidTagsMessage =
  "Tags are lower case letters, digits, '-', '_', '.' or ':', at most 50 characters, at most 20 of them."

/** Commander's collector for a repeatable `--tag`; `--tag a,b` is the same as `--tag a --tag b`. */
export function collectTags(value: string, previous: string[] = []): string[] {
  return [...previous, ...value.split(',')]
}

/** Trimmed, lower case, distinct and sorted, so every probe reports the same list. */
export function normalizeTags(raw: readonly string[] | undefined): string[] {
  const tags = [...new Set((raw ?? []).map((tag) => tag.trim().toLowerCase()).filter((tag) => tag !== ''))].sort()
  if (tags.length > MaxTags || tags.some((tag) => !TagPattern.test(tag))) {
    throw new CliError(InvalidTagsMessage, ExitCode.Validation)
  }
  return tags
}
