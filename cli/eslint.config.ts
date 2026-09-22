import js from '@eslint/js'
import type { Linter } from 'eslint'
import prettier from 'eslint-config-prettier'
import globals from 'globals'
import tseslint from 'typescript-eslint'

// The explicit annotation keeps the emitted type from naming a transitively-installed
// package path (pnpm's strict node_modules makes that unportable).
const config: Linter.Config[] = tseslint.config(
  {
    name: 'aictiq-cli/ignores',
    // schema.d.ts is generated from the API's OpenAPI document by `pnpm gen:api`;
    // lint findings in it are not actionable here.
    ignores: ['dist/**', 'coverage/**', 'src/api/schema.d.ts'],
  },
  js.configs.recommended,
  ...tseslint.configs.recommended,
  {
    name: 'aictiq-cli/src',
    files: ['**/*.ts', '**/*.mts', '**/*.mjs'],
    languageOptions: {
      globals: { ...globals.node },
    },
    rules: {
      '@typescript-eslint/no-unused-vars': ['error', { argsIgnorePattern: '^_' }],
    },
  },
  prettier,
) as Linter.Config[]

export default config
