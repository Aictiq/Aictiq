/**
 * Fixture-backed browser integration test of the real Vue app (no live backend).
 * Start Vite, then run:
 * PLAYWRIGHT_MODULE=/path/to/playwright/index.mjs node --test e2e/item-runs-visibility.mjs
 * Optional: E2E_BASE_URL, CHROMIUM_PATH, E2E_ARTIFACT_DIR.
 * Screenshots are saved only after all assertions pass.
 */
import assert from 'node:assert/strict'
import console from 'node:console'
import { mkdir, writeFile } from 'node:fs/promises'
import { resolve } from 'node:path'
import process from 'node:process'
import test from 'node:test'
import { URL } from 'node:url'

const { chromium } = await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright')
const baseURL = process.env.E2E_BASE_URL ?? 'http://127.0.0.1:5173'
const artifactDirectory = resolve(process.env.E2E_ARTIFACT_DIR ?? '.artifacts/AICTIQ-105')
const now = new Date().toISOString()
const paged = (items = []) => ({ items, page: 1, pageSize: 50, totalCount: items.length })
const item = {
    id: 'item-fixture',
    key: 'DEMO-1',
    type: 'story',
    title: 'Review the release checklist',
    stateId: 'state-fixture',
    stateCategory: 'active',
    priority: 'medium',
    descriptionMarkdown: 'Confirm the release date and review the announcement.',
    descriptionHtml: '',
    assigneeId: null,
    teamId: null,
    sprintId: null,
    parentId: null,
    points: null,
    estimateHours: null,
    remainingHours: null,
    completedHours: null,
    dueDate: null,
    version: 1,
    labels: [],
    updatedAt: now,
    isWatching: false,
    watcherCount: 0,
    claimedBy: null,
    claimedAt: null,
    claimHeartbeatAt: null,
    rollup: {
        totalCount: 0,
        completedCount: 0,
        pointsTotal: 0,
        pointsCompleted: 0,
        remainingHours: 0,
    },
}
const run = {
    id: 'run-fixture',
    projectId: 'project-fixture',
    itemId: item.id,
    itemKey: item.key,
    playbookId: 'playbook-fixture',
    playbookName: 'Implement checklist',
    agentId: 'agent-fixture',
    agentName: 'Release assistant',
    requestedBy: 'user-fixture',
    runnerId: 'runner-fixture',
    runnerName: 'Fixture build machine',
    status: 'succeeded',
    harness: 'codex',
    maxMinutes: 60,
    queuedAt: now,
    startedAt: now,
    finishedAt: now,
    cancelRequested: false,
    pullRequestUrl: 'https://github.com/Aictiq/Aictiq/pull/1',
    sessionId: 'fixture-session',
    workspacePath: '/fixture/repo',
    version: 1,
}

test(
    'ItemDetail factory visibility for stakeholders, guests and operators',
    { timeout: 120000 },
    async () => {
        const browser = await chromium.launch({
            executablePath: process.env.CHROMIUM_PATH ?? '/usr/bin/chromium',
            headless: true,
            args: ['--no-sandbox'],
        })
        const screenshots = []
        try {
            for (const [role, canOperateFactory] of [
                ['member', false],
                ['guest', false],
                ['member', true],
            ]) {
                const context = await browser.newContext({
                    baseURL,
                    viewport: { width: 1440, height: 1100 },
                })
                context.setDefaultTimeout(10000)
                const requests = []
                const unexpected = []
                const errors = []
                const org = {
                    id: 'org-fixture',
                    slug: 'fixture',
                    name: 'Fixture Studio',
                    role,
                    canOperateFactory,
                }
                const project = {
                    id: 'project-fixture',
                    key: 'DEMO',
                    name: 'Demo product',
                    description: null,
                    visibility: 'private',
                    icon: null,
                    color: null,
                    isArchived: false,
                    createdAt: now,
                    role,
                    version: 1,
                }
                await context.route('**/*', async (route) => {
                    const url = new URL(route.request().url())
                    if (url.origin !== new URL(baseURL).origin) return route.abort()
                    if (url.pathname.startsWith('/hubs/')) return route.fulfill({ status: 404 })
                    if (!url.pathname.startsWith('/api/')) return route.continue()
                    const path = url.pathname.replace(/\/$/, '')
                    requests.push(path)
                    const json = (body) =>
                        route.fulfill({
                            status: 200,
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
                    if (path === '/api/v1/me/notifications') return json([])
                    if (path === '/api/v1/me/onboarding')
                        return json({
                            tourVersion: 1,
                            status: 'completed',
                            lastStepId: null,
                            completedAt: now,
                            updatedAt: now,
                            version: 1,
                        })
                    if (path === '/api/v1/orgs') return json([org])
                    if (path === '/api/v1/orgs/fixture') return json(org)
                    if (path.endsWith('/projects')) return json([project])
                    if (path.endsWith('/projects/DEMO')) return json(project)
                    if (path.endsWith('/items/DEMO-1')) return json(item)
                    if (path.endsWith('/items/DEMO-1/watch')) return json([])
                    if (path.endsWith('/items/DEMO-1/runs')) return json(paged([run]))
                    if (
                        path.endsWith('/comments') ||
                        path.endsWith('/history') ||
                        path === '/api/v1/orgs/fixture/members'
                    )
                        return json(paged())
                    if (
                        [
                            '/relations',
                            '/links',
                            '/watchers',
                            '/children',
                            '/workflows',
                            '/teams',
                            '/labels',
                            '/github/bindings',
                            '/projects/DEMO/members',
                        ].some((suffix) => path.endsWith(suffix))
                    )
                        return json([])
                    if (path.endsWith('/refinement')) return json(null)
                    if (path.endsWith('/refinement-settings'))
                        return json({ enabled: false, version: 1 })
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
                            seats: { human: 1, agent: 1 },
                            plans: [],
                            evaluation: null,
                        })
                    unexpected.push(path)
                    return route.fulfill({
                        status: 404,
                        contentType: 'application/json',
                        body: '{}',
                    })
                })
                const page = await context.newPage()
                page.on('pageerror', (error) => errors.push(error.message))
                for (const modal of [false, true]) {
                    await page.goto(modal ? '/about?item=DEMO-1' : '/o/fixture/p/DEMO/items/DEMO-1')
                    const detail = modal
                        ? page.getByRole('dialog', { name: 'DEMO-1', exact: true })
                        : page.locator('main')
                    await detail.locator('#item-title').waitFor()
                    assert.equal(await detail.locator('#item-title').inputValue(), item.title)
                    await detail.getByRole('button', { name: 'comments', exact: true }).waitFor()
                    if (canOperateFactory) {
                        await detail
                            .getByTestId('item-runs')
                            .getByText('Succeeded', { exact: true })
                            .waitFor()
                        await detail.getByTestId('start-run-button').waitFor()
                        await detail.getByRole('link', { name: 'Open log', exact: true }).waitFor()
                        await detail.getByTestId('run-resume-command').waitFor()
                        assert.ok(requests.some((path) => path.endsWith('/runs')))
                    } else {
                        assert.equal(await detail.getByTestId('item-runs').count(), 0)
                        assert.equal(await detail.getByTestId('start-run-button').count(), 0)
                        assert.doesNotMatch(
                            await detail.innerText(),
                            /Agent runs|Release assistant|Fixture build machine|Open log|Copy resume command/,
                        )
                        assert.equal(
                            requests.some((path) =>
                                /\/(runs|refinement|refinement-settings|fleet\/status)$/.test(path),
                            ),
                            false,
                        )
                        assert.equal(
                            await page.getByRole('link', { name: 'Factory', exact: true }).count(),
                            0,
                        )
                    }
                    console.log(`PASS ${role} factory=${canOperateFactory} modal=${modal}`)
                    if (role === 'member') {
                        const label = canOperateFactory ? 'operator' : 'stakeholder'
                        screenshots.push([
                            `${label}-${modal ? 'dialog' : 'page'}.png`,
                            await page.screenshot({ animations: 'disabled' }),
                        ])
                        if (!canOperateFactory && !modal) {
                            await page.setViewportSize({ width: 390, height: 844 })
                            assert.equal(await detail.getByTestId('item-runs').count(), 0)
                            screenshots.push([
                                'stakeholder-mobile.png',
                                await page.screenshot({ animations: 'disabled' }),
                            ])
                            await page.setViewportSize({ width: 1440, height: 1100 })
                        }
                    }
                }
                assert.deepEqual(errors, [])
                assert.deepEqual(unexpected, [])
                await context.close()
            }
            await mkdir(artifactDirectory, { recursive: true })
            for (const [name, bytes] of screenshots)
                await writeFile(resolve(artifactDirectory, name), bytes)
            console.log(
                `PASS fixture-backed browser integration; ${screenshots.length} screenshots in ${artifactDirectory}`,
            )
        } finally {
            await browser.close()
        }
    },
)
