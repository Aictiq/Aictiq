/* global window */
/**
 * Fixture-backed browser regression for the Jira import wizard. Start Vite, then run:
 * PLAYWRIGHT_MODULE=/path/to/playwright/index.mjs node --test e2e/jira-import.mjs
 * Optional: E2E_BASE_URL, CHROMIUM_PATH, E2E_ARTIFACT_DIR.
 * No live account, backend or Jira site is used; screenshots are saved only after all checks pass.
 */
import assert from 'node:assert/strict'
import { mkdir, writeFile } from 'node:fs/promises'
import { resolve } from 'node:path'
import process from 'node:process'
import test from 'node:test'
import { URL } from 'node:url'

const { chromium } = await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright')
const baseURL = process.env.E2E_BASE_URL ?? 'http://127.0.0.1:5173'
const artifactDirectory = resolve(process.env.E2E_ARTIFACT_DIR ?? '../.artifacts/AICTIQ-2')
const now = '2026-10-31T12:00:00Z'
const org = {
    id: 'org-fixture',
    slug: 'fixture',
    name: 'Fixture Studio',
    role: 'member',
    canOperateFactory: false,
}
const project = (role, isArchived = false) => ({
    id: 'project-fixture',
    key: 'MIG',
    name: 'Migration',
    description: null,
    visibility: 'organization',
    icon: null,
    color: null,
    isArchived,
    createdAt: now,
    role,
    version: 1,
})
const states = [
    { id: 'state-new', name: 'New', category: 'proposed' },
    { id: 'state-active', name: 'Active', category: 'inProgress' },
    { id: 'state-done', name: 'Done', category: 'completed' },
]
const job = (overrides) => ({
    id: 'job-1',
    jiraProjectKey: 'SHOP',
    jiraProjectName: 'Web shop',
    siteUrl: 'https://fixture.atlassian.net',
    status: 'running',
    totalIssues: 4,
    processedIssues: 2,
    createdItems: 2,
    skippedIssues: 0,
    createdComments: 3,
    createdAttachments: 1,
    createdSprints: 1,
    failures: [],
    createdAt: now,
    startedAt: now,
    completedAt: null,
    ...overrides,
})

async function newContext(browser, role, writes, unexpected, { conflict = false } = {}) {
    const context = await browser.newContext({
        baseURL,
        viewport: { width: 1440, height: 1000 },
        colorScheme: 'dark',
    })
    context.setDefaultTimeout(10000)
    let polls = 0
    let conflicted = false
    await context.route('**/*', async (route) => {
        const request = route.request()
        const url = new URL(request.url())
        if (url.origin !== new URL(baseURL).origin) return route.abort()
        if (url.pathname.startsWith('/hubs/')) return route.fulfill({ status: 404 })
        if (!url.pathname.startsWith('/api/')) return route.continue()
        const path = url.pathname
        const method = request.method()
        const json = (body, status = 200) =>
            route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
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
            return json({ version: 'fixture', updateCheck: { enabled: false, repository: null } })
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
        if (path === '/api/v1/orgs/fixture/projects') return json([project(role)])
        if (path === '/api/v1/orgs/fixture/projects/MIG') return json(project(role))
        if (path.endsWith('/teams')) return json([])
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
        const base = '/api/v1/orgs/fixture/projects/MIG'
        if (path === `${base}/imports/jira` && method === 'GET')
            return json(
                conflicted ? [job({ status: 'pending', processedIssues: 0, createdItems: 0 })] : [],
            )
        if (path === `${base}/import/jira/connect` && method === 'POST') {
            const body = request.postDataJSON()
            writes.push({ path: 'connect', body })
            if (body.apiToken !== 'fixture-good-token')
                return json(
                    {
                        title: 'One or more validation errors occurred.',
                        status: 422,
                        errors: { apiToken: ['Jira rejected this email and API token.'] },
                    },
                    422,
                )
            return json({
                siteUrl: 'https://fixture.atlassian.net',
                accountName: 'Fixture Reviewer',
                projects: [
                    { id: '10000', key: 'SHOP', name: 'Web shop' },
                    { id: '10001', key: 'OPS', name: 'Operations' },
                ],
            })
        }
        if (path === `${base}/import/jira/preview` && method === 'POST') {
            writes.push({ path: 'preview', body: request.postDataJSON() })
            return json({
                jiraProject: { id: '10000', key: 'SHOP', name: 'Web shop' },
                issueCount: 5,
                alreadyImported: 1,
                issueTypes: [
                    { name: 'Epic', suggestedType: 'epic' },
                    { name: 'Story', suggestedType: 'story' },
                    { name: 'Sub-task', suggestedType: 'task' },
                    { name: 'Spike', suggestedType: 'task' },
                ],
                statuses: [
                    { name: 'To Do', category: 'new', suggestedStateId: 'state-new' },
                    {
                        name: 'In Review',
                        category: 'indeterminate',
                        suggestedStateId: 'state-active',
                    },
                    { name: 'Done', category: 'done', suggestedStateId: 'state-done' },
                ],
                users: [
                    {
                        accountId: 'acc-1',
                        displayName: 'Ana Fixture',
                        matchedUserId: 'user-ana',
                        matchedDisplayName: 'Ana Fixture',
                    },
                    {
                        accountId: 'acc-2',
                        displayName: 'Former Contractor',
                        matchedUserId: null,
                        matchedDisplayName: null,
                    },
                ],
                states,
            })
        }
        if (path === `${base}/import/jira` && method === 'POST') {
            writes.push({ path: 'start', body: request.postDataJSON() })
            if (conflict) {
                conflicted = true
                return json(
                    {
                        title: 'An import is already running.',
                        detail: 'An import from Jira is already running for this project.',
                        status: 409,
                    },
                    409,
                )
            }
            return json(job({ status: 'pending', processedIssues: 0, createdItems: 0 }), 202)
        }
        if (path === `${base}/imports/jira/job-1`) {
            polls += 1
            if (polls === 1) return json(job({}))
            return json(
                job({
                    status: 'completed',
                    processedIssues: 4,
                    createdItems: 4,
                    skippedIssues: 1,
                    createdComments: 6,
                    createdAttachments: 2,
                    createdSprints: 2,
                    failures: [
                        {
                            issueKey: 'SHOP-7',
                            kind: 'attachment',
                            reason: 'design.psd is 40 MB, over the 25 MB attachment limit.',
                        },
                    ],
                    completedAt: now,
                }),
            )
        }
        unexpected.push(`${method} ${path}`)
        return json({}, 404)
    })
    return context
}

test('a member connects, maps and imports a Jira project', { timeout: 120000 }, async () => {
    const browser = await chromium.launch({
        executablePath: process.env.CHROMIUM_PATH ?? '/usr/bin/chromium',
        headless: true,
        args: ['--no-sandbox'],
    })
    const screenshots = []
    try {
        const writes = []
        const unexpected = []
        const errors = []
        const context = await newContext(browser, 'member', writes, unexpected)
        const page = await context.newPage()
        page.on('pageerror', (error) => errors.push(error.message))
        await page.goto('/o/fixture/p/MIG/settings/import')
        await page.getByRole('heading', { name: 'Import from Jira' }).waitFor()

        // A member sees only this tab, and the sidebar's Settings link leads here.
        const tabs = await page
            .locator('a[href^="/o/fixture/p/MIG/settings/"]')
            .evaluateAll((links) => [...new Set(links.map((link) => link.getAttribute('href')))])
        assert.deepEqual(tabs, ['/o/fixture/p/MIG/settings/import'])

        // A wrong token is reported on its field and nothing moves on.
        await page.getByLabel('Jira site URL').fill('fixture.atlassian.net')
        await page.getByLabel('Account email').fill('reviewer@example.invalid')
        await page.locator('#jira-token').fill('fixture-bad-token')
        await page.getByRole('button', { name: 'Connect', exact: true }).click()
        await page.getByText('Jira rejected this email and API token.').waitFor()
        assert.equal(await page.getByRole('button', { name: 'Select' }).count(), 0)
        screenshots.push(['connect-error.png', await page.screenshot({ animations: 'disabled' })])

        await page.locator('#jira-token').fill('fixture-good-token')
        await page.getByRole('button', { name: 'Connect', exact: true }).click()
        await page.getByText('https://fixture.atlassian.net').waitFor()
        await page.getByRole('button', { name: 'Select' }).first().click()

        // Preview: issue count, the skip note, pre-filled mappings and the unmatched person.
        await page.getByTestId('jira-issue-count').getByText('already imported').waitFor()
        assert.match(await page.getByTestId('jira-issue-count').innerText(), /5\s+issues/)
        assert.equal(await page.getByLabel('Aictiq type for Epic').inputValue(), 'epic')
        assert.equal(
            await page.getByLabel('Workflow state for In Review').inputValue(),
            'state-active',
        )
        await page.getByText('Not a member - left unassigned, name kept on the item').waitFor()
        await page.getByLabel('Aictiq type for Spike').selectOption('story')
        await page.getByLabel('Workflow state for In Review').selectOption('state-done')
        screenshots.push([
            'mapping.png',
            await page.screenshot({ animations: 'disabled', fullPage: true }),
        ])

        await page.getByRole('button', { name: 'Import 4 issues' }).click()
        await page.getByTestId('jira-job').waitFor()
        await page.getByText('Finished', { exact: true }).first().waitFor({ timeout: 15000 })
        await page
            .getByTestId('jira-failures')
            .getByText('over the 25 MB attachment limit')
            .waitFor()
        assert.equal(await page.getByRole('progressbar').getAttribute('aria-valuenow'), '100')

        const start = writes.find((write) => write.path === 'start').body
        assert.equal(start.siteUrl, 'https://fixture.atlassian.net')
        assert.equal(start.jiraProjectKey, 'SHOP')
        assert.equal(start.issueTypes.Spike, 'story')
        assert.equal(start.statuses['In Review'], 'state-done')
        assert.equal(start.apiToken, 'fixture-good-token')
        // The token never reaches browser storage.
        const stored = await page.evaluate(() =>
            JSON.stringify({ ...window.localStorage, ...window.sessionStorage }),
        )
        assert.ok(!stored.includes('fixture-good-token'))
        await page.mouse.move(0, 0)
        screenshots.push([
            'result.png',
            await page.screenshot({ animations: 'disabled', fullPage: true }),
        ])
        assert.deepEqual(errors, [])
        assert.deepEqual(unexpected, [])
        await context.close()

        // A second import while one runs is refused; the page shows the running one instead.
        const conflictUnexpected = []
        const conflictErrors = []
        const busy = await newContext(browser, 'member', [], conflictUnexpected, { conflict: true })
        const busyPage = await busy.newPage()
        busyPage.on('pageerror', (error) => conflictErrors.push(error.message))
        await busyPage.goto('/o/fixture/p/MIG/settings/import')
        await busyPage.getByLabel('Jira site URL').fill('https://fixture.atlassian.net')
        await busyPage.getByLabel('Account email').fill('reviewer@example.invalid')
        await busyPage.locator('#jira-token').fill('fixture-good-token')
        await busyPage.getByRole('button', { name: 'Connect', exact: true }).click()
        await busyPage.getByRole('button', { name: 'Select' }).first().click()
        await busyPage.getByRole('button', { name: 'Import 4 issues' }).click()
        await busyPage
            .getByText('An import from Jira is already running for this project.')
            .waitFor()
        await busyPage.getByTestId('jira-job').waitFor()
        await busyPage.getByText('Finished', { exact: true }).first().waitFor({ timeout: 15000 })
        assert.deepEqual(conflictErrors, [])
        assert.deepEqual(conflictUnexpected, [])
        await busy.close()

        // A guest gets no settings tabs and the import explains why it is unavailable.
        const guestUnexpected = []
        const guest = await newContext(browser, 'guest', [], guestUnexpected)
        const guestPage = await guest.newPage()
        await guestPage.goto('/o/fixture/p/MIG/settings/import')
        await guestPage.getByTestId('jira-import-disabled').waitFor()
        assert.equal(await guestPage.getByLabel('Jira site URL').count(), 0)
        assert.equal(await guestPage.locator('a[href^="/o/fixture/p/MIG/settings/"]').count(), 0)
        screenshots.push(['guest.png', await guestPage.screenshot({ animations: 'disabled' })])
        assert.deepEqual(guestUnexpected, [])
        await guest.close()

        await mkdir(artifactDirectory, { recursive: true })
        for (const [name, bytes] of screenshots)
            await writeFile(resolve(artifactDirectory, name), bytes)
    } finally {
        await browser.close()
    }
})
