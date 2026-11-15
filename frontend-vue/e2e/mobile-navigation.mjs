/**
 * Fixture-backed browser check for mobile navigation. No live API or account data.
 * Start Vite, then run PLAYWRIGHT_MODULE=/path/to/playwright/index.mjs
 * node --test e2e/mobile-navigation.mjs. Artifacts are written after all checks pass.
 */
/* global getComputedStyle, localStorage -- Playwright callbacks execute in the browser. */
import assert from 'node:assert/strict'
import console from 'node:console'
import { mkdir, writeFile } from 'node:fs/promises'
import { resolve } from 'node:path'
import process from 'node:process'
import test from 'node:test'
import { URL } from 'node:url'

const { chromium } = await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright')
const baseURL = process.env.E2E_BASE_URL ?? 'http://127.0.0.1:5173'
const artifactDirectory = resolve(
    process.env.E2E_ARTIFACT_DIR ?? 'node_modules/.artifacts/AICTIQ-133',
)
const now = '2026-11-13T12:00:00Z'

test(
    'mobile navigation is readable and usable across phone widths and densities',
    { timeout: 120000 },
    async () => {
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
            name: 'Fixture organization with a very long name ' + 'Organization'.repeat(5),
            role: 'admin',
            canOperateFactory: true,
        }
        const project = {
            id: 'project-fixture',
            key: 'DEMO',
            name: 'Fixture project ' + 'LongProjectName'.repeat(5),
            description: null,
            visibility: 'private',
            icon: null,
            color: null,
            isArchived: false,
            createdAt: now,
            role: 'admin',
            version: 1,
        }
        const wiki = {
            id: 'wiki-fixture',
            slug: 'translation-reference',
            title: 'Translation reference',
            parentId: null,
            position: 0,
            updatedAt: now,
            projectId: project.id,
            contentMarkdown: 'The `navigation.home` translation key labels the home link.',
            contentHtml: '',
            revisionNumber: 1,
            summary: null,
            version: 1,
        }
        const orgs = [
            org,
            ...Array.from({ length: 24 }, (_, i) => ({
                ...org,
                id: `org-${i}`,
                slug: `fixture-${i}`,
                name: `Fixture ${i} organization ${'LongName'.repeat(8)}`,
            })),
        ]
        const projects = [
            project,
            ...Array.from({ length: 7 }, (_, i) => ({
                ...project,
                id: `project-${i}`,
                key: `DEMO${i}`,
                name: `Fixture ${i} project ${'LongName'.repeat(8)}`,
            })),
        ]
        const teams = Array.from({ length: 2 }, (_, i) => ({
            id: `team-${i}`,
            key: `TEAM${i}`,
            name: `Fixture ${i} team ${'LongName'.repeat(8)}`,
            sprintLengthDays: 14,
            workingDays: [1, 2, 3, 4, 5],
            estimationUnit: 'points',
            timeZone: null,
            isDefault: i === 0,
            memberCount: 1,
            isLead: true,
            version: 1,
        }))
        const settle = (locator) =>
            locator.evaluate((el) =>
                Promise.all(
                    el.getAnimations({ subtree: true }).map((animation) => animation.finished),
                ),
            )
        const fontSize = (locator) => locator.evaluate((el) => getComputedStyle(el).fontSize)
        const fits = async (locator, viewport) => {
            const box = await locator.boundingBox()
            assert.ok(
                box &&
                    box.x >= -1 &&
                    box.y >= -1 &&
                    box.x + box.width <= viewport.width + 1 &&
                    box.y + box.height <= viewport.height + 1,
                `surface fits viewport: ${JSON.stringify(box)}`,
            )
            assert.ok(
                await locator.evaluate((el) => el.scrollWidth <= el.clientWidth + 1),
                'no horizontal scrolling',
            )
        }
        try {
            for (const density of ['comfortable', 'compact']) {
                for (const viewport of [
                    { width: 320, height: 640 },
                    { width: 430, height: 932 },
                ]) {
                    const label = `${viewport.width}-${density}`
                    const context = await browser.newContext({ baseURL, viewport })
                    context.setDefaultTimeout(10000)
                    await context.addInitScript((density) => {
                        localStorage.setItem(
                            'aictiq.ui',
                            JSON.stringify({ theme: 'light', density, sidebarCollapsed: false }),
                        )
                    }, density)
                    await context.route('**/*', async (route) => {
                        const url = new URL(route.request().url())
                        if (url.origin !== new URL(baseURL).origin) return route.abort()
                        if (url.pathname.startsWith('/hubs/')) return route.fulfill({ status: 404 })
                        if (!url.pathname.startsWith('/api/')) return route.continue()
                        const path = url.pathname.replace(/\/$/, '')
                        const method = route.request().method()
                        if (method !== 'GET') writes.push(`${method} ${path}`)
                        const json = (body) =>
                            route.fulfill({
                                status: 200,
                                contentType: 'application/json',
                                body: JSON.stringify(body),
                            })
                        if (path === '/api/v1/auth/session' || path === '/api/v1/me')
                            return json({
                                id: 'user-fixture',
                                email: 'fixture.reviewer.with.a.long.address@example.invalid',
                                firstName: 'Fixture',
                                lastName: 'Reviewer',
                                fullName: 'Fixture Reviewer with a very long account name',
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
                        if (path === '/api/v1/orgs') return json(orgs)
                        if (/^\/api\/v1\/orgs\/fixture(-\d+)?$/.test(path))
                            return json(orgs.find((entry) => path.endsWith(`/${entry.slug}`)))
                        if (path.endsWith('/overview'))
                            return json({
                                openBugs: 0,
                                blockedItems: 0,
                                agentActionsLast24Hours: 0,
                                projects: [],
                                recentActivity: [],
                                agentsAtWork: [],
                            })
                        if (path.endsWith('/projects')) return json(projects)
                        if (/\/projects\/DEMO\d*$/.test(path))
                            return json(projects.find((entry) => path.endsWith(`/${entry.key}`)))
                        if (path.endsWith('/teams')) return json(teams)
                        if (path.endsWith('/wiki/tree')) return json([wiki])
                        if (path.endsWith('/wiki/pages/wiki-fixture/revisions')) return json([])
                        if (path.endsWith('/wiki/pages/wiki-fixture')) return json(wiki)
                        if (path.endsWith('/projects/DEMO/chat'))
                            return json({
                                messages: [],
                                runnerOnline: true,
                                runnerName: 'Fixture runner',
                            })
                        if (path.endsWith('/factory-settings'))
                            return json({
                                multiRepoEnabled: false,
                                multiRepoAvailability: 'hidden',
                            })
                        if (path.endsWith('/fleet/status'))
                            return json({
                                availability: 'hidden',
                                active: false,
                                enabled: false,
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
                        if (['/workflows', '/labels'].some((suffix) => path.endsWith(suffix)))
                            return json([])
                        unexpected.push(`${method} ${path}`)
                        return route.fulfill({
                            status: 404,
                            contentType: 'application/json',
                            body: '{}',
                        })
                    })
                    const page = await context.newPage()
                    page.on('pageerror', (error) => errors.push(error.message))
                    const wikiPath = '/o/fixture/p/DEMO/wiki/wiki-fixture/translation-reference'
                    await page.goto(wikiPath)
                    await page.getByRole('heading', { name: wiki.title, exact: true }).waitFor()
                    const content = page.locator('#main-content .aictiq-markdown').first()
                    const contentFont = await fontSize(content)
                    const drawer = page.getByRole('dialog', { name: 'Navigation', exact: true })
                    const sidebar = page.locator('#mobile-navigation')
                    const nav = sidebar.locator('nav')
                    const open = async () => {
                        await page
                            .getByRole('button', { name: 'Open navigation', exact: true })
                            .click()
                        await sidebar.waitFor()
                        await settle(drawer)
                    }
                    await open()
                    await nav.getByText(teams[0].name, { exact: true }).last().waitFor()
                    assert.equal(
                        await fontSize(nav.getByRole('link', { name: 'My work', exact: true })),
                        '16px',
                    )
                    assert.equal(await fontSize(nav.locator('.font-label').first()), '12px')
                    assert.equal(
                        await fontSize(
                            sidebar.locator('[data-tour="org-switcher"] .tracking-tight'),
                        ),
                        '16px',
                    )
                    assert.equal(
                        await fontSize(
                            sidebar.locator('[data-tour="account-menu"] .truncate.font-medium'),
                        ),
                        '16px',
                    )
                    assert.equal(
                        await fontSize(
                            sidebar.locator('[data-tour="account-menu"] .text-muted-foreground'),
                        ),
                        '13px',
                    )
                    for (const target of await sidebar
                        .locator(
                            'nav a, nav button, [data-tour="org-switcher"], [data-tour="account-menu"]',
                        )
                        .all()) {
                        assert.ok(
                            (await target.boundingBox()).height >= 44,
                            'tap target remains at least 44px in either density',
                        )
                    }
                    await fits(sidebar, viewport)
                    assert.ok(
                        await nav.evaluate((el) => el.scrollHeight > el.clientHeight),
                        'long navigation scrolls',
                    )
                    await nav
                        .getByRole('link', { name: 'Settings', exact: true })
                        .last()
                        .scrollIntoViewIfNeeded()
                    if (label === '320-comfortable')
                        screenshots.push([
                            'drawer-sections-320-comfortable.png',
                            await page.screenshot({ animations: 'disabled' }),
                        ])
                    await nav.evaluate((el) => {
                        el.scrollTop = 0
                    })
                    screenshots.push([
                        `drawer-${label}.png`,
                        await page.screenshot({ animations: 'disabled' }),
                    ])

                    await sidebar.locator('[data-tour="org-switcher"]').click()
                    const orgMenu = page.getByRole('menu')
                    await orgMenu.waitFor()
                    await settle(orgMenu)
                    assert.equal(await fontSize(orgMenu.getByRole('menuitem').first()), '16px')
                    assert.equal(
                        await fontSize(orgMenu.getByText('Switch organization', { exact: true })),
                        '12px',
                    )
                    assert.ok(
                        (await orgMenu.getByRole('menuitem').first().boundingBox()).height >= 44,
                    )
                    await fits(orgMenu, viewport)
                    assert.ok(
                        await orgMenu.evaluate((el) => el.scrollHeight > el.clientHeight),
                        'many organizations scroll',
                    )
                    const captureMenus =
                        (density === 'comfortable' && viewport.width === 320) ||
                        (density === 'compact' && viewport.width === 430)
                    if (captureMenus)
                        screenshots.push([
                            `organizations-${label}.png`,
                            await page.screenshot({ animations: 'disabled' }),
                        ])
                    await orgMenu
                        .getByRole('menuitem', { name: 'New organization…', exact: true })
                        .click()
                    const creating = page.getByRole('dialog', {
                        name: 'New organization',
                        exact: true,
                    })
                    await creating.waitFor()
                    await creating.getByRole('button', { name: 'Cancel', exact: true }).click()
                    await creating.waitFor({ state: 'hidden' })
                    await sidebar.waitFor()

                    await sidebar.locator('[data-tour="account-menu"]').click()
                    const accountMenu = page.getByRole('menu')
                    await accountMenu.waitFor()
                    await settle(accountMenu)
                    assert.equal(await fontSize(accountMenu.getByRole('menuitem').first()), '16px')
                    assert.equal(
                        await fontSize(
                            accountMenu.locator('[data-slot="dropdown-menu-label"] .font-medium'),
                        ),
                        '16px',
                    )
                    assert.equal(
                        await fontSize(
                            accountMenu.locator('[data-slot="dropdown-menu-label"] .font-normal'),
                        ),
                        '13px',
                    )
                    for (const item of await accountMenu.getByRole('menuitem').all())
                        assert.ok((await item.boundingBox()).height >= 44)
                    await fits(accountMenu, viewport)
                    if (captureMenus)
                        screenshots.push([
                            `account-${label}.png`,
                            await page.screenshot({ animations: 'disabled' }),
                        ])
                    await accountMenu
                        .getByRole('menuitem', { name: 'Account settings', exact: true })
                        .click()
                    await page.waitForURL('**/settings/profile')
                    await drawer.waitFor({ state: 'hidden' })
                    await page
                        .getByRole('heading', { name: 'Profile picture', exact: true })
                        .waitFor()
                    await page.goto(wikiPath)
                    await page.getByRole('heading', { name: wiki.title, exact: true }).waitFor()
                    assert.equal(
                        await fontSize(content),
                        contentFont,
                        'content size remains unchanged',
                    )
                    await open()
                    await nav.getByRole('button', { name: new RegExp(projects[1].name) }).click()
                    await page.waitForURL((url) => url.pathname === '/o/fixture/p/DEMO0/wiki')
                    await drawer.waitFor({ state: 'hidden' })
                    await open()
                    await nav.getByRole('button', { name: teams[1].name, exact: true }).click()
                    assert.equal(
                        await page.evaluate(() =>
                            localStorage.getItem('aictiq.team.fixture.DEMO0'),
                        ),
                        teams[1].id,
                    )
                    await nav.getByRole('link', { name: 'Wiki', exact: true }).click()
                    await drawer.waitFor({ state: 'hidden' })
                    await open()
                    await sidebar.locator('[data-tour="org-switcher"]').click()
                    await page.getByRole('menuitem', { name: orgs[1].name, exact: true }).click()
                    await page.waitForURL(baseURL + '/')
                    await drawer.waitFor({ state: 'hidden' })
                    assert.equal(
                        await page.evaluate(() => localStorage.getItem('aictiq.org')),
                        orgs[1].slug,
                    )
                    // Home lets the shell handle Escape; Wiki already owns that shortcut.
                    await page
                        .getByRole('heading', { name: 'Welcome, Fixture', exact: true })
                        .waitFor()
                    await open()
                    await page.keyboard.press('Escape')
                    await drawer.waitFor({ state: 'hidden' })
                    await open()
                    await drawer.getByRole('button', { name: 'Close', exact: true }).click()
                    await drawer.waitFor({ state: 'hidden' })
                    await open()
                    await page.mouse.click(viewport.width - 4, viewport.height / 2)
                    await drawer.waitFor({ state: 'hidden' })
                    await page.goto(wikiPath)
                    await page.getByRole('heading', { name: wiki.title, exact: true }).waitFor()

                    await open()
                    await page.setViewportSize({ width: 1440, height: 1000 })
                    await drawer.waitFor({ state: 'hidden' })
                    const desktop = page.getByRole('complementary', { name: 'Primary' })
                    await desktop.waitFor()
                    assert.equal(
                        await fontSize(desktop.getByRole('link', { name: 'My work', exact: true })),
                        '12.5px',
                    )
                    assert.equal(
                        await fontSize(desktop.locator('nav .font-label').first()),
                        '9.5px',
                    )
                    assert.equal(
                        await fontSize(
                            desktop.locator('[data-tour="org-switcher"] .tracking-tight'),
                        ),
                        '13px',
                    )
                    assert.equal(await fontSize(content), contentFont)
                    await desktop.locator('[data-tour="account-menu"]').click()
                    assert.equal(
                        await page
                            .getByRole('menu')
                            .getAttribute('class')
                            .then((value) => value.includes('mobile-navigation-menu')),
                        false,
                    )
                    assert.equal(
                        await fontSize(page.getByRole('menuitem').first()),
                        `${(density === 'comfortable' ? 13.5 : 12.5) * 0.875}px`,
                    )
                    await page.keyboard.press('Escape')
                    if (label === '430-comfortable')
                        screenshots.push([
                            'desktop-navigation.png',
                            await page.screenshot({ animations: 'disabled' }),
                        ])
                    console.log(
                        `PASS ${label}: text, containment, scrolling, actions, close behavior and desktop sizing`,
                    )
                    await context.close()
                }
            }
            assert.deepEqual(writes, [], 'navigation creates no backend writes')
            assert.deepEqual(unexpected, [], 'all backend responses use explicit fixtures')
            assert.deepEqual(errors, [], 'no browser errors')
            await mkdir(artifactDirectory, { recursive: true })
            for (const [name, image] of screenshots)
                await writeFile(resolve(artifactDirectory, name), image)
            console.log(`PASS; ${screenshots.length} screenshots saved to ${artifactDirectory}`)
        } finally {
            await browser.close()
        }
    },
)
