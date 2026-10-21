/**
 * Fixture-backed integration of the real Vue app, including SignalR invalidations.
 * Start Vite then run PLAYWRIGHT_MODULE=/path/to/playwright/index.mjs
 * node --test e2e/reschedule-run.mjs. No live backend or account is used.
 * Optional: E2E_BASE_URL, CHROMIUM_PATH, E2E_ARTIFACT_DIR.
 * Only local requests are permitted, and screenshots are retained after all checks pass.
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
const artifactDirectory = resolve(process.env.E2E_ARTIFACT_DIR ?? '../.artifacts/AICTIQ-109')
const now = new Date().toISOString()
const later = (days, hour = 14) => {
    const at = new Date()
    at.setUTCDate(at.getUTCDate() + days)
    at.setUTCHours(hour, 30, 0, 0)
    return at.toISOString()
}
const localInput = (at) => at.slice(0, 16)
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
    claimedBy: 'agent-fixture',
    claimedAt: now,
    claimHeartbeatAt: now,
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
    runnerId: null,
    runnerName: null,
    status: 'queued',
    harness: 'codex',
    maxMinutes: 60,
    queuedAt: now,
    startedAt: null,
    finishedAt: null,
    cancelRequested: false,
    pullRequestUrl: null,
    scheduledFor: later(2),
    assignedAt: null,
    exitCode: null,
    costUsd: null,
    inputTokens: null,
    outputTokens: null,
    sessionId: 'fixture-session',
    workspacePath: '/fixture/repo',
    version: 1,
}

async function fixture(
    browser,
    {
        role = 'member',
        dispatcher = true,
        operator = true,
        status = 'queued',
        scheduledFor = later(2),
    } = {},
) {
    const context = await browser.newContext({
        baseURL,
        timezoneId: 'UTC',
        locale: 'en-US',
        viewport: { width: 1440, height: 1100 },
    })
    context.setDefaultTimeout(10000)
    const current = {
        ...run,
        requestedBy: dispatcher ? 'user-fixture' : 'other-user',
        status,
        scheduledFor,
    }
    const state = {
        current,
        writes: [],
        reads: [],
        errors: [],
        unexpected: [],
        conflict: false,
        validation: false,
        sockets: new Set(),
    }
    const org = {
        id: 'org-fixture',
        slug: 'fixture',
        name: 'Fixture Studio',
        role: 'member',
        canOperateFactory: operator,
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
    state.emit = () => {
        for (const socket of state.sockets)
            socket.send(
                JSON.stringify({
                    type: 1,
                    target: 'run.changed',
                    arguments: [
                        {
                            runId: current.id,
                            itemId: item.id,
                            itemKey: item.key,
                            status: current.status,
                        },
                    ],
                }) + '\x1e',
            )
    }
    await context.route('**/*', async (route) => {
        const request = route.request()
        const url = new URL(request.url())
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
        state.reads.push(path)
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
        if (path.endsWith('/runs/run-fixture/reschedule')) {
            assert.equal(request.method(), 'POST')
            const body = request.postDataJSON()
            state.writes.push(body)
            if (state.conflict) {
                current.status = 'assigned'
                current.assignedAt = now
                return json({ title: 'Run already assigned', status: 409 }, 409)
            }
            if (state.validation)
                return json(
                    {
                        title: 'Validation failed',
                        status: 400,
                        errors: { scheduledFor: ['Choose a start time in the future.'] },
                    },
                    400,
                )
            current.scheduledFor = body.scheduledFor
            current.version++
            state.emit()
            return json(current)
        }
        if (path.endsWith('/runs/run-fixture')) return json(current)
        if (path.endsWith('/runs/run-fixture/log'))
            return json({ items: [], nextAfter: -1, hasMore: false })
        if (path.endsWith('/items/DEMO-1')) return json(item)
        if (path.endsWith('/items/DEMO-1/runs')) return json(paged([current]))
        if (path.endsWith('/items/DEMO-1/watch')) return json([])
        if (path === '/api/v1/orgs/fixture/members')
            return json(
                paged([
                    {
                        userId: 'agent-fixture',
                        displayName: 'Release assistant',
                        email: null,
                        isAgent: true,
                        role: 'member',
                        avatarKey: null,
                    },
                ]),
            )
        if (path.endsWith('/comments') || path.endsWith('/history')) return json(paged())
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
        if (path.endsWith('/refinement-settings')) return json({ enabled: false, version: 1 })
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
    await page.goto('/o/fixture/factory/runs/run-fixture')
    await page
        .getByText(operator ? 'Fixture Studio' : 'The factory is not available to you', {
            exact: true,
        })
        .last()
        .waitFor()
    if (operator) await page.getByTestId('run-runner').waitFor()
    return { context, page, state }
}

test(
    'scheduled run rescheduling permissions, validation, realtime and claim preservation',
    { timeout: 120000 },
    async () => {
        const browser = await chromium.launch({
            executablePath: process.env.CHROMIUM_PATH ?? '/usr/bin/chromium',
            headless: true,
            args: ['--no-sandbox'],
        })
        const screenshots = []
        try {
            for (const options of [
                { dispatcher: true, visible: true },
                { dispatcher: false, role: 'admin', visible: true },
                { dispatcher: false, visible: false },
                { status: 'running', visible: false },
                { scheduledFor: null, visible: false },
                { scheduledFor: later(-1), visible: false },
                { operator: false, visible: false },
            ]) {
                const { context, page, state } = await fixture(browser, options)
                assert.equal(
                    await page.getByTestId('run-edit-time').count(),
                    options.visible ? 1 : 0,
                    JSON.stringify(options),
                )
                assert.deepEqual(state.errors, [])
                assert.deepEqual(state.unexpected, [])
                await context.close()
            }
            const { context, page, state } = await fixture(browser)
            const beforeDetail = await page.getByTestId('run-scheduled').innerText()
            const observer = await context.newPage()
            await observer.goto('/o/fixture/p/DEMO/items/DEMO-1')
            await observer.getByTestId('claim-banner-scheduled').waitFor()
            await observer.getByTestId('item-runs').getByTestId('run-scheduled').waitFor()
            const beforeClaim = await observer.getByTestId('claim-banner-scheduled').innerText()
            const beforeList = await observer
                .getByTestId('item-runs')
                .getByTestId('run-scheduled')
                .innerText()
            await page.getByTestId('run-edit-time').click()
            const dialog = page.getByRole('dialog', { name: 'Edit start time', exact: true })
            await dialog.waitFor()
            assert.equal(
                await dialog.getByTestId('run-start-at').inputValue(),
                localInput(state.current.scheduledFor),
            )
            await dialog.getByTestId('run-start-at').fill('')
            await dialog.getByTestId('run-save-time').click()
            await dialog.getByRole('alert').filter({ hasText: 'Choose a date and time.' }).waitFor()
            assert.equal(state.writes.length, 0)
            await dialog.getByTestId('run-start-at').fill(localInput(later(-1)))
            await dialog.getByTestId('run-save-time').click()
            await dialog
                .getByRole('alert')
                .filter({ hasText: 'Choose a start time in the future.' })
                .waitFor()
            assert.equal(state.writes.length, 0)
            await dialog.getByTestId('run-start-at').fill(localInput(later(3)))
            state.validation = true
            await Promise.all([
                page.waitForResponse(
                    (response) =>
                        response.url().endsWith('/reschedule') && response.status() === 400,
                ),
                dialog.getByTestId('run-save-time').click(),
            ])
            await dialog
                .getByRole('alert')
                .filter({ hasText: 'Choose a start time in the future.' })
                .waitFor()
            assert.equal(await dialog.isVisible(), true)
            state.validation = false
            await dialog.getByRole('button', { name: 'Cancel', exact: true }).click()
            await dialog.waitFor({ state: 'hidden' })
            await page.getByTestId('run-edit-time').click()
            await dialog.getByTestId('run-start-at').fill(localInput(later(4)))
            await dialog.getByRole('heading', { name: 'Edit start time', exact: true }).click()
            assert.equal(await dialog.getByRole('alert').count(), 0)
            screenshots.push(['edit-start-time.png', await page.screenshot({ fullPage: true })])
            const originalId = state.current.id
            const originalAgent = item.claimedBy
            const runReads = state.reads.filter((path) => path.endsWith('/runs/run-fixture')).length
            await dialog.getByTestId('run-save-time').click()
            await dialog.waitFor({ state: 'hidden' })
            await page.getByText('Run scheduled for', { exact: false }).waitFor()
            await page.getByTestId('run-scheduled').filter({ hasNotText: beforeDetail }).waitFor()
            const expectedTime = await page.evaluate(
                (at) =>
                    new Date(at).toLocaleString(undefined, {
                        weekday: 'short',
                        month: 'short',
                        day: 'numeric',
                        hour: '2-digit',
                        minute: '2-digit',
                    }),
                later(4),
            )
            assert.equal(
                await page.getByTestId('run-scheduled').innerText(),
                `Scheduled for ${expectedTime}`,
            )
            await observer
                .getByTestId('claim-banner-scheduled')
                .filter({ hasNotText: beforeClaim })
                .waitFor()
            await observer
                .getByTestId('item-runs')
                .getByTestId('run-scheduled')
                .filter({ hasNotText: beforeList })
                .waitFor()
            assert.equal(state.current.id, originalId)
            assert.equal(item.claimedBy, originalAgent)
            assert.equal(state.current.status, 'queued')
            assert.equal(state.current.scheduledFor, later(4))
            assert.equal(state.writes.length, 2)
            assert.ok(
                state.reads.filter((path) => path.endsWith('/runs/run-fixture')).length > runReads,
            )
            assert.equal(new URL(page.url()).pathname, '/o/fixture/factory/runs/run-fixture')
            screenshots.push(['updated-start-time.png', await page.screenshot({ fullPage: true })])
            await page.getByTestId('run-edit-time').click()
            await dialog.getByTestId('run-start-at').fill(localInput(later(5)))
            state.conflict = true
            await dialog.getByTestId('run-save-time').click()
            await dialog.waitFor({ state: 'hidden' })
            await page
                .getByText('This run is no longer waiting for its start time.', { exact: true })
                .waitFor()
            await page.getByTestId('run-edit-time').waitFor({ state: 'hidden' })
            await page.getByText('Assigned', { exact: true }).waitFor()
            assert.deepEqual(state.errors, [])
            assert.deepEqual(state.unexpected, [])
            await context.close()
            await mkdir(artifactDirectory, { recursive: true })
            for (const [name, bytes] of screenshots)
                await writeFile(resolve(artifactDirectory, name), bytes)
            console.log(
                `All fixture assertions passed; saved ${screenshots.length} screenshots in ${artifactDirectory}`,
            )
        } finally {
            await browser.close()
        }
    },
)
