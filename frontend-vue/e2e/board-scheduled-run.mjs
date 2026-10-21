/**
 * Fixture-backed integration of the real Vue app's board, including SignalR invalidations.
 * Start Vite then run PLAYWRIGHT_MODULE=/path/to/playwright/index.mjs
 * node --test e2e/board-scheduled-run.mjs. No live backend or account is used.
 * Optional: E2E_BASE_URL, CHROMIUM_PATH, E2E_ARTIFACT_DIR.
 * Only local requests are permitted, and screenshots are retained after all checks pass.
 */
import assert from 'node:assert/strict'
import { mkdir } from 'node:fs/promises'
import { resolve } from 'node:path'
import process from 'node:process'
import test from 'node:test'
import { URL } from 'node:url'

const { chromium } = await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright')
const baseURL = process.env.E2E_BASE_URL ?? 'http://127.0.0.1:5173'
const artifactDirectory = resolve(process.env.E2E_ARTIFACT_DIR ?? '../.artifacts/AICTIQ-112')
const now = new Date().toISOString()
const later = (hours) => new Date(Date.now() + hours * 3_600_000).toISOString()
const card = (number, title, extra = {}) => ({
    id: `item-${number}`,
    key: `DEMO-${number}`,
    type: 'story',
    title,
    stateId: 'state-todo',
    boardColumnId: 'column-todo',
    stateCategory: 'proposed',
    priority: 'medium',
    descriptionMarkdown: '',
    descriptionHtml: '',
    assigneeId: null,
    teamId: 'team-fixture',
    sprintId: null,
    parentId: null,
    points: null,
    estimateHours: null,
    remainingHours: null,
    completedHours: null,
    dueDate: null,
    version: 1,
    labels: [],
    createdAt: now,
    updatedAt: now,
    isWatching: false,
    watcherCount: 0,
    blocked: false,
    claimedBy: null,
    claimedAt: null,
    claimHeartbeatAt: null,
    scheduledRunAt: null,
    rollup: {
        totalCount: 0,
        completedCount: 0,
        pointsTotal: 0,
        pointsCompleted: 0,
        remainingHours: 0,
    },
    ...extra,
})
const claimed = { claimedBy: 'agent-fixture', claimedAt: now, claimHeartbeatAt: now }

async function fixture(browser) {
    const context = await browser.newContext({
        baseURL,
        timezoneId: 'UTC',
        locale: 'en-US',
        viewport: { width: 1280, height: 800 },
    })
    context.setDefaultTimeout(10000)
    const cards = [
        card(1, 'Release checklist review', { ...claimed, scheduledRunAt: later(6) }),
        card(2, 'Footer link audit', claimed),
        card(3, 'Pricing page copy'),
        // Due within seconds: the clock must leave on its own, without a run event.
        card(4, 'Changelog draft', {
            ...claimed,
            scheduledRunAt: new Date(Date.now() + 6000).toISOString(),
        }),
    ]
    const state = { cards, boardReads: 0, errors: [], unexpected: [], sockets: new Set() }
    const org = {
        id: 'org-fixture',
        slug: 'fixture',
        name: 'Fixture Studio',
        role: 'member',
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
        role: 'member',
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
        isLead: false,
        version: 1,
    }
    await context.routeWebSocket(/.*/, (socket) => {
        const url = new URL(socket.url())
        if (url.host !== new URL(baseURL).host) return socket.close()
        if (!url.pathname.startsWith('/hubs/')) return socket.connectToServer()
        state.sockets.add(socket)
        socket.onClose(() => state.sockets.delete(socket))
        socket.onMessage((message) => {
            for (const part of String(message).split('\x1e').filter(Boolean)) {
                const value = JSON.parse(part)
                if (value.protocol) socket.send('{}\x1e')
                else if (value.type === 1 && value.invocationId)
                    socket.send(
                        JSON.stringify({ type: 3, invocationId: value.invocationId }) + '\x1e',
                    )
            }
        })
    })
    state.emit = (itemKey, status) => {
        for (const socket of state.sockets)
            socket.send(
                JSON.stringify({
                    type: 1,
                    target: 'run.changed',
                    arguments: [{ runId: 'run-fixture', itemId: 'item-1', itemKey, status }],
                }) + '\x1e',
            )
    }
    await context.route('**/*', async (route) => {
        const url = new URL(route.request().url())
        if (url.origin !== new URL(baseURL).origin) return route.abort()
        const json = (body, statusCode = 200) =>
            route.fulfill({
                status: statusCode,
                contentType: 'application/json',
                body: JSON.stringify(body),
            })
        if (url.pathname.startsWith('/hubs/'))
            return json({
                negotiateVersion: 1,
                connectionId: 'fixture',
                connectionToken: 'fixture',
                availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text'] }],
            })
        if (!url.pathname.startsWith('/api/')) return route.continue()
        const path = url.pathname.replace(/\/$/, '')
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
        if (path.endsWith('/teams/team-fixture/board')) {
            state.boardReads++
            return json({
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
                        count: state.cards.length,
                        wipExceeded: false,
                        cards: state.cards,
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
            })
        }
        if (path.endsWith('/workflows'))
            return json([
                {
                    id: 'workflow-fixture',
                    name: 'Default',
                    isDefault: true,
                    states: [
                        { id: 'state-todo', name: 'To do', category: 'proposed', position: 0 },
                        { id: 'state-doing', name: 'Doing', category: 'active', position: 1 },
                    ],
                },
            ])
        if (path === '/api/v1/orgs/fixture/members')
            return json({ items: [], page: 1, pageSize: 50, totalCount: 0 })
        if (
            ['/projects/DEMO/members', '/labels', '/sprints', '/github/bindings'].some((suffix) =>
                path.endsWith(suffix),
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
        state.unexpected.push(path)
        return json({}, 404)
    })
    context.on('page', (page) => page.on('pageerror', (error) => state.errors.push(error.message)))
    const page = await context.newPage()
    return { context, page, state }
}

const cardOf = (page, key) =>
    page.locator('[draggable="true"]').filter({ has: page.getByText(key, { exact: true }) })
const clockOf = (page, key) => cardOf(page, key).getByTestId('card-scheduled')

test('board cards show a clock while their run waits for its start time', { timeout: 120000 }, async () => {
    const browser = await chromium.launch({
        executablePath: process.env.CHROMIUM_PATH ?? '/usr/bin/chromium',
        headless: true,
        args: ['--no-sandbox'],
    })
    try {
        const { context, page, state } = await fixture(browser)
        await page.goto('/o/fixture/p/DEMO/board')
        await cardOf(page, 'DEMO-1').waitFor()

        // Scheduled: a clock beside the claim glyph, named for assistive technology.
        const clock = clockOf(page, 'DEMO-1')
        await clock.waitFor()
        const label = await clock.getAttribute('aria-label')
        assert.match(label, /^Agent run scheduled for /)
        assert.equal(await clock.getAttribute('title'), label)
        assert.equal(await clock.getAttribute('role'), 'img')
        assert.equal(await page.getByRole('img', { name: label }).count(), 1)
        assert.equal(await cardOf(page, 'DEMO-1').getByLabel(/is working on this/).count(), 1)
        // As large as the claim glyph beside it.
        const box = await clock.locator('svg').boundingBox()
        const glyph = await cardOf(page, 'DEMO-1').getByLabel(/is working on this/).locator('svg').boundingBox()
        assert.ok(box && glyph && box.width >= glyph.width && box.width >= 11, JSON.stringify([box, glyph]))

        // An ordinary queued run's claim, or nothing at all: no clock.
        assert.equal(await clockOf(page, 'DEMO-2').count(), 0)
        assert.equal(await cardOf(page, 'DEMO-2').getByLabel(/is working on this/).count(), 1)
        assert.equal(await clockOf(page, 'DEMO-3').count(), 0)

        await mkdir(artifactDirectory, { recursive: true })
        await page.screenshot({
            path: resolve(artifactDirectory, 'board-scheduled-clock.png'),
            clip: { x: 0, y: 0, width: 1280, height: 640 },
        })
        await cardOf(page, 'DEMO-1').screenshot({
            path: resolve(artifactDirectory, 'board-card-scheduled.png'),
        })

        // The start time comes: the clock leaves without a refetch or a run event.
        await clockOf(page, 'DEMO-4').waitFor()
        const readsBefore = state.boardReads
        await clockOf(page, 'DEMO-4').waitFor({ state: 'detached', timeout: 15000 })
        assert.equal(state.boardReads, readsBefore)
        assert.equal(await clockOf(page, 'DEMO-1').count(), 1)

        // Cancelled (or started now): the run event refreshes the board and the clock goes.
        state.cards[0] = { ...state.cards[0], ...{ claimedBy: null, claimedAt: null, claimHeartbeatAt: null }, scheduledRunAt: null }
        state.emit('DEMO-1', 'cancelled')
        await clockOf(page, 'DEMO-1').waitFor({ state: 'detached' })

        // Scheduled again, then rescheduled: the clock follows the new time.
        state.cards[0] = { ...state.cards[0], ...claimed, scheduledRunAt: later(6) }
        state.emit('DEMO-1', 'queued')
        await clockOf(page, 'DEMO-1').waitFor()
        state.cards[0] = { ...state.cards[0], scheduledRunAt: later(30) }
        state.emit('DEMO-1', 'queued')
        await page.waitForFunction(
            (previous) =>
                globalThis.document.querySelector('[data-testid="card-scheduled"]')?.getAttribute('aria-label') !== previous,
            label,
        )

        // Reloading the board keeps it.
        await page.reload()
        await clockOf(page, 'DEMO-1').waitFor()
        await page.screenshot({
            path: resolve(artifactDirectory, 'board-after-reschedule-and-reload.png'),
            clip: { x: 0, y: 0, width: 1280, height: 640 },
        })

        // Picked up by a runner: no longer waiting.
        state.cards[0] = { ...state.cards[0], scheduledRunAt: null }
        state.emit('DEMO-1', 'assigned')
        await clockOf(page, 'DEMO-1').waitFor({ state: 'detached' })
        assert.equal(await cardOf(page, 'DEMO-1').getByLabel(/is working on this/).count(), 1)

        assert.deepEqual(state.errors, [])
        assert.deepEqual(state.unexpected, [])
        await context.close()
    } finally {
        await browser.close()
    }
})
