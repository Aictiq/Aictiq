/**
 * Fixture-backed browser regression for invitation result layout. Start Vite, then run:
 * PLAYWRIGHT_MODULE=/path/to/playwright/index.mjs node --test e2e/invite-members.mjs
 * Optional: E2E_BASE_URL, CHROMIUM_PATH, E2E_ARTIFACT_DIR.
 * No live account or backend is used; screenshots are saved only after all checks pass.
 */
/* global window, document, navigator, getComputedStyle */
import assert from 'node:assert/strict'
import { mkdir, writeFile } from 'node:fs/promises'
import { resolve } from 'node:path'
import process from 'node:process'
import test from 'node:test'
import { URL } from 'node:url'

const { chromium } = await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright')
const baseURL = process.env.E2E_BASE_URL ?? 'http://127.0.0.1:5173'
const artifactDirectory = resolve(process.env.E2E_ARTIFACT_DIR ?? '../.artifacts/AICTIQ-118')
const now = '2026-10-30T12:00:00Z'
const org = {
    id: 'org-fixture',
    slug: 'fixture',
    name: 'Fixture Studio',
    role: 'owner',
    canOperateFactory: true,
}

async function assertContained(dialog) {
    const dimensions = await dialog.evaluate((element) => {
        const bounds = element.getBoundingClientRect()
        const children = [
            ...element.querySelectorAll('form, fieldset, textarea, select, button, h3'),
        ]
        return {
            left: bounds.left,
            right: bounds.right,
            top: bounds.top,
            bottom: bounds.bottom,
            viewportWidth: window.innerWidth,
            viewportHeight: window.innerHeight,
            margin: parseFloat(getComputedStyle(document.documentElement).fontSize) - 1,
            width: element.clientWidth,
            scrollWidth: element.scrollWidth,
            escaped: children
                .filter((child) => {
                    const rect = child.getBoundingClientRect()
                    return rect.left < bounds.left - 1 || rect.right > bounds.right + 1
                })
                .map((child) => child.tagName),
        }
    })
    assert.ok(
        dimensions.left >= dimensions.margin &&
            dimensions.right <= dimensions.viewportWidth - dimensions.margin,
        `Dialog exceeds viewport width: ${JSON.stringify(dimensions)}`,
    )
    assert.ok(
        dimensions.top >= dimensions.margin &&
            dimensions.bottom <= dimensions.viewportHeight - dimensions.margin,
        `Dialog exceeds viewport height: ${JSON.stringify(dimensions)}`,
    )
    assert.ok(
        dimensions.scrollWidth <= dimensions.width + 1,
        `Dialog has horizontal overflow: ${JSON.stringify(dimensions)}`,
    )
    assert.deepEqual(dimensions.escaped, [], 'Controls must stay inside the dialog background')
}

test(
    'invitation links stay inside one dialog and large batches remain reachable',
    { timeout: 120000 },
    async () => {
        const browser = await chromium.launch({
            executablePath: process.env.CHROMIUM_PATH ?? '/usr/bin/chromium',
            headless: true,
            args: ['--no-sandbox'],
        })
        const screenshots = []
        try {
            for (const scenario of [
                { name: 'desktop-email', width: 1440, height: 1100, count: 2, emailSent: true },
                { name: 'desktop-share', width: 1440, height: 900, count: 2, emailSent: false },
                { name: 'mobile-email', width: 390, height: 844, count: 2, emailSent: true },
                {
                    name: 'short-batch',
                    width: 390,
                    height: 600,
                    count: 12,
                    emailSent: false,
                    failure: true,
                },
            ]) {
                const context = await browser.newContext({
                    baseURL,
                    viewport: { width: scenario.width, height: scenario.height },
                    colorScheme: 'dark',
                })
                context.setDefaultTimeout(10000)
                const errors = []
                const unexpected = []
                const writes = []
                const invitations = []
                await context.addInitScript(() => {
                    window.fixtureClipboard = []
                    Object.defineProperty(navigator, 'clipboard', {
                        value: {
                            writeText: async (value) => window.fixtureClipboard.push(value),
                        },
                    })
                })
                await context.route('**/*', async (route) => {
                    const request = route.request()
                    const url = new URL(request.url())
                    if (url.origin !== new URL(baseURL).origin) return route.abort()
                    if (url.pathname.startsWith('/hubs/')) return route.fulfill({ status: 404 })
                    if (!url.pathname.startsWith('/api/')) return route.continue()
                    const path = url.pathname
                    const json = (body, status = 200) =>
                        route.fulfill({
                            status,
                            contentType: 'application/json',
                            body: JSON.stringify(body),
                        })
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
                    if (path === '/api/v1/orgs/fixture/invitations') {
                        if (request.method() === 'GET') return json(invitations)
                        if (request.method() === 'POST') {
                            const body = request.postDataJSON()
                            writes.push(body)
                            if (body.email.startsWith('refused'))
                                return json(
                                    {
                                        title: 'This fixture address is already a member.',
                                        status: 409,
                                    },
                                    409,
                                )
                            const invitation = {
                                id: `invitation-${writes.length}`,
                                email: body.email,
                                role: body.role,
                                canOperateFactory: body.canOperateFactory,
                                invitedByName: 'Fixture Reviewer',
                                createdAt: now,
                                expiresAt: '2026-11-06T12:00:00Z',
                                status: 'pending',
                                projectId: null,
                                projectKey: null,
                                projectRole: null,
                            }
                            invitations.push(invitation)
                            return json({
                                invitation,
                                token: 'nonfunctional-fixture',
                                emailSent: scenario.emailSent,
                                acceptUrl: `${baseURL}/invite/nonfunctional-fixture-${'x'.repeat(64)}-${writes.length}`,
                            })
                        }
                    }
                    unexpected.push(`${request.method()} ${path}`)
                    return json({}, 404)
                })
                const page = await context.newPage()
                page.on('pageerror', (error) => errors.push(error.message))
                await page.goto('/o/fixture/settings/invitations')
                await page
                    .getByRole('button', { name: 'Invite people', exact: true })
                    .first()
                    .click()
                const dialog = page.getByRole('dialog', { name: 'Invite people', exact: true })
                await dialog.waitFor()
                // Wait for the opening animation before measuring its bounds.
                await dialog.evaluate((element) =>
                    Promise.all(element.getAnimations().map((a) => a.finished)),
                )
                await assertContained(dialog)
                const emails = Array.from(
                    { length: scenario.count },
                    (_, i) => `person-${i + 1}@example.invalid`,
                )
                if (scenario.failure)
                    emails.splice(1, 0, `refused-${'a'.repeat(50)}@example.invalid`)
                await dialog.getByLabel('Email addresses').fill(emails.join(', '))
                await dialog.getByRole('button', { name: /^Send / }).click()
                await dialog.getByRole('button', { name: 'Done', exact: true }).waitFor()
                // Done appears as soon as the first result arrives; input clears after the full batch.
                await page.waitForFunction(
                    () => document.querySelector('#invite-emails')?.value === '',
                )
                assert.equal(writes.length, emails.length)
                assert.deepEqual(
                    writes.map((body) => body.email),
                    emails,
                )
                assert.ok(writes.every((body) => body.role === 'member' && body.canOperateFactory))
                assert.equal(await page.getByRole('dialog').count(), 1)
                assert.equal(
                    await dialog.getByRole('button', { name: /^Copy the link for / }).count(),
                    scenario.count,
                )
                await dialog
                    .getByRole('heading', {
                        name: scenario.emailSent ? 'Invitation links' : 'Share these links',
                        exact: true,
                    })
                    .waitFor()
                if (scenario.failure)
                    await dialog.getByText('This fixture address is already a member.').waitFor()
                await assertContained(dialog)
                const copy = dialog.getByRole('button', {
                    name: `Copy the link for ${emails[0]}`,
                    exact: true,
                })
                await copy.click()
                assert.deepEqual(await page.evaluate(() => window.fixtureClipboard), [
                    `${baseURL}/invite/nonfunctional-fixture-${'x'.repeat(64)}-1`,
                ])
                await dialog
                    .getByRole('button', { name: 'Done', exact: true })
                    .scrollIntoViewIfNeeded()
                const doneBox = await dialog
                    .getByRole('button', { name: 'Done', exact: true })
                    .boundingBox()
                assert.ok(doneBox.y >= 0 && doneBox.y + doneBox.height <= scenario.height)
                // Let the confirmation toast expire so it does not cover the footer in the proof.
                await page.mouse.move(0, 0)
                await page
                    .locator('[data-sonner-toast]')
                    .waitFor({ state: 'hidden', timeout: 15000 })
                if (scenario.name !== 'desktop-share')
                    screenshots.push([
                        `${scenario.name}.png`,
                        await page.screenshot({ animations: 'disabled' }),
                    ])
                await dialog.getByRole('button', { name: 'Done', exact: true }).click()
                await dialog.waitFor({ state: 'hidden' })
                await page
                    .getByRole('button', { name: 'Invite people', exact: true })
                    .first()
                    .click()
                assert.equal(
                    await dialog.getByRole('button', { name: /^Copy the link for / }).count(),
                    0,
                )
                assert.equal(await dialog.getByLabel('Email addresses').inputValue(), '')
                await dialog.getByRole('button', { name: 'Cancel', exact: true }).click()
                assert.deepEqual(errors, [])
                assert.deepEqual(unexpected, [])
                await context.close()
            }
            await mkdir(artifactDirectory, { recursive: true })
            for (const [name, bytes] of screenshots)
                await writeFile(resolve(artifactDirectory, name), bytes)
        } finally {
            await browser.close()
        }
    },
)
