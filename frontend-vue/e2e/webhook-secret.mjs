/**
 * Fixture-backed webhook secret regression on insecure HTTP and secure localhost.
 * Start Vite, then run PLAYWRIGHT_MODULE=/path/to/playwright/index.mjs
 * node --test e2e/webhook-secret.mjs. No live backend or account is used.
 * Optional: E2E_SERVER_URL, CHROMIUM_PATH, E2E_ARTIFACT_DIR.
 * Screenshots contain a placeholder and are saved only after all checks pass.
 */
/* global window, navigator, DOMException, console */
import assert from 'node:assert/strict'
import { mkdir, writeFile } from 'node:fs/promises'
import { resolve } from 'node:path'
import process from 'node:process'
import test from 'node:test'
import { URL } from 'node:url'

const { chromium } = await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright')
const serverURL = process.env.E2E_SERVER_URL ?? 'http://127.0.0.1:5173'
const artifactDirectory = resolve(
    process.env.E2E_ARTIFACT_DIR ?? 'node_modules/.artifacts/AICTIQ-137',
)
const now = '2026-11-16T12:00:00Z'
const fixtureSecret = 'nonfunctional-fixture-signing-secret-'.repeat(4)
const manualCopy = 'Could not copy - select the secret and copy it manually.'
const org = {
    id: 'org-fixture',
    slug: 'fixture',
    name: 'Fixture Studio',
    role: 'owner',
    canOperateFactory: true,
}

async function screenshotWithPlaceholder(page, code) {
    // Keep even the nonfunctional fixture value out of uploaded visual proof.
    await code.evaluate((element) => {
        element.textContent = '[fixture signing secret]'
    })
    try {
        return await page.screenshot({ animations: 'disabled', fullPage: true })
    } finally {
        await code.evaluate((element, value) => {
            element.textContent = value
        }, fixtureSecret)
    }
}

test(
    'webhook secrets remain recoverable when clipboard access is missing or denied',
    { timeout: 120000 },
    async () => {
        assert.ok(
            ['127.0.0.1', 'localhost', '[::1]'].includes(new URL(serverURL).hostname),
            'Vite must be local',
        )
        const browser = await chromium.launch({
            executablePath: process.env.CHROMIUM_PATH ?? '/usr/bin/chromium',
            headless: true,
            args: ['--no-sandbox'],
        })
        const screenshots = []
        try {
            for (const scenario of [
                { name: 'http-desktop', insecure: true, width: 1440, height: 1000 },
                { name: 'http-mobile', insecure: true, width: 390, height: 844 },
                { name: 'clipboard-permission', insecure: false, width: 1440, height: 1000 },
            ]) {
                // The synthetic LAN-style hostname is an actual insecure browser context;
                // its static requests are intercepted and fetched only from local Vite.
                const baseURL = scenario.insecure ? 'http://aictiq.test:5173' : serverURL
                const context = await browser.newContext({
                    baseURL,
                    viewport: { width: scenario.width, height: scenario.height },
                    locale: 'en-US',
                    timezoneId: 'UTC',
                })
                context.setDefaultTimeout(10000)
                const errors = []
                const unexpected = []
                const writes = []
                const hooks = []
                await context.addInitScript(
                    ({ insecure }) => {
                        window.localStorage.setItem(
                            'aictiq.ui',
                            JSON.stringify({
                                theme: 'light',
                                density: 'comfortable',
                                sidebarCollapsed: false,
                            }),
                        )
                        window.fixtureClipboard = []
                        window.fixtureClipboardDenied = true
                        if (!insecure)
                            Object.defineProperty(navigator, 'clipboard', {
                                value: {
                                    writeText: async (value) => {
                                        window.fixtureClipboard.push(value)
                                        if (window.fixtureClipboardDenied)
                                            throw new DOMException(
                                                'Permission denied',
                                                'NotAllowedError',
                                            )
                                    },
                                },
                            })
                    },
                    { insecure: scenario.insecure },
                )
                await context.routeWebSocket(/.*/, (socket) => {
                    const url = new URL(socket.url())
                    if (url.host !== new URL(baseURL).host || !url.pathname.startsWith('/hubs/'))
                        return socket.close()
                    socket.onMessage((message) => {
                        for (const part of String(message).split('\x1e').filter(Boolean)) {
                            const value = JSON.parse(part)
                            if (value.protocol) socket.send('{}\x1e')
                            else if (value.type === 1 && value.invocationId)
                                socket.send(
                                    JSON.stringify({ type: 3, invocationId: value.invocationId }) +
                                        '\x1e',
                                )
                        }
                    })
                })
                await context.route('**/*', async (route) => {
                    const request = route.request()
                    const url = new URL(request.url())
                    if (url.origin !== new URL(baseURL).origin) {
                        unexpected.push(`${request.method()} ${url.origin}${url.pathname}`)
                        return route.abort()
                    }
                    const json = (body, status = 200) =>
                        route.fulfill({
                            status,
                            contentType: 'application/json',
                            body: JSON.stringify(body),
                        })
                    if (url.pathname.startsWith('/hubs/'))
                        return json({
                            negotiateVersion: 1,
                            connectionId: 'fixture',
                            connectionToken: 'fixture',
                            availableTransports: [
                                { transport: 'WebSockets', transferFormats: ['Text'] },
                            ],
                        })
                    if (!url.pathname.startsWith('/api/'))
                        return route.fulfill({
                            response: await route.fetch({
                                url: new URL(url.pathname + url.search, serverURL).toString(),
                                maxRedirects: 0,
                            }),
                        })
                    const path = url.pathname
                    if (path === '/api/v1/orgs/fixture/webhooks') {
                        if (request.method() === 'GET') return json(hooks)
                        if (request.method() === 'POST') {
                            const body = request.postDataJSON()
                            writes.push(body)
                            const subscription = {
                                id: `webhook-fixture-${writes.length}`,
                                projectId: null,
                                ...body,
                                active: true,
                                consecutiveFailures: 0,
                                createdAt: now,
                                updatedAt: now,
                            }
                            hooks.push(subscription)
                            return json({ subscription, secret: fixtureSecret })
                        }
                    }
                    if (path === '/api/v1/auth/session')
                        return json({
                            id: 'user-fixture',
                            email: 'reviewer@example.invalid',
                            firstName: 'Fixture',
                            lastName: 'Reviewer',
                            fullName: 'Fixture Reviewer',
                            roles: [],
                            isAgent: false,
                            avatarKey: null,
                            timeZone: 'UTC',
                            unreadCount: 0,
                        })
                    if (path === '/api/v1/meta')
                        return json({
                            version: 'fixture',
                            updateCheck: { enabled: false, repository: null },
                        })
                    if (path === '/api/v1/me/onboarding')
                        return json({
                            tourVersion: 1,
                            status: 'completed',
                            lastStepId: null,
                            completedAt: now,
                            updatedAt: now,
                            version: 1,
                        })
                    if (path === '/api/v1/me/notifications') return json([])
                    if (path === '/api/v1/orgs') return json([org])
                    if (path === '/api/v1/orgs/fixture') return json(org)
                    if (path.endsWith('/projects')) return json([])
                    if (
                        path === '/api/v1/orgs/fixture/github/installations' ||
                        path === '/api/v1/orgs/fixture/github/deliveries'
                    )
                        return json([])
                    if (path.endsWith('/fleet/status'))
                        return json({
                            availability: 'hidden',
                            enabled: false,
                            active: false,
                            downgraded: false,
                            knownTags: [],
                        })
                    if (path.endsWith('/billing/subscription'))
                        return json({
                            mode: 'self_hosted',
                            enabled: false,
                            plan: 'selfHosted',
                            status: 'none',
                            readOnly: false,
                            seats: { human: 1, agent: 0 },
                            plans: [],
                            evaluation: null,
                        })
                    unexpected.push(`${request.method()} ${path}`)
                    return json({}, 404)
                })
                const page = await context.newPage()
                page.on('pageerror', (error) => errors.push(error.message))
                await page.goto('/o/fixture/settings/integrations')
                await page
                    .getByRole('heading', { name: 'Outgoing webhooks', exact: true })
                    .waitFor()
                assert.deepEqual(
                    await page.evaluate(() => ({
                        secure: window.isSecureContext,
                        clipboard: typeof navigator.clipboard,
                    })),
                    scenario.insecure
                        ? { secure: false, clipboard: 'undefined' }
                        : { secure: true, clipboard: 'object' },
                )
                const create = page.getByRole('button', { name: 'Create', exact: true })
                await page
                    .getByPlaceholder('https://receiver.example/aictiq')
                    .fill('https://receiver.example.invalid/aictiq')
                await create.click()
                const panel = page.getByRole('status').filter({
                    hasText: 'Copy it now - this is the only time it is shown.',
                })
                const code = panel.locator('code')
                await code.waitFor()
                assert.equal(await code.textContent(), fixtureSecret)
                assert.equal(
                    await create.isDisabled(),
                    true,
                    'Another creation must not replace the secret',
                )
                assert.deepEqual(writes, [
                    { url: 'https://receiver.example.invalid/aictiq', events: ['item.updated'] },
                ])
                assert.deepEqual(
                    await page.evaluate(() => window.fixtureClipboard),
                    [],
                    'Creation must not attempt an automatic copy',
                )
                await page
                    .getByText('Webhook created. Copy its secret before leaving this page.', {
                        exact: true,
                    })
                    .waitFor()
                assert.equal(await page.getByText('Secret copied', { exact: true }).count(), 0)
                await panel
                    .getByRole('button', { name: 'Copy the webhook secret', exact: true })
                    .click()
                await page.getByText(manualCopy, { exact: true }).waitFor()
                assert.equal(await code.textContent(), fixtureSecret)
                assert.equal(await page.getByText('Secret copied', { exact: true }).count(), 0)
                await code.evaluate((element) => window.getSelection().selectAllChildren(element))
                assert.equal(
                    await page.evaluate(() => window.getSelection().toString()),
                    fixtureSecret,
                    'The entire secret must remain selectable for manual copying',
                )
                await page.evaluate(() => window.getSelection().removeAllRanges())
                const dimensions = await panel.evaluate((element) => {
                    const bounds = element.getBoundingClientRect()
                    return {
                        left: bounds.left,
                        right: bounds.right,
                        viewport: window.innerWidth,
                        width: element.clientWidth,
                        scrollWidth: element.scrollWidth,
                    }
                })
                assert.ok(
                    dimensions.left >= 0 && dimensions.right <= dimensions.viewport + 1,
                    `Secret panel exceeds viewport: ${JSON.stringify(dimensions)}`,
                )
                assert.ok(
                    dimensions.scrollWidth <= dimensions.width + 1,
                    `Secret panel overflows: ${JSON.stringify(dimensions)}`,
                )
                if (!scenario.insecure) {
                    assert.deepEqual(await page.evaluate(() => window.fixtureClipboard), [
                        fixtureSecret,
                    ])
                    await page.evaluate(() => {
                        window.fixtureClipboardDenied = false
                    })
                    await panel
                        .getByRole('button', { name: 'Copy the webhook secret', exact: true })
                        .click()
                    await page.getByText('Secret copied', { exact: true }).waitFor()
                    assert.deepEqual(await page.evaluate(() => window.fixtureClipboard), [
                        fixtureSecret,
                        fixtureSecret,
                    ])
                    assert.equal(await code.textContent(), fixtureSecret)
                }
                screenshots.push([
                    `${scenario.name}.png`,
                    await screenshotWithPlaceholder(page, code),
                ])
                await panel.getByRole('button', { name: 'Done', exact: true }).click()
                await panel.waitFor({ state: 'hidden' })
                assert.equal(await create.isEnabled(), true)
                assert.equal(await page.getByText(fixtureSecret, { exact: true }).count(), 0)
                await page.reload()
                await page
                    .getByRole('heading', { name: 'Outgoing webhooks', exact: true })
                    .waitFor()
                await page.getByRole('button', { name: 'Delete', exact: true }).waitFor()
                assert.equal(
                    await panel.count(),
                    0,
                    'Reload must never reveal a saved webhook secret',
                )
                assert.equal(await page.getByText(fixtureSecret, { exact: true }).count(), 0)
                assert.equal(writes.length, 1)
                assert.deepEqual(errors, [])
                assert.deepEqual(unexpected, [])
                await context.close()
            }
            await mkdir(artifactDirectory, { recursive: true })
            for (const [name, bytes] of screenshots)
                await writeFile(resolve(artifactDirectory, name), bytes)
            console.log(
                `PASS missing/denied/successful clipboard, manual selection, mobile layout, dismissal and reload; ${screenshots.length} fixture screenshots`,
            )
        } finally {
            await browser.close()
        }
    },
)
