import { writeFile } from 'node:fs/promises'
import { Command } from 'commander'
import { createContext } from '../context.js'
import type { GlobalOptions } from '../context.js'

/** Attachment bytes are intentionally written verbatim: agents may fetch screenshots, logs,
 * PDFs or archives, and formatting them would corrupt every type except text. */
export function attachmentCommand(globals: () => GlobalOptions): Command {
  const attachment = new Command('attachment').description('Download an attachment')
  attachment
    .command('get')
    .description('Write an attachment to stdout, or to --output')
    .argument('<id>', 'Attachment UUID')
    .option('-o, --output <path>', 'Write to this path instead of stdout')
    .action(async (id: string, options: { output?: string }) => {
      const ctx = createContext(globals())
      const bytes = await ctx.client.download(
        '/api/v1/orgs/{orgSlug}/attachments/{attachmentId}/download',
        { path: { orgSlug: await ctx.org(), attachmentId: id } },
      )
      if (options.output) {
        await writeFile(options.output, bytes, { mode: 0o600 })
        return
      }
      process.stdout.write(bytes)
    })
  return attachment
}
