import { CliError, ExitCode } from './errors.js'
import { createProgram } from './program.js'

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
