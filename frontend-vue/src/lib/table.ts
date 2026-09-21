import type { ColumnDef, RowData } from '@tanstack/vue-table'

/**
 * The column type to use with `DataTable`.
 *
 * Pinned to TanStack Table **v8**, not v9: v9 moved row models into feature slots and
 * reshaped its generics, and nothing here needs what it added. v8 is the line the Vue
 * adapter and shadcn-vue's own table docs are written against, so it is the version a
 * future contributor will be able to look up.
 */
export type AictiqColumnDef<TData extends RowData> = ColumnDef<TData, unknown>
