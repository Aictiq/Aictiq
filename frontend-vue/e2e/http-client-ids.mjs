/**
 * Fixture-backed regression for client IDs on an actual insecure HTTP origin.
 * Start Vite, then run PLAYWRIGHT_MODULE=/path/to/playwright/index.mjs
 * node --test e2e/http-client-ids.mjs. No backend or account is used.
 * The browser sees aictiq.test; intercepted static requests go only to local Vite.
 * Optional: E2E_SERVER_URL, CHROMIUM_PATH, E2E_ARTIFACT_DIR.
 * Screenshots are written only after every assertion passes.
 */
import assert from 'node:assert/strict'
import { mkdir, writeFile } from 'node:fs/promises'
import { resolve } from 'node:path'
import process from 'node:process'
import test from 'node:test'
import { URL } from 'node:url'

const { chromium } = await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright')
const serverURL = process.env.E2E_SERVER_URL ?? 'http://127.0.0.1:5173'
const baseURL = 'http://aictiq.test:5173'
const artifactDirectory = resolve(
    process.env.E2E_ARTIFACT_DIR ?? 'node_modules/.artifacts/AICTIQ-136',
)
const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/
const now = '2026-11-15T12:00:00Z'

test(
    'board and dashboard actions generate and persist IDs over insecure HTTP',
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
        const errors = []
        const unexpected = []
        const writes = []
        const screenshots = []
        const org = {
            id: 'org-fixture',
            slug: 'fixture',
            name: 'Fixture Studio',
            role: 'admin',
            canOperateFactory: true,
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
            role: 'admin',
            version: 1,
        }
        const team = {
            id: 'team-fixture',
            name: 'Demo team',
            key: 'DT',
            sprintLengthDays: 14,
            workingDays: [1, 2, 3, 4, 5],
            estimationUnit: 'points',
            timeZone: null,
            isDefault: true,
            memberCount: 1,
            isLead: true,
            version: 1,
        }
        let board = {
            id: 'board-fixture',
            teamId: team.id,
            kind: 'kanban',
            swimlane: 'none',
            cardFields: [],
            types: ['story', 'bug'],
            version: 1,
            columns: [
                {
                    id: 'column-todo',
                    name: 'To do',
                    stateIds: ['state-todo'],
                    generalState: 'new',
                    wipLimit: null,
                    count: 0,
                    wipExceeded: false,
                    cards: [],
                },
                {
                    id: 'column-doing',
                    name: 'Doing',
                    stateIds: ['state-doing'],
                    generalState: 'doing',
                    wipLimit: null,
                    count: 0,
                    wipExceeded: false,
                    cards: [],
                },
            ],
        }
        const dashboards = []
        const boardPath = '/api/v1/orgs/fixture/teams/team-fixture/board'
        const dashboardsPath = '/api/v1/orgs/fixture/projects/DEMO/dashboards'
        try {
            const context = await browser.newContext({
                baseURL,
                viewport: { width: 1440, height: 1000 },
                locale: 'en-US',
                timezoneId: 'UTC',
            })
            context.setDefaultTimeout(10000)
            await context.addInitScript(() => {
                globalThis.localStorage.setItem(
                    'aictiq.ui',
                    JSON.stringify({
                        theme: 'light',
                        density: 'comfortable',
                        sidebarCollapsed: false,
                    }),
                )
            })
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
                if (url.origin !== baseURL) {
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
                if (!url.pathname.startsWith('/api/')) {
                    const localURL = new URL(url.pathname + url.search, serverURL).toString()
                    return route.fulfill({
                        response: await route.fetch({ url: localURL, maxRedirects: 0 }),
                    })
                }
                const path = url.pathname.replace(/\/$/, '')
                const method = request.method()
                if (method !== 'GET') {
                    const body = request.postDataJSON()
                    if (path === boardPath && method === 'PUT') {
                        assert.equal(body.version, board.version)
                        for (const column of body.columns.slice(2)) assert.match(column.id, uuid)
                        writes.push({ path, method, body })
                        board = {
                            ...board,
                            ...body,
                            version: board.version + 1,
                            columns: body.columns.map((column) => ({
                                ...column,
                                count: 0,
                                wipExceeded: false,
                                cards: [],
                            })),
                        }
                        return json(board)
                    }
                    if (path === dashboardsPath && method === 'POST') {
                        assert.equal(body.layout.length, 1)
                        assert.match(body.layout[0].id, uuid)
                        writes.push({ path, method, body })
                        const dashboard = {
                            ...body,
                            id: 'dashboard-fixture',
                            ownerUserId: 'user-fixture',
                            isDefault: false,
                            version: 1,
                        }
                        dashboards.push(dashboard)
                        return json(dashboard)
                    }
                    if (path === `${dashboardsPath}/dashboard-fixture` && method === 'PATCH') {
                        assert.equal(body.version, dashboards[0].version)
                        for (const widget of body.layout) assert.match(widget.id, uuid)
                        writes.push({ path, method, body })
                        Object.assign(dashboards[0], body, { version: dashboards[0].version + 1 })
                        return json(dashboards[0])
                    }
                    unexpected.push(`${method} ${path}`)
                    return json({}, 404)
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
                if (path.endsWith('/projects/DEMO/teams')) return json([team])
                if (path === boardPath) return json(board)
                if (path === dashboardsPath) return json(dashboards)
                if (path === `${dashboardsPath}/dashboard-fixture/data`)
                    return json({
                        id: 'dashboard-fixture',
                        widgets: dashboards[0].layout.map((widget) => ({
                            id: widget.id,
                            type: widget.type,
                            error: null,
                            data:
                                widget.type === 'velocity'
                                    ? { teamName: team.name, velocity: null }
                                    : null,
                        })),
                    })
                if (path.endsWith('/workflows'))
                    return json([
                        {
                            id: 'workflow-fixture',
                            name: 'Default',
                            isDefault: true,
                            states: [
                                {
                                    id: 'state-todo',
                                    name: 'To do',
                                    category: 'proposed',
                                    position: 0,
                                },
                                {
                                    id: 'state-doing',
                                    name: 'Doing',
                                    category: 'active',
                                    position: 1,
                                },
                                {
                                    id: 'state-review',
                                    name: 'Review',
                                    category: 'active',
                                    position: 2,
                                },
                                {
                                    id: 'state-done',
                                    name: 'Done',
                                    category: 'completed',
                                    position: 3,
                                },
                            ],
                        },
                    ])
                if (path === '/api/v1/orgs/fixture/members')
                    return json({ items: [], page: 1, pageSize: 50, totalCount: 0 })
                if (
                    ['/projects/DEMO/members', '/labels', '/sprints', '/github/bindings'].some(
                        (suffix) => path.endsWith(suffix),
                    )
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
                        seats: { human: 1, agent: 1 },
                        plans: [],
                        evaluation: null,
                    })
                unexpected.push(`${method} ${path}`)
                return json({}, 404)
            })
            const page = await context.newPage()
            page.on('pageerror', (error) => errors.push(error.message))
            await page.goto('/o/fixture/p/DEMO/board')
            await page.getByRole('button', { name: 'Board settings', exact: true }).waitFor()
            assert.deepEqual(
                await page.evaluate(() => ({
                    secure: globalThis.isSecureContext,
                    randomUUID: typeof globalThis.crypto.randomUUID,
                    getRandomValues: typeof globalThis.crypto.getRandomValues,
                })),
                { secure: false, randomUUID: 'undefined', getRandomValues: 'function' },
            )

            await page.getByRole('button', { name: 'Board settings', exact: true }).click()
            const dialog = page.getByRole('dialog', { name: 'Board settings' })
            await dialog.getByRole('button', { name: 'Add column', exact: true }).click()
            assert.equal(await dialog.getByLabel('Column name', { exact: true }).count(), 3)
            assert.equal(
                await dialog.getByLabel('Column name', { exact: true }).nth(2).inputValue(),
                'Review',
            )
            await dialog.getByRole('button', { name: 'Save board', exact: true }).click()
            await dialog.waitFor({ state: 'detached' })
            await page.getByRole('region', { name: 'Review', exact: true }).waitFor()
            assert.equal(writes.length, 1)
            const settingsColumnId = board.columns[2].id
            assert.match(settingsColumnId, uuid)

            // The board's edge button opens settings and immediately inserts another column.
            await page.getByRole('button', { name: 'Add column', exact: true }).click()
            await dialog.waitFor()
            assert.equal(await dialog.getByLabel('Column name', { exact: true }).count(), 4)
            assert.equal(
                await dialog.getByLabel('Column name', { exact: true }).nth(3).inputValue(),
                'Done',
            )
            screenshots.push([
                'board-add-column-http.png',
                await page.screenshot({ fullPage: true }),
            ])
            await dialog.getByRole('button', { name: 'Save board', exact: true }).click()
            await dialog.waitFor({ state: 'detached' })
            await page.getByRole('region', { name: 'Done', exact: true }).waitFor()
            assert.equal(writes.length, 2)
            const edgeColumnId = board.columns[3].id
            assert.match(edgeColumnId, uuid)
            assert.notEqual(edgeColumnId, settingsColumnId)
            await page.reload()
            await page.getByRole('region', { name: 'Review', exact: true }).waitFor()
            await page.getByRole('region', { name: 'Done', exact: true }).waitFor()
            assert.equal(board.columns[2].id, settingsColumnId)
            assert.equal(board.columns[3].id, edgeColumnId)

            await page.goto('/o/fixture/p/DEMO/dashboard')
            await page.getByRole('button', { name: 'Dashboard', exact: true }).click()
            await page.getByRole('button', { name: 'Save layout', exact: true }).waitFor()
            await page.getByRole('heading', { name: 'Team velocity', exact: true }).waitFor()
            assert.equal(dashboards.length, 1)
            assert.equal(dashboards[0].name, 'My dashboard')
            const initialWidgetId = dashboards[0].layout[0].id
            assert.match(initialWidgetId, uuid)
            await page.getByRole('button', { name: 'markdown', exact: true }).click()
            await page.getByRole('heading', { name: 'Project note', exact: true }).waitFor()
            await page.getByRole('button', { name: 'Save layout', exact: true }).click()
            await page.getByText('Dashboard saved.', { exact: true }).waitFor()
            assert.equal(dashboards[0].layout.length, 2)
            const addedWidgetId = dashboards[0].layout[1].id
            assert.match(addedWidgetId, uuid)
            assert.notEqual(addedWidgetId, initialWidgetId)
            screenshots.push([
                'dashboard-add-widget-http.png',
                await page.screenshot({ fullPage: true }),
            ])
            await page.reload()
            await page.getByRole('heading', { name: 'Team velocity', exact: true }).waitFor()
            await page.getByRole('heading', { name: 'Project note', exact: true }).waitFor()
            await page.getByText('Add a note', { exact: true }).waitFor()
            assert.deepEqual(
                dashboards[0].layout.map((widget) => widget.id),
                [initialWidgetId, addedWidgetId],
            )
            assert.equal(
                new Set([settingsColumnId, edgeColumnId, initialWidgetId, addedWidgetId]).size,
                4,
            )
            assert.deepEqual(
                writes.map(({ method }) => method),
                ['PUT', 'PUT', 'POST', 'PATCH'],
            )
            assert.deepEqual(errors, [])
            assert.deepEqual(unexpected, [])
            await context.close()
            await mkdir(artifactDirectory, { recursive: true })
            for (const [name, buffer] of screenshots)
                await writeFile(resolve(artifactDirectory, name), buffer)
        } finally {
            await browser.close()
        }
    },
)
