export interface Column<T> {
  header: string
  value: (row: T) => string
  /** Right-align numeric columns so magnitudes line up. */
  align?: 'left' | 'right'
}

/**
 * Every command prints either `--json` (the machine contract) or a table (the human one).
 * The table is plain text with two spaces between columns: no colour, no box drawing, and
 * no truncation of the last column, so `| grep` and `| cut` stay useful.
 */
export function renderTable<T>(rows: readonly T[], columns: readonly Column<T>[]): string {
  const cells = rows.map((row) => columns.map((column) => column.value(row) ?? ''))
  const widths = columns.map((column, index) =>
    Math.max(column.header.length, ...cells.map((row) => row[index]?.length ?? 0), 0),
  )
  const line = (values: readonly string[]) =>
    values
      .map((value, index) =>
        index === values.length - 1
          ? columns[index]?.align === 'right'
            ? value.padStart(widths[index]!)
            : value
          : columns[index]?.align === 'right'
            ? value.padStart(widths[index]!)
            : value.padEnd(widths[index]!),
      )
      .join('  ')
      .trimEnd()

  return [line(columns.map((c) => c.header)), ...cells.map(line)].join('\n')
}

/** Key/value block for a single record, used by the `view` commands. */
export function renderFields(fields: readonly (readonly [string, string | undefined])[]): string {
  const present = fields.filter((f): f is [string, string] => f[1] !== undefined && f[1] !== '')
  const width = Math.max(0, ...present.map(([label]) => label.length))
  return present.map(([label, value]) => `${label.padEnd(width)}  ${value}`).join('\n')
}

export function print(text: string): void {
  process.stdout.write(text.endsWith('\n') ? text : `${text}\n`)
}

export function printJson(value: unknown): void {
  process.stdout.write(`${JSON.stringify(value, null, 2)}\n`)
}

export function formatDate(value: string | null | undefined): string {
  if (!value) return ''
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? String(value) : date.toISOString().slice(0, 10)
}

export function formatNumber(value: number | null | undefined): string {
  return value === null || value === undefined ? '' : String(value)
}

/** Date and time to the second, UTC — a run's timings are minutes apart, not days. */
export function formatDateTime(value: string | null | undefined): string {
  if (!value) return ''
  const date = new Date(value)
  return Number.isNaN(date.getTime())
    ? String(value)
    : `${date.toISOString().slice(0, 19).replace('T', ' ')}Z`
}
