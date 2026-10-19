/**
 * Fixture-backed browser integration test. Runs the real Vue app with an intercepted API;
 * it does not claim to exercise the backend or require a real account.
 *
 * Start Vite, then run:
 * PLAYWRIGHT_MODULE=/path/to/playwright/index.mjs node --test e2e/automatic-agent.mjs
 * Optional: E2E_BASE_URL, CHROMIUM_PATH, E2E_ARTIFACT_DIR.
 * Screenshots are written only after every behavior assertion passes.
 */
/* global document */
import assert from 'node:assert/strict'
import console from 'node:console'
import { mkdir } from 'node:fs/promises'
import { resolve } from 'node:path'
import process from 'node:process'
import test from 'node:test'
import { URL } from 'node:url'

const { chromium } = await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright')
const baseURL = process.env.E2E_BASE_URL ?? 'http://127.0.0.1:5173'
const artifactDirectory = resolve(process.env.E2E_ARTIFACT_DIR ?? '../.artifacts/AICTIQ-102')
const now = '2026-10-19T10:00:00Z'

test(
    'organization default controls and automatic project agent hints',
    { timeout: 120000 },
    async () => {
        console.log('Launching fixture browser')
        const browser = await chromium.launch({
            executablePath: process.env.CHROMIUM_PATH ?? '/usr/bin/chromium',
            headless: true,
            args: ['--no-sandbox'],
        })
        try {
            const context = await browser.newContext({ viewport: { width: 1440, height: 1100 } })
            context.setDefaultTimeout(10000)
            const errors = []
            const unexpected = []
            const writes = []
            const org = {
                id: 'org-fixture',
                slug: 'fixture',
                name: 'Fixture Studio',
                role: 'owner',
                canOperateFactory: true,
                plan: 'selfHosted',
                timeZone: 'UTC',
                weekStart: 'monday',
                membersCanCreateProjects: true,
                createdAt: now,
                version: 1,
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
            const makeAgent = (userId, displayName, isActive = true) => ({
                userId,
                displayName,
                email: `${userId}@agents.invalid`,
                ownerUserId: 'owner-fixture',
                ownerName: 'Jamie Example',
                role: 'member',
                isActive,
                isDefault: false,
                createdAt: now,
                lastActiveAt: null,
                tokenCount: 0,
            })
            let agents = [
                makeAgent('agent-build', 'Build assistant'),
                makeAgent('agent-review', 'Review assistant'),
                makeAgent('agent-disabled', 'Retired assistant', false),
            ]
            let eligible = ['agent-build', 'agent-review']
            let settings = {
                repoSource: 0,
                repoFullName: 'fixture/demo',
                defaultBranch: 'main',
                localPathHint: null,
                defaultAgentId: null,
                version: 1,
            }
            await context.route('**/*', async (route) => {
                const request = route.request()
                const url = new URL(request.url())
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
                        id: 'owner-fixture',
                        email: 'jamie@example.invalid',
                        firstName: 'Jamie',
                        lastName: 'Example',
                        fullName: 'Jamie Example',
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
                        subscribedPlan: null,
                        status: 'none',
                        currentPeriodEnd: null,
                        cancelAtPeriodEnd: false,
                        seats: { human: 1, agent: 3 },
                        paymentFailedAt: null,
                        graceEndsAt: null,
                        readOnly: false,
                        readOnlyReason: null,
                        hasBillingAccount: false,
                        plans: [],
                        evaluation: null,
                    })
                if (path.endsWith('/projects/DEMO/teams')) return json([])
                if (path === '/api/v1/orgs') return json([org])
                if (path === '/api/v1/orgs/fixture') return json(org)
                if (path === '/api/v1/orgs/fixture/projects') return json([project])
                if (path === '/api/v1/orgs/fixture/projects/DEMO') return json(project)
                if (path === '/api/v1/orgs/fixture/agents') return json(agents)
                if (/\/agents\/agent-[^/]+$/.test(path) && request.method() === 'PATCH') {
                    const body = request.postDataJSON()
                    const id = path.split('/').at(-1)
                    writes.push({ id, body })
                    agents = agents.map((agent) => ({
                        ...agent,
                        isDefault: body.isDefault
                            ? agent.userId === id
                            : agent.userId === id
                              ? false
                              : agent.isDefault,
                    }))
                    return json(agents.find((agent) => agent.userId === id))
                }
                if (path.endsWith('/agent-activity/contribution'))
                    return json({ completedByAgents: 0, completedTotal: 0, byAgent: [] })
                if (path.endsWith('/agent-activity')) return json([])
                if (path.endsWith('/projects/DEMO/members'))
                    return json(
                        agents
                            .filter((agent) => eligible.includes(agent.userId))
                            .map((agent) => ({
                                userId: agent.userId,
                                displayName: agent.displayName,
                                email: null,
                                avatarKey: null,
                                isAgent: true,
                                role: 'member',
                                isImplicit: false,
                                addedAt: now,
                            })),
                    )
                if (path.endsWith('/factory-settings')) {
                    if (request.method() === 'PUT') {
                        settings = {
                            ...settings,
                            ...request.postDataJSON(),
                            version: settings.version + 1,
                        }
                        writes.push({ id: 'factory-settings', body: request.postDataJSON() })
                    }
                    return json(settings)
                }
                if (path.endsWith('/github/bindings'))
                    return json([{ id: 'binding-fixture', fullName: 'fixture/demo' }])
                if (path.endsWith('/refinement-settings'))
                    return json({
                        playbookId: null,
                        agentId: null,
                        productDescription: '',
                        writingInstructions: '',
                        namingConventions: '',
                        platforms: '',
                        refinedStateId: null,
                        version: 1,
                    })
                if (path.endsWith('/playbooks') || path.endsWith('/workflows')) return json([])
                unexpected.push(`${request.method()} ${path}`)
                return route.fulfill({ status: 404, contentType: 'application/json', body: '{}' })
            })
            const page = await context.newPage()
            page.on('pageerror', (error) => errors.push(error.message))
            const orgPath = `${baseURL}/o/fixture/settings/agents`
            const factoryPath = `${baseURL}/o/fixture/p/DEMO/settings/factory`
            const hint = page.getByTestId('automatic-agent-hint')
            const waitFor = async (locator, text) => {
                await locator.waitFor({ state: 'visible' })
                await page.waitForFunction(
                    ({ selector, text }) =>
                        document.querySelector(selector)?.textContent?.includes(text),
                    {
                        selector: '[data-testid="automatic-agent-hint"]',
                        text,
                    },
                )
                assert.ok((await locator.textContent()).includes(text))
            }

            await page.goto(orgPath)
            console.log('Organization roster loaded')
            await page.getByRole('button', { name: 'Manage Retired assistant' }).click()
            assert.equal(
                await page
                    .getByRole('menuitem', { name: 'Set as organization default' })
                    .getAttribute('data-disabled'),
                '',
            )
            await page.keyboard.press('Escape')
            await page.getByRole('button', { name: 'Manage Build assistant' }).click()
            await page.getByRole('menuitem', { name: 'Set as organization default' }).click()
            await page.getByText('Default', { exact: true }).waitFor()
            assert.deepEqual(writes.at(-1), { id: 'agent-build', body: { isDefault: true } })
            await page.reload()
            await page.getByText('Default', { exact: true }).waitFor()

            await page.goto(factoryPath)
            await waitFor(hint, 'Runs use Build assistant, the organization default')
            assert.deepEqual(
                await page.locator('#factory-default-agent option').allTextContents(),
                ['Choose at run time', 'Build assistant', 'Review assistant'],
            )
            await page.locator('#factory-default-agent').selectOption('agent-review')
            assert.equal(await hint.count(), 0)
            await page.getByRole('button', { name: 'Save factory settings' }).click()
            await page.getByText('Factory settings saved.', { exact: true }).waitFor()
            assert.equal(writes.at(-1).body.defaultAgentId, 'agent-review')
            await page.reload()
            await page.locator('#factory-default-agent').waitFor()
            assert.equal(await page.locator('#factory-default-agent').inputValue(), 'agent-review')
            assert.equal(await hint.count(), 0)
            await page.locator('#factory-default-agent').selectOption('')
            await page.getByRole('button', { name: 'Save factory settings' }).click()
            await waitFor(hint, 'Runs use Build assistant, the organization default')

            await page.goto(orgPath)
            await page.getByRole('button', { name: 'Manage Build assistant' }).click()
            await page.getByRole('menuitem', { name: 'Clear organization default' }).click()
            await page.waitForFunction(() => !document.querySelector('[role="menu"]'))
            assert.deepEqual(writes.at(-1), { id: 'agent-build', body: { isDefault: false } })
            await page.reload()
            await page.getByRole('button', { name: 'Manage Build assistant' }).waitFor()
            assert.equal(await page.getByText('Default', { exact: true }).count(), 0)
            await page.goto(factoryPath)
            await waitFor(hint, 'Choose an agent when starting a run')
            eligible = ['agent-review']
            await page.reload()
            await waitFor(hint, 'Runs use Review assistant, the only eligible agent')
            agents[0].isDefault = true
            await page.reload()
            await waitFor(hint, 'Runs use Review assistant, the only eligible agent')

            // All behavioral assertions complete before capturing any visual proof.
            eligible = ['agent-build', 'agent-review']
            await page.reload()
            await waitFor(hint, 'Runs use Build assistant, the organization default')
            assert.deepEqual(errors, [])
            assert.deepEqual(unexpected, [])
            await mkdir(artifactDirectory, { recursive: true })
            await page.screenshot({
                path: resolve(artifactDirectory, 'project-automatic-agent.png'),
                fullPage: true,
            })
            await page.goto(orgPath)
            await page.getByText('Default', { exact: true }).waitFor()
            await page.screenshot({
                path: resolve(artifactDirectory, 'organization-default-agent.png'),
                fullPage: true,
            })
            console.log(`Fixture-backed browser checks passed. Screenshots: ${artifactDirectory}`)
        } catch (error) {
            console.error(error)
            throw error
        } finally {
            await browser.close()
        }
    },
)
