import { createInterface } from 'node:readline'

/**
 * Reads a secret from the terminal without echoing it, or from a pipe when stdin is not a
 * TTY (`echo $TOKEN | aictiq auth login`), which is how CI supplies one without putting
 * the token on a command line that `ps` would show.
 */
export async function promptSecret(question: string): Promise<string> {
  if (!process.stdin.isTTY) {
    const chunks: Buffer[] = []
    for await (const chunk of process.stdin) chunks.push(Buffer.from(chunk))
    return Buffer.concat(chunks).toString('utf8').trim()
  }

  const rl = createInterface({ input: process.stdin, output: process.stdout, terminal: true })
  try {
    process.stdout.write(question)
    const muted = rl as unknown as { output: NodeJS.WriteStream; _writeToOutput?: unknown }
    const original = muted.output.write.bind(muted.output)
    muted.output.write = (() => true) as typeof muted.output.write
    const answer = await new Promise<string>((resolve) => rl.question('', resolve))
    muted.output.write = original
    process.stdout.write('\n')
    return answer.trim()
  } finally {
    rl.close()
  }
}
