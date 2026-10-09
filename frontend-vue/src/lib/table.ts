import {
  createSortedRowModel,
  rowSortingFeature,
  sortFn_alphanumeric,
  sortFn_basic,
  sortFn_datetime,
  sortFn_text,
  tableFeatures,
  type ColumnDef,
  type RowData,
} from '@tanstack/vue-table'

/**
 * The TanStack Table features every `DataTable` gets.
 *
 * v9 only exposes the APIs of features registered here, so this is the whole surface the
 * app relies on: header-click sorting, client-side or server-driven. The sort functions are
 * the ones v8 bundled and picked from automatically by column value (text, alphanumeric
 * keys like `ACME-12`, dates); without them every column would fall back to `basic`.
 */
export const dataTableFeatures = tableFeatures({
  rowSortingFeature,
  sortedRowModel: createSortedRowModel(),
  sortFns: {
    alphanumeric: sortFn_alphanumeric,
    basic: sortFn_basic,
    datetime: sortFn_datetime,
    text: sortFn_text,
  },
})

/** The column type to use with `DataTable`. */
export type AictiqColumnDef<TData extends RowData> = ColumnDef<
  typeof dataTableFeatures,
  TData,
  unknown
>
