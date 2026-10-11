import { readFile, writeFile } from 'node:fs/promises'
import { basename, extname } from 'node:path'
import { Command } from 'commander'
import type { AttachmentView } from '../api/views.js'
import { createContext } from '../context.js'
import type { GlobalOptions } from '../context.js'
import { CliError, ExitCode } from '../errors.js'
import { print, printJson } from '../output.js'
import { projectKeyOf } from '../resolve.js'

/** Image types by extension; anything else goes as octet-stream and the server decides. */
const imageTypes: Record<string, string> = {
  '.png': 'image/png',
  '.jpg': 'image/jpeg',
  '.jpeg': 'image/jpeg',
  '.gif': 'image/gif',
  '.webp': 'image/webp',
}

/** Attachment bytes are intentionally written verbatim: agents may fetch screenshots, logs,
 * PDFs or archives, and formatting them would corrupt every type except text. */
export function attachmentCommand(globals: () => GlobalOptions): Command {
  const attachment = new Command('attachment').description('Download or upload attachments')
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

  // Uploads stay pending until a comment claims them (`item comment --attachment`, or the
  // MCP add_comment tool's attachmentIds); the server sweeps unclaimed ones after an hour.
  attachment
    .command('upload')
    .description('Upload files as pending attachments in an item\'s project; prints each id and its Markdown')
    .argument('<files...>', 'Files to upload')
    .requiredOption('--item <key>', 'Item key whose project receives the files, e.g. ACME-123')
    .action(async (files: string[], options: { item: string }) => {
      const ctx = createContext(globals())
      const orgSlug = await ctx.org()
      const projectKey = projectKeyOf(options.item)
      const uploaded: Array<AttachmentView & { markdown: string }> = []
      for (const file of files) {
        let bytes: Buffer
        try {
          bytes = await readFile(file)
        } catch (error) {
          throw new CliError(`Cannot read ${file}: ${error instanceof Error ? error.message : String(error)}`, ExitCode.Validation)
        }
        const form = new FormData()
        const type = imageTypes[extname(file).toLowerCase()] ?? 'application/octet-stream'
        form.append('file', new Blob([new Uint8Array(bytes)], { type }), basename(file))
        const view = await ctx.client.upload<AttachmentView, '/api/v1/orgs/{orgSlug}/projects/{projectKey}/attachments'>(
          '/api/v1/orgs/{orgSlug}/projects/{projectKey}/attachments',
          form,
          { path: { orgSlug, projectKey } },
        )
        const url = `/api/v1/orgs/${encodeURIComponent(orgSlug)}/attachments/${view.id}/download`
        const label = view.fileName.replace(/[[\]]/g, '')
        const markdown = view.contentType.startsWith('image/') ? `![${label}](${url})` : `[${label}](${url})`
        uploaded.push({ ...view, markdown })
      }
      if (ctx.json) return printJson(uploaded)
      for (const view of uploaded) print(`${view.id}  ${view.markdown}`)
    })
  return attachment
}
