import type { RefinementStatus } from '@/api/refinement'

const MaxDraftTitle = 80

/**
 * A title for a ticket filed with only a description, until the refine run writes a real
 * one: its first line of prose, without Markdown images, links or emphasis.
 */
export function draftTitle(markdown: string): string {
  const prose = markdown
    .replace(/!\[[^\]]*\]\([^)]*\)/g, ' ')
    .replace(/\[([^\]]*)\]\([^)]*\)/g, '$1')
    .replace(/[#>*_`~]/g, ' ')
  const line =
    prose
      .split('\n')
      .map((part) => part.replace(/\s+/g, ' ').trim())
      .find((part) => part.length > 0) ?? ''
  if (!line) return 'Untitled ticket'
  return line.length > MaxDraftTitle ? `${line.slice(0, MaxDraftTitle - 1).trimEnd()}…` : line
}

export const refinementLabels: Record<RefinementStatus, string> = {
  refining: 'Refining',
  needsInput: 'Needs your input',
  ready: 'Ready for review',
  failed: 'Refinement failed',
  confirmed: 'Confirmed',
}
