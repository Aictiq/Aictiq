/**
 * Fixture-backed browser regression for multiple repositories (AICTIQ-87). Start Vite, then run:
 * PLAYWRIGHT_MODULE=/path/to/playwright/index.mjs node --test e2e/multi-repo.mjs
 * Optional: E2E_BASE_URL, CHROMIUM_PATH, E2E_ARTIFACT_DIR.
 * No live account or backend is used; screenshots are saved only after all checks pass.
 */
/* global window */
import assert from 'node:assert/strict'
import { mkdir, writeFile } from 'node:fs/promises'
import { resolve } from 'node:path'
import process from 'node:process'
import test from 'node:test'
import { URL } from 'node:url'

const { chromium } = await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright')
const baseURL = process.env.E2E_BASE_URL ?? 'http://127.0.0.1:5173'
const artifactDirectory = resolve(process.env.E2E_ARTIFACT_DIR ?? '../.artifacts/AICTIQ-87')
const now = '2026-10-30T12:00:00Z'
const org = {
    id: 'org-fixture',
    slug: 'fixture',
    name: 'Fixture Studio',
    role: 'owner',
    canOperateFactory: true,
}
const project = {
    id: 'project-acme',
    key: 'ACME',
    name: 'Acme Platform',
    description: null,
    visibility: 'private',
    icon: null,
    color: null,
    isArchived: false,
    createdAt: now,
    role: 'admin',
    version: 1,
}
const single = {
    projectId: project.id,
    repoSource: 0,
    repoFullName: 'fixture/acme',
    defaultBranch: 'main',
    localPathHint: null,
    defaultAgentId: null,
    defaultRunnerId: null,
    updatedAt: now,
    version: 3,
    multiRepoEnabled: false,
    multiRepoAvailability: 'available',
    primaryAlias: 'acme',
    primaryWorkingDirectory: null,
    additionalRepositories: [],
}
const bindings = ['fixture/acme', 'fixture/acme-api'].map((fullName, index) => ({
    id: `binding-${index}`,
    repoId: 100 + index,
    installationId: 1,
    fullName,
    onPullRequestOpenedStateId: null,
    onPullRequestMergedStateId: null,
    createdAt: now,
}))
const playbook = {
    id: 'playbook-implement',
    projectId: project.id,
    name: 'Implement',
    wikiPageId: 'page-implement',
    harness: 'claude',
    onSuccessStateId: null,
    onFailureStateId: null,
    workOnDefaultBranch: false,
    maxMinutes: 60,
    isDefault: true,
    stagePlaybookIds: [],
    requiredTags: [],
    repositorySteps: [],
    createdBy: 'user-fixture',
    createdAt: now,
    updatedAt: now,
    version: 4,
}
const run = {
    id: 'run-backend',
    projectId: project.id,
    itemId: 'item-1',
    itemKey: 'ACME-12',
    playbookId: playbook.id,
    playbookName: 'Implement',
    agentId: 'agent-fixture',
    agentName: 'Fixture Agent',
    requestedBy: 'user-fixture',
    ruleId: null,
    ruleName: null,
    runnerId: 'runner-fixture',
    runnerName: 'fixture-runner',
    requestedRunnerId: null,
    requestedRunnerName: null,
    status: 'succeeded',
    harness: 'claude',
    playbookRevisionId: null,
    maxMinutes: 60,
    queuedAt: '2026-10-30T11:00:00Z',
    assignedAt: '2026-10-30T11:00:05Z',
    startedAt: '2026-10-30T11:00:10Z',
    finishedAt: '2026-10-30T11:20:10Z',
    lastHeartbeatAt: '2026-10-30T11:20:00Z',
    cancelRequested: false,
    outcomeSummary: 'Changed the order schema in acme and the client in acme-api.',
    pullRequestUrl: 'https://github.com/fixture/acme/pull/41',
    pullRequestUrls: [
        'https://github.com/fixture/acme/pull/41',
        'https://github.com/fixture/acme-api/pull/7',
    ],
    exitCode: 0,
    costUsd: 0.42,
    inputTokens: 12000,
    outputTokens: 3400,
    failureReason: null,
    promptSnapshot: null,
    version: 9,
    kind: 'implement',
    branchName: 'acme-12-order-schema',
    repositories: ['acme', 'acme-api'],
    repositorySteps: [
        {
            aliases: ['acme', 'acme-api'],
            after: [],
            waitForMerge: false,
            state: 'succeeded',
            runId: 'run-backend',
            pullRequestUrls: [
                'https://github.com/fixture/acme/pull/41',
                'https://github.com/fixture/acme-api/pull/7',
            ],
        },
        {
            aliases: ['web'],
            after: ['acme'],
            waitForMerge: true,
            state: 'waiting-for-merge',
            runId: null,
            pullRequestUrls: [],
        },
    ],
}

async function openContext(browser, settings, writes, unexpected) {
    const context = await browser.newContext({
        baseURL,
        viewport: { width: 1280, height: 1000 },
        colorScheme: 'dark',
    })
    context.setDefaultTimeout(10000)
    await context.route('**/*', async (route) => {
        const request = route.request()
        const url = new URL(request.url())
        if (url.origin !== new URL(baseURL).origin) return route.abort()
        if (url.pathname.startsWith('/hubs/')) return route.fulfill({ status: 404 })
        if (!url.pathname.startsWith('/api/')) return route.continue()
        const path = url.pathname.replace(/\/$/, '')
        const method = request.method()
        const json = (body, status = 200) =>
            route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
        const base = '/api/v1/orgs/fixture'
        const projectBase = `${base}/projects/ACME`
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
        if (path === base) return json(org)
        if (path === `${base}/projects`) return json([project])
        if (path === projectBase) return json(project)
        if (path === `${base}/fleet/status`)
            return json({
                availability: 'hidden',
                enabled: false,
                active: false,
                downgraded: false,
                knownTags: [],
            })
        if (path === `${base}/billing/subscription`)
            return json({
                mode: 'saas',
                enabled: true,
                plan: 'hosted',
                status: 'active',
                readOnly: false,
                seats: { human: 1, agent: 1 },
                plans: [],
                evaluation: null,
            })
        if (path === `${projectBase}/factory-settings`) {
            if (method === 'PUT') {
                const body = request.postDataJSON()
                writes.push(body)
                return json({ ...settings.current, ...body, version: body.version + 1 })
            }
            return json(settings.current)
        }
        if (path === `${projectBase}/github/bindings`) return json(bindings)
        if (path === `${projectBase}/members`) return json([])
        if (path === `${base}/agents`) return json([])
        if (path === `${base}/runners/choices`) return json([])
        if (path === `${projectBase}/playbooks`) return json([playbook])
        if (path === `${projectBase}/playbooks/${playbook.id}/instructions`)
            return json({
                wikiPageId: playbook.wikiPageId,
                pageTitle: 'Implement',
                markdown: '# Implement\n\nShip the ticket.',
                inFactorySection: true,
            })
        if (path === `${projectBase}/teams`) return json([])
        if (path === `${projectBase}/refinement-settings`)
            return json({
                projectId: project.id,
                playbookId: null,
                agentId: null,
                productDescription: '',
                writingInstructions: '',
                namingConventions: '',
                platforms: '',
                refinedStateId: null,
                enabled: false,
                updatedAt: null,
                version: 0,
            })
        if (path === `${projectBase}/workflows`) return json([])
        if (path === `${projectBase}/boards`) return json([])
        if (path === `${base}/runs/${run.id}`) return json(run)
        if (path === `${base}/runs/${run.id}/log`) return json({ items: [], truncated: false })
        unexpected.push(`${method} ${path}`)
        return json(method === 'GET' ? [] : {}, method === 'GET' ? 200 : 404)
    })
    const page = await context.newPage()
    const errors = []
    page.on('pageerror', (error) => errors.push(error.message))
    return { context, page, errors }
}

test(
    'multiple repositories: opt-in settings, playbook steps and run steps',
    { timeout: 120000 },
    async () => {
        const browser = await chromium.launch({
            executablePath: process.env.CHROMIUM_PATH ?? '/usr/bin/chromium',
            headless: true,
            args: ['--no-sandbox'],
        })
        const screenshots = []
        const unexpected = []
        const shot = async (page, name, locator) =>
            screenshots.push([name, await (locator ?? page).screenshot({ animations: 'disabled' })])
        try {
            // Self-hosted: the single-repository page, with nothing new on it.
            {
                const settings = { current: { ...single, multiRepoAvailability: 'hidden' } }
                const { context, page, errors } = await openContext(
                    browser,
                    settings,
                    [],
                    unexpected,
                )
                await page.goto('/o/fixture/p/ACME/settings/factory')
                await page.getByText('Repository source').waitFor()
                assert.equal(await page.getByTestId('multi-repo').count(), 0)
                assert.equal(await page.getByText('Monorepos and multiple repositories').count(), 0)
                assert.deepEqual(errors, [])
                await context.close()
            }

            // Free plan: one line with the upgrade.
            {
                const settings = { current: { ...single, multiRepoAvailability: 'upgrade' } }
                const { context, page, errors } = await openContext(
                    browser,
                    settings,
                    [],
                    unexpected,
                )
                await page.goto('/o/fixture/p/ACME/settings/factory')
                const note = page.getByTestId('multi-repo-note')
                await note.waitFor()
                assert.match(await note.innerText(), /supported on paid plans/)
                assert.equal(
                    await page.getByTestId('multi-repo-upgrade-link').getAttribute('href'),
                    '/o/fixture/settings/billing',
                )
                assert.equal(await page.getByTestId('multi-repo-switch').count(), 0)
                assert.deepEqual(errors, [])
                await shot(page, '3-settings-upgrade-note.png')
                await context.close()
            }

            // Paid plan: the note, then the switch, the primary and two more repositories.
            {
                const settings = { current: { ...single } }
                const writes = []
                const { context, page, errors } = await openContext(
                    browser,
                    settings,
                    writes,
                    unexpected,
                )
                await page.goto('/o/fixture/p/ACME/settings/factory')
                const note = page.getByTestId('multi-repo-note')
                await note.waitFor()
                assert.match(
                    await note.innerText(),
                    /Monorepos and multiple repositories are supported\./,
                )
                assert.equal(await page.getByTestId('multi-repo-settings').count(), 0)
                await shot(page, '1-settings-note.png')

                await page.getByTestId('multi-repo-turn-on').click()
                const section = page.getByTestId('multi-repo-settings')
                await section.waitFor()
                assert.equal(await page.getByTestId('multi-repo-switch').isChecked(), true)
                assert.equal(await page.locator('#multi-repo-primary-alias').inputValue(), 'acme')
                await page.locator('#multi-repo-primary-directory').fill('packages/orders')

                await page.getByTestId('multi-repo-add').click()
                await page.locator('#multi-repo-alias-0').fill('acme-api')
                await page.locator('#multi-repo-github-0').selectOption('fixture/acme-api')
                await page.locator('#multi-repo-directory-0').fill('sdk')

                await page.getByTestId('multi-repo-add').click()
                await page.locator('#multi-repo-alias-1').fill('web')
                await page
                    .locator('#multi-repo-source-1')
                    .selectOption({ label: 'Runner-local checkout' })
                await page.locator('#multi-repo-path-1').fill('/srv/repos/acme-web')
                await section
                    .getByText('aictiq runner map ACME/web /srv/repos/acme-web --org fixture')
                    .waitFor()
                assert.equal(await page.getByTestId('multi-repo-entry').count(), 2)

                const save = page.getByRole('button', { name: 'Save factory settings' })
                assert.equal(await save.isEnabled(), true)
                await save.click()
                await page.waitForFunction(() =>
                    window.document.body.innerText.includes('Factory settings saved'),
                )
                assert.equal(writes.length, 1)
                assert.deepEqual(
                    {
                        multiRepoEnabled: writes[0].multiRepoEnabled,
                        primaryAlias: writes[0].primaryAlias,
                        primaryWorkingDirectory: writes[0].primaryWorkingDirectory,
                        additionalRepositories: writes[0].additionalRepositories,
                    },
                    {
                        multiRepoEnabled: true,
                        primaryAlias: 'acme',
                        primaryWorkingDirectory: 'packages/orders',
                        additionalRepositories: [
                            {
                                alias: 'acme-api',
                                repoSource: 0,
                                repoFullName: 'fixture/acme-api',
                                localPathHint: null,
                                defaultBranch: 'main',
                                workingDirectory: 'sdk',
                            },
                            {
                                alias: 'web',
                                repoSource: 1,
                                repoFullName: null,
                                localPathHint: '/srv/repos/acme-web',
                                defaultBranch: 'main',
                                workingDirectory: null,
                            },
                        ],
                    },
                )
                await page
                    .locator('[data-sonner-toast]')
                    .waitFor({ state: 'hidden', timeout: 15000 })
                assert.deepEqual(errors, [])
                await shot(page, '2-settings-repositories.png', section)
                await context.close()
            }

            // The playbook editor: two steps, acme-api after acme once its pull request merged.
            {
                const settings = {
                    current: {
                        ...single,
                        multiRepoEnabled: true,
                        additionalRepositories: [
                            {
                                alias: 'acme-api',
                                repoSource: 0,
                                repoFullName: 'fixture/acme-api',
                                localPathHint: null,
                                defaultBranch: 'main',
                                workingDirectory: 'sdk',
                            },
                        ],
                    },
                }
                const { context, page, errors } = await openContext(
                    browser,
                    settings,
                    [],
                    unexpected,
                )
                const patches = []
                await context.route(
                    '**/api/v1/orgs/fixture/projects/ACME/playbooks/playbook-implement',
                    async (route) => {
                        if (route.request().method() !== 'PATCH') return route.fallback()
                        const body = route.request().postDataJSON()
                        patches.push(body)
                        return route.fulfill({
                            contentType: 'application/json',
                            body: JSON.stringify({ ...playbook, ...body, version: 5 }),
                        })
                    },
                )
                await page.goto('/o/fixture/factory/playbooks')
                await page.getByText('Implement', { exact: true }).first().waitFor()
                await page.getByRole('button', { name: 'Manage Implement', exact: true }).click()
                await page.getByRole('menuitem', { name: 'Edit', exact: true }).click()
                const dialog = page.getByRole('dialog')
                const steps = dialog.getByTestId('playbook-repository-steps')
                await steps.waitFor()
                await steps.getByTestId('playbook-repository-add').click()
                await steps.getByTestId('playbook-repository-add').click()
                assert.equal(await steps.getByTestId('playbook-repository-step').count(), 2)
                assert.equal(await dialog.locator('#repository-step-alias-0').inputValue(), 'acme')
                assert.equal(
                    await dialog.locator('#repository-step-alias-1').inputValue(),
                    'acme-api',
                )
                const second = steps.getByTestId('playbook-repository-step').nth(1)
                await second.getByLabel('acme', { exact: true }).check()
                await second.getByLabel(/Wait for their pull requests to merge/).check()
                await dialog.locator('#repository-step-directory-0').fill('packages/orders')
                await steps.scrollIntoViewIfNeeded()
                await dialog.evaluate((element) =>
                    Promise.all(element.getAnimations().map((a) => a.finished)),
                )
                await shot(page, '4-playbook-repository-steps.png', dialog)
                await dialog.getByRole('button', { name: 'Save changes' }).click()
                await dialog.waitFor({ state: 'hidden' })
                assert.deepEqual(patches[0].repositorySteps, [
                    {
                        alias: 'acme',
                        workingDirectory: 'packages/orders',
                        after: [],
                        waitForMerge: false,
                    },
                    {
                        alias: 'acme-api',
                        workingDirectory: null,
                        after: ['acme'],
                        waitForMerge: true,
                    },
                ])
                assert.deepEqual(errors, [])
                await context.close()
            }

            // A multi-repository run: its steps and both pull requests.
            {
                const settings = { current: { ...single } }
                const { context, page, errors } = await openContext(
                    browser,
                    settings,
                    [],
                    unexpected,
                )
                await page.goto(`/o/fixture/factory/runs/${run.id}`)
                const steps = page.getByTestId('run-repository-steps')
                await steps.waitFor()
                const rows = steps.getByTestId('run-repository-step')
                assert.equal(await rows.count(), 2)
                assert.match(await rows.nth(0).innerText(), /Succeeded/)
                assert.match(await rows.nth(1).innerText(), /Waiting for merge/)
                assert.match(await rows.nth(1).innerText(), /after acme is merged/)
                const prs = page.getByTestId('run-pr-links')
                await prs.getByText('Pull request fixture/acme#41').waitFor()
                await prs.getByText('Pull request fixture/acme-api#7').waitFor()
                assert.match(
                    await page.getByTestId('run-repositories').innerText(),
                    /acme, acme-api/,
                )
                assert.deepEqual(errors, [])
                await shot(page, '5-run-repository-steps.png')
                await context.close()
            }

            assert.deepEqual(unexpected, [])
            await mkdir(artifactDirectory, { recursive: true })
            for (const [name, bytes] of screenshots)
                await writeFile(resolve(artifactDirectory, name), bytes)
        } finally {
            await browser.close()
        }
    },
)
