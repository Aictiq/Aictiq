// Installs the Aictiq skill for coding harnesses after `npm install -g @aictiq/cli`. It must
// never fail the install: a source checkout has no `dist/` yet, and anything else that goes
// wrong is only a warning (`aictiq skills install` does the same by hand).
try {
    await import('../dist/postinstall.js')
} catch (error) {
    if (error?.code !== 'ERR_MODULE_NOT_FOUND') {
        process.stderr.write(
            `aictiq: Warning: could not install the Aictiq skill (${error?.message ?? error})\n`,
        )
    }
}
