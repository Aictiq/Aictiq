/**
 * Fixture-backed browser integration of ItemDetail team editing, with no live backend.
 * Start Vite, then run PLAYWRIGHT_MODULE=/path/to/playwright/index.mjs
 * node --test e2e/item-team.mjs. Screenshots are written only after all assertions pass.
 */
import assert from 'node:assert/strict'
import { mkdir, writeFile } from 'node:fs/promises'
import { resolve } from 'node:path'
import process from 'node:process'
import test from 'node:test'
import { URL } from 'node:url'

const { chromium } = await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright')
const baseURL = process.env.E2E_BASE_URL ?? 'http://127.0.0.1:5173'
const artifactDirectory = resolve(process.env.E2E_ARTIFACT_DIR ?? '.artifacts/AICTIQ-139')
const now = '2026-11-16T12:00:00Z'
const paged = (items = []) => ({ items, page: 1, pageSize: 50, totalCount: items.length })
const teams = ['Product', 'Platform'].map((name, index) => ({
    id: `team-${index + 1}`,
    name,
    key: name.toUpperCase(),
    isDefault: index === 0,
    sprintLengthDays: 14,
    workingDays: [1, 2, 3, 4, 5],
    estimationUnit: 'points',
    timeZone: null,
    memberCount: 0,
    isLead: false,
    version: 1,
}))
const initialItem = {
    id: 'item-fixture',
    key: 'DEMO-1',
    type: 'epic',
    title: 'Improve the planning experience',
    stateId: 'state-fixture',
    stateCategory: 'proposed',
    priority: 'medium',
    descriptionMarkdown: 'Plan the next release with the Product and Platform teams.',
    descriptionHtml: '',
    assigneeId: null,
    teamId: 'team-1',
    sprintId: 'sprint-fixture',
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

test(
    'ItemDetail changes and clears teams in page and dialog, respecting read-only access',
    { timeout: 120000 },
    async () => {
        const browser = await chromium.launch({
            executablePath: process.env.CHROMIUM_PATH ?? '/usr/bin/chromium',
            headless: true,
            args: ['--no-sandbox'],
        })
        const screenshots = []
        try {
            for (const [role, isArchived] of [
                ['member', false],
                ['guest', false],
                ['member', true],
            ]) {
                const context = await browser.newContext({
                    baseURL,
                    viewport: { width: 1440, height: 1100 },
                })
                context.setDefaultTimeout(10000)
                const errors = []
                const unexpected = []
                const writes = []
                let item = { ...initialItem }
                const org = {
                    id: 'org-fixture',
                    slug: 'fixture',
                    name: 'Fixture Studio',
                    role,
                    canOperateFactory: false,
                }
                const project = {
                    id: 'project-fixture',
                    key: 'DEMO',
                    name: 'Demo product',
                    description: null,
                    visibility: 'private',
                    icon: null,
                    color: null,
                    isArchived,
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
                    if (path.endsWith('/items/DEMO-1')) {
                        if (route.request().method() === 'PATCH') {
                            const body = route.request().postDataJSON()
                            assert.equal(role, 'member')
                            assert.equal(isArchived, false)
                            assert.equal(body.version, item.version)
                            assert.equal(body.type, 'epic')
                            assert.equal(body.title, initialItem.title)
                            writes.push(body)
                            item = { ...item, ...body, sprintId: null, version: item.version + 1 }
                        }
                        return json(item)
                    }
                    if (path.endsWith('/teams')) return json(teams)
                    if (path.endsWith('/board'))
                        return json({ columns: [], items: [], totalCount: 0 })
                    if (path.endsWith('/workflows'))
                        return json([
                            {
                                id: 'workflow-fixture',
                                states: [
                                    { id: 'state-fixture', name: 'Proposed', category: 'proposed' },
                                ],
                            },
                        ])
                    if (
                        path.endsWith('/comments') ||
                        path.endsWith('/history') ||
                        path === '/api/v1/orgs/fixture/members'
                    )
                        return json(paged())
                    if (
                        [
                            '/watch',
                            '/relations',
                            '/links',
                            '/watchers',
                            '/children',
                            '/labels',
                            '/github/bindings',
                            '/projects/DEMO/members',
                        ].some((suffix) => path.endsWith(suffix))
                    )
                        return json([])
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
                    item = { ...initialItem }
                    writes.length = 0
                    await page.goto(modal ? '/about?item=DEMO-1' : '/o/fixture/p/DEMO/items/DEMO-1')
                    const detail = modal
                        ? page.getByRole('dialog', { name: 'DEMO-1', exact: true })
                        : page.locator('main')
                    const select = detail.getByLabel('Team', { exact: true })
                    await select.waitFor()
                    await detail
                        .getByRole('option', { name: 'Platform', exact: true })
                        .waitFor({ state: 'attached' })
                    assert.equal(await select.inputValue(), 'team-1')
                    assert.equal(await select.isDisabled(), role === 'guest' || isArchived)
                    if (role === 'member' && !isArchived) {
                        assert.equal(await detail.locator('#item-team-sprint-note').count(), 1)
                        await select.selectOption('team-2')
                        await detail
                            .locator('#item-team-sprint-note')
                            .waitFor({ state: 'detached' })
                        assert.equal(await select.inputValue(), 'team-2')
                        assert.equal(writes.length, 1)
                        assert.equal(writes[0].teamId, 'team-2')
                        assert.equal(item.sprintId, null)
                        await select.scrollIntoViewIfNeeded()
                        screenshots.push([
                            `team-${modal ? 'dialog' : 'page'}.png`,
                            await page.screenshot({ animations: 'disabled' }),
                        ])
                        await select.selectOption('')
                        await select.waitFor({ state: 'visible' })
                        await detail
                            .getByText('Updating team…', { exact: true })
                            .waitFor({ state: 'detached' })
                        assert.equal(await select.inputValue(), '')
                        assert.equal(writes.length, 2)
                        assert.equal(writes[1].teamId, null)
                        assert.equal(item.teamId, null)
                        if (!modal) {
                            await page.setViewportSize({ width: 390, height: 844 })
                            await select.scrollIntoViewIfNeeded()
                            screenshots.push([
                                'team-mobile-no-team.png',
                                await page.screenshot({ animations: 'disabled' }),
                            ])
                            await page.setViewportSize({ width: 1440, height: 1100 })
                        }
                    } else assert.equal(writes.length, 0)
                }
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
