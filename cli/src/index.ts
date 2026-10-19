import { CliError, ExitCode } from './errors.js'
import { createProgram } from './program.js'
import {
  describeSkillResult,
  isInstalledPackage,
  skillAutoInstallDisabled,
  syncSkillOnce,
} from './skills/install.js'
import { packageRoot, version } from './version.js'

/**
 * The first command a new CLI version runs installs the Aictiq skill for the harnesses on this
 * machine (see `syncSkillOnce`). `--version` and `--help` stay side-effect free, and
 * `aictiq skills` does it itself.
 */
function syncSkill(args: string[]): void {
  const command = args.find((arg) => !arg.startsWith('-'))
  if (!command || command === 'skills' || command === 'help') return
  if (skillAutoInstallDisabled() || !isInstalledPackage(packageRoot)) return
  for (const result of syncSkillOnce({ packageRoot, version }) ?? []) {
    if (result.status === 'unchanged') continue
    const line = describeSkillResult(result)
    if (line) process.stderr.write(`${line}\n`)
  }
}

async function main(): Promise<void> {
  const program = createProgram()
  // Commander's own failures (unknown command, missing required option) are usage errors,
  // which is the same class as a rejected request body: exit 2.
  program.exitOverride((error) => {
    if (error.code === 'commander.helpDisplayed' || error.code === 'commander.version') {
      process.exit(ExitCode.Ok)
    }
    process.exit(error.exitCode === 0 ? ExitCode.Ok : ExitCode.Validation)
  })
  syncSkill(process.argv.slice(2))
  await program.parseAsync(process.argv)
}

main().catch((error: unknown) => {
  if (error instanceof CliError) {
    // The message already carries the problem's title and field errors.
    process.stderr.write(`${error.message}\n`)
    process.exit(error.exitCode)
  }
  process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`)
  process.exit(ExitCode.Error)
})
