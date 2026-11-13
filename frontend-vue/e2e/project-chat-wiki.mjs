/**
 * Fixture-backed browser check for project-chat help. No live API or account data.
 * Start Vite, then run PLAYWRIGHT_MODULE=/path/to/playwright/index.mjs
 * node --test e2e/project-chat-wiki.mjs. Artifacts are written after all checks pass.
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
const artifactDirectory = resolve(process.env.E2E_ARTIFACT_DIR ?? 'node_modules/.artifacts/AICTIQ-131')
const now = '2026-11-13T12:00:00Z'

test('chat explains requested wiki saves, playbook reviews and project visibility at desktop and mobile widths', { timeout: 60000 }, async () => {
  const browser = await chromium.launch({
    executablePath: process.env.CHROMIUM_PATH ?? '/usr/bin/chromium',
    headless: true,
    args: ['--no-sandbox'],
  })
  const context = await browser.newContext({ baseURL, viewport: { width: 1440, height: 1000 } })
  context.setDefaultTimeout(10000)
  const errors = []
  const unexpected = []
  const writes = []
  const screenshots = []
  const org = { id: 'org-fixture', slug: 'fixture', name: 'Fixture Studio', role: 'admin', canOperateFactory: true }
  const project = {
    id: 'project-fixture', key: 'DEMO', name: 'Demo product', description: null,
    visibility: 'private', icon: null, color: null, isArchived: false, createdAt: now,
    role: 'admin', version: 1,
  }
  const wiki = {
    id: 'wiki-fixture', slug: 'translation-reference', title: 'Translation reference', parentId: null,
    position: 0, updatedAt: now, projectId: project.id,
    contentMarkdown: 'The `navigation.home` translation key labels the home link.',
    contentHtml: '', revisionNumber: 1, summary: null, version: 1,
  }
  await context.route('**/*', async (route) => {
    const url = new URL(route.request().url())
    if (url.origin !== new URL(baseURL).origin) return route.abort()
    if (url.pathname.startsWith('/hubs/')) return route.fulfill({ status: 404 })
    if (!url.pathname.startsWith('/api/')) return route.continue()
    const path = url.pathname.replace(/\/$/, '')
    const method = route.request().method()
    if (method !== 'GET') writes.push(`${method} ${path}`)
    const json = (body) => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) })
    if (path === '/api/v1/auth/session') return json({
      id: 'user-fixture', email: 'reviewer@example.invalid', firstName: 'Fixture', lastName: 'Reviewer',
      fullName: 'Fixture Reviewer', roles: [], isAgent: false, avatarKey: null, timeZone: 'UTC', unreadCount: 0,
    })
    if (path === '/api/v1/meta') return json({ version: 'fixture', updateCheck: { enabled: false, repository: null } })
    if (path === '/api/v1/me/notifications') return json([])
    if (path === '/api/v1/me/onboarding') return json({
      tourVersion: 1, status: 'completed', lastStepId: null, completedAt: now, updatedAt: now, version: 1,
    })
    if (path === '/api/v1/orgs') return json([org])
    if (path === '/api/v1/orgs/fixture') return json(org)
    if (path.endsWith('/projects')) return json([project])
    if (path.endsWith('/projects/DEMO')) return json(project)
    if (path.endsWith('/wiki/tree')) return json([wiki])
    if (path.endsWith('/wiki/pages/wiki-fixture/revisions')) return json([])
    if (path.endsWith('/wiki/pages/wiki-fixture')) return json(wiki)
    if (path.endsWith('/projects/DEMO/chat')) return json({ messages: [], runnerOnline: true, runnerName: 'Fixture runner' })
    if (path.endsWith('/factory-settings')) return json({ multiRepoEnabled: false, multiRepoAvailability: 'hidden' })
    if (path.endsWith('/fleet/status')) return json({ availability: 'hidden', active: false, enabled: false, knownTags: [] })
    if (path.endsWith('/billing/subscription')) return json({
      mode: 'self_hosted', enabled: false, plan: 'selfHosted', status: 'none', readOnly: false,
      seats: { human: 1, agent: 1 }, plans: [], evaluation: null,
    })
    if (['/workflows', '/teams', '/labels'].some((suffix) => path.endsWith(suffix))) return json([])
    unexpected.push(`${method} ${path}`)
    return route.fulfill({ status: 404, contentType: 'application/json', body: '{}' })
  })
  const page = await context.newPage()
  page.on('pageerror', (error) => errors.push(error.message))
  try {
    await page.goto('/o/fixture/p/DEMO/wiki/wiki-fixture/translation-reference')
    await page.getByRole('heading', { name: 'Translation reference', exact: true }).waitFor()
    await page.getByTestId('project-chat-button').click()
    const panel = page.getByRole('dialog', { name: 'Project chat', exact: true })
    await panel.getByText(/Ask about the project's code, wiki and items/).waitFor()
    for (const [name, viewport] of [
      ['desktop', { width: 1440, height: 1000 }],
      ['mobile', { width: 390, height: 844 }],
    ]) {
      await page.setViewportSize(viewport)
      const text = await panel.innerText()
      assert.match(text, /ask for a playbook review/)
      assert.match(text, /On your request, the agent can file tickets or save a discussion as a new wiki page/)
      assert.match(text, /Saved pages use normal project wiki visibility and remain after you clear chat history/)
      const box = await panel.boundingBox()
      assert.ok(box && box.x >= 0 && box.y >= 0 && box.x + box.width <= viewport.width && box.y + box.height <= viewport.height,
        `${name}: help stays inside the viewport`)
      assert.equal(await panel.getByTestId('project-chat-send').isDisabled(), true)
      screenshots.push([`project-chat-wiki-${name}.png`, await page.screenshot({ fullPage: false, animations: 'disabled' })])
      console.log(`PASS ${name}: requested actions and wiki visibility help are visible`)
    }
    assert.deepEqual(writes, [], 'opening help creates no pages or tickets')
    assert.deepEqual(unexpected, [], 'all backend responses use known fixtures')
    assert.deepEqual(errors, [], 'no browser errors')
    await mkdir(artifactDirectory, { recursive: true })
    for (const [name, image] of screenshots) await writeFile(resolve(artifactDirectory, name), image)
    console.log(`Screenshots written to ${artifactDirectory}`)
  } finally {
    await browser.close()
  }
})
