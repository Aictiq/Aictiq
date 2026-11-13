/**
 * Fixture-backed browser test; no live backend or account data.
 * Start Vite, then run PLAYWRIGHT_MODULE=/path/to/playwright/index.mjs
 * node --test e2e/emoji-picker.mjs. Screenshots are written after all checks pass.
 */
/* global document, window -- Playwright callbacks execute in the browser. */
import assert from 'node:assert/strict'
import console from 'node:console'
import { mkdir, writeFile } from 'node:fs/promises'
import { resolve } from 'node:path'
import process from 'node:process'
import test from 'node:test'
import { URL } from 'node:url'

const { chromium } = await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright')
const baseURL = process.env.E2E_BASE_URL ?? 'http://127.0.0.1:5173'
const artifactDirectory = resolve(process.env.E2E_ARTIFACT_DIR ?? 'node_modules/.artifacts/AICTIQ-132')
const now = new Date().toISOString()
const paged = (items = []) => ({ items, page: 1, pageSize: 50, totalCount: items.length })

test('emoji pickers preserve selection, focus, undo and saved Markdown', { timeout: 120000 }, async () => {
  const browser = await chromium.launch({
    executablePath: process.env.CHROMIUM_PATH ?? '/usr/bin/chromium',
    headless: true,
    args: ['--no-sandbox'],
  })
  const context = await browser.newContext({ baseURL, viewport: { width: 1440, height: 1100 } })
  context.setDefaultTimeout(10000)
  const errors = []
  const unexpected = []
  const screenshots = []
  let itemSaves = 0
  let templateSaves = 0
  const comments = []
  const wiki = {
    id: 'wiki-fixture', slug: 'release', title: 'Release checklist', parentId: null,
    position: 0, updatedAt: now, projectId: 'project-fixture', contentMarkdown: 'Release ready ',
    contentHtml: '', revisionNumber: 1, summary: null, version: 1,
  }
  const playbook = {
    id: 'playbook-fixture', projectId: 'project-fixture', name: 'Implement release',
    wikiPageId: 'playbook-page-fixture', harness: 'codex', onSuccessStateId: null,
    onFailureStateId: null, workOnDefaultBranch: false, maxMinutes: 60, isDefault: true,
    stagePlaybookIds: [], createdBy: 'user-fixture', createdAt: now, updatedAt: now, version: 1,
  }
  let instructions = 'Review checklist '
  const org = { id: 'org-fixture', slug: 'fixture', name: 'Fixture Studio', role: 'admin', canOperateFactory: false }
  const project = {
    id: 'project-fixture', key: 'DEMO', name: 'Demo product', description: null,
    visibility: 'private', icon: null, color: null, isArchived: false, createdAt: now,
    role: 'admin', version: 1,
  }
  const item = {
    id: 'item-fixture', key: 'DEMO-1', type: 'story', title: 'Review the release checklist',
    stateId: 'state-fixture', stateCategory: 'active', priority: 'medium',
    descriptionMarkdown: 'Before selected after', descriptionHtml: '', assigneeId: null,
    teamId: null, sprintId: null, parentId: null, points: null, estimateHours: null,
    remainingHours: null, completedHours: null, dueDate: null, version: 1, labels: [],
    updatedAt: now, isWatching: false, watcherCount: 0, claimedBy: null,
    claimedAt: null, claimHeartbeatAt: null,
    rollup: { totalCount: 0, completedCount: 0, pointsTotal: 0, pointsCompleted: 0, remainingHours: 0 },
  }
  const template = {
    id: 'template-fixture', type: 'bug', name: 'Release checklist',
    descriptionMarkdown: '## Before selected after', defaultLabelIds: [],
    defaultPriority: null, isDefault: true, version: 1,
  }
  await context.route('**/*', async (route) => {
    const url = new URL(route.request().url())
    if (url.origin !== new URL(baseURL).origin) return route.abort()
    if (url.pathname.startsWith('/hubs/')) return route.fulfill({ status: 404 })
    if (!url.pathname.startsWith('/api/')) return route.continue()
    const path = url.pathname.replace(/\/$/, '')
    const method = route.request().method()
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
    if (path.endsWith('/items/DEMO-1')) {
      if (method === 'PATCH') { Object.assign(item, route.request().postDataJSON(), { version: item.version + 1 }); itemSaves++ }
      return json(item)
    }
    if (path.endsWith('/templates/template-fixture')) {
      if (method === 'PATCH') { Object.assign(template, route.request().postDataJSON(), { version: template.version + 1 }); templateSaves++ }
      return json(template)
    }
    if (path.endsWith('/templates')) return json([template])
    if (path.endsWith('/wiki/tree')) return json([wiki])
    if (path.endsWith('/wiki/pages/wiki-fixture/revisions')) return json([])
    if (path.endsWith('/wiki/pages/wiki-fixture')) {
      if (method === 'PATCH') {
        const body = route.request().postDataJSON()
        wiki.contentMarkdown = body.contentMd
        wiki.version++
      }
      return json(wiki)
    }
    if (path.endsWith('/playbooks/playbook-fixture/instructions')) return json({
      wikiPageId: playbook.wikiPageId, pageTitle: playbook.name, markdown: instructions, inFactorySection: true,
    })
    if (path.endsWith('/playbooks/playbook-fixture')) {
      if (method === 'PATCH') {
        const body = route.request().postDataJSON()
        instructions = body.instructionsMarkdown
        Object.assign(playbook, body, { version: playbook.version + 1 })
      }
      return json(playbook)
    }
    if (path.endsWith('/playbooks')) return json([playbook])
    if (path.endsWith('/factory-settings')) return json({ multiRepoEnabled: false, multiRepoAvailability: 'hidden' })
    if (path.endsWith('/fleet/status')) return json({ availability: 'hidden', active: false, enabled: false, knownTags: [] })
    if (path.endsWith('/items/DEMO-1/watch')) return json([])
    if (/\/comments\/comment-\d+$/.test(path)) {
      const comment = comments.find((entry) => path.endsWith(`/${entry.id}`))
      if (method === 'PATCH') Object.assign(comment, route.request().postDataJSON(), { editedAt: now })
      return json(comment)
    }
    if (path.endsWith('/comments')) {
      if (method === 'POST') {
        const body = route.request().postDataJSON()
        const comment = {
          id: `comment-${comments.length + 1}`, ...body,
          author: { id: 'user-fixture', displayName: 'Fixture Reviewer', isAgent: false, avatarKey: null },
          bodyHtml: '', createdAt: now, editedAt: null, deletedAt: null,
          mentions: [], canReact: true, reactions: [], revisions: [],
        }
        comments.push(comment)
        return json(comment)
      }
      return json({ ...paged(comments), total: comments.length })
    }
    if (path.endsWith('/history') || path === '/api/v1/orgs/fixture/members') return json(paged())
    if (['/relations', '/links', '/watchers', '/children', '/workflows', '/teams', '/labels', '/github/bindings', '/projects/DEMO/members'].some((suffix) => path.endsWith(suffix))) return json([])
    if (path.endsWith('/billing/subscription')) return json({
      mode: 'self_hosted', enabled: false, plan: 'selfHosted', status: 'none', readOnly: false,
      seats: { human: 1, agent: 1 }, plans: [], evaluation: null,
    })
    unexpected.push(`${method} ${path}`)
    return route.fulfill({ status: 404, contentType: 'application/json', body: '{}' })
  })
  const page = await context.newPage()
  try {
    page.on('pageerror', (error) => errors.push(error.message))
    const popup = page.getByRole('dialog', { name: 'Insert emoji', exact: true })
    async function appendEmoji(scope, name, text) {
      const input = scope.locator('.tiptap')
      await input.waitFor()
      await input.press('Control+End')
      await scope.getByRole('button', { name: 'Insert emoji', exact: true }).click()
      await popup.waitFor()
      assert.equal(await popup.getByRole('button').count(), 30)
      await popup.getByRole('button', { name, exact: true }).click()
      await page.waitForFunction((element) => document.activeElement === element, await input.elementHandle())
      assert.ok((await input.innerText()).endsWith(text))
    }
    await page.goto('/o/fixture/p/DEMO/items/DEMO-1')
    const editor = page.locator('.tiptap').first()
    await editor.waitFor()
    const trigger = page.getByRole('button', { name: 'Insert emoji', exact: true }).first()
    await editor.click()
    await editor.press('Control+Home')
    for (let i = 0; i < 7; i++) await editor.press('ArrowRight')
    for (let i = 0; i < 8; i++) await editor.press('Shift+ArrowRight')
    assert.equal(await page.evaluate(() => window.getSelection().toString()), 'selected')
    await trigger.click()
    await popup.waitFor()
    const richChoices = await popup.getByRole('button').allTextContents()
    assert.equal(richChoices.length, 30)
    await popup.getByRole('button', { name: 'Rocket', exact: true }).click()
    assert.equal(await editor.innerText(), 'Before 🚀 after')
    assert.equal(await editor.evaluate((element) => document.activeElement === element), true)
    assert.equal(itemSaves, 0, 'emoji buttons must not save the item')
    await editor.press('Control+z')
    assert.equal(await editor.innerText(), 'Before selected after')
    await editor.press('Control+Shift+z')
    assert.equal(await editor.innerText(), 'Before 🚀 after')
    await trigger.focus()
    await trigger.press('Enter')
    await popup.waitFor()
    await page.keyboard.press('Escape')
    await popup.waitFor({ state: 'hidden' })
    assert.equal(await editor.innerText(), 'Before 🚀 after')
    await page.getByTestId('item-save').filter({ visible: true }).click()
    await page.waitForFunction(() => !document.body.innerText.includes('Unsaved'))
    assert.equal(itemSaves, 1)
    assert.equal(item.descriptionMarkdown, 'Before 🚀 after')
    await page.reload()
    await editor.waitFor()
    assert.equal(await editor.innerText(), 'Before 🚀 after')
    await trigger.click()
    await popup.waitFor()
    screenshots.push(['item-emoji-picker.png', await page.screenshot({ animations: 'disabled' })])
    await page.keyboard.press('Escape')
    console.log('PASS rich editor selected text, focus, undo/redo, keyboard dismissal and save/reopen')

    const commentForm = page.locator('form').filter({ has: page.getByRole('button', { name: 'Send', exact: true }) })
    await commentForm.locator('.tiptap').fill('Release approved ')
    await appendEmoji(commentForm, 'Check mark', '✅')
    assert.equal(comments.length, 0, 'picker must not submit a comment')
    await commentForm.getByRole('button', { name: 'Send', exact: true }).click()
    const rootComment = page.locator('#comment-comment-1')
    await rootComment.waitFor()
    assert.match(await rootComment.innerText(), /Release approved ✅/u)
    await rootComment.getByRole('button', { name: /^Reply to /u }).click()
    const replyForm = page.getByTestId('reply-form')
    await replyForm.locator('.tiptap').fill('Acknowledged ')
    await appendEmoji(replyForm, 'Thumbs up', '👍')
    assert.equal(comments.length, 1, 'picker must not submit a reply')
    await replyForm.getByRole('button', { name: 'Reply', exact: true }).click()
    await page.locator('#comment-comment-2').waitFor()
    for (const [id, name, text] of [['comment-1', 'Celebration', '🎉'], ['comment-2', 'Handshake', '🤝']]) {
      const entry = page.locator(`#comment-${id}`)
      await entry.getByRole('button', { name: 'Edit', exact: true }).click()
      const editForm = entry.getByTestId('edit-comment-form')
      await appendEmoji(editForm, name, text)
      await editForm.getByRole('button', { name: 'Save', exact: true }).click()
      await editForm.waitFor({ state: 'hidden' })
    }
    await page.reload()
    assert.match(await rootComment.innerText(), /Release approved ✅🎉/u)
    assert.match(await page.locator('#comment-comment-2').innerText(), /Acknowledged 👍🤝/u)
    console.log('PASS comments and replies insert, edit, render and reopen Unicode emojis')

    await page.goto('/o/fixture/p/DEMO/settings/templates')
    await page.getByRole('button', { name: 'Edit', exact: true }).click()
    const textarea = page.getByRole('textbox', { name: 'Template description', exact: true })
    assert.equal(await textarea.inputValue(), '## Before selected after')
    await textarea.focus()
    await textarea.evaluate((element) => element.setSelectionRange(10, 18))
    await page.getByRole('button', { name: 'Insert emoji', exact: true }).click()
    await popup.waitFor()
    assert.deepEqual(await popup.getByRole('button').allTextContents(), richChoices)
    await popup.getByRole('button', { name: 'Unchecked box', exact: true }).click()
    assert.equal(await textarea.inputValue(), '## Before ☐ after')
    assert.equal(await textarea.evaluate((element) => document.activeElement === element), true)
    assert.equal(templateSaves, 0, 'emoji buttons must not submit the template form')
    await textarea.press('Control+z')
    assert.equal(await textarea.inputValue(), '## Before selected after')
    await textarea.press('Control+Shift+z')
    assert.equal(await textarea.inputValue(), '## Before ☐ after')
    await page.getByRole('button', { name: 'Insert emoji', exact: true }).focus()
    await page.keyboard.press('Enter')
    await popup.waitFor()
    await page.keyboard.press('ArrowRight')
    await page.keyboard.press('Enter')
    await popup.waitFor({ state: 'hidden' })
    assert.match(await textarea.inputValue(), /☐.*☑️|☑️.*☐/u)
    const savedTemplate = await textarea.inputValue()
    await page.getByRole('button', { name: 'Save template', exact: true }).click()
    await page.getByRole('heading', { name: 'New template', exact: true }).waitFor()
    assert.equal(templateSaves, 1)
    assert.equal(template.descriptionMarkdown, savedTemplate)
    await page.reload()
    await page.getByRole('button', { name: 'Edit', exact: true }).click()
    assert.equal(await textarea.inputValue(), savedTemplate)
    await page.getByRole('button', { name: 'Insert emoji', exact: true }).click()
    await popup.waitFor()
    screenshots.push(['template-emoji-picker.png', await page.screenshot({ animations: 'disabled' })])
    await page.setViewportSize({ width: 390, height: 844 })
    const bounds = await popup.boundingBox()
    assert.ok(bounds && bounds.x >= 0 && bounds.x + bounds.width <= 391, 'picker fits mobile viewport')
    screenshots.push(['template-emoji-picker-mobile.png', await page.screenshot({ animations: 'disabled' })])
    await page.setViewportSize({ width: 1440, height: 1100 })
    await page.goto('/o/fixture/p/DEMO/wiki/wiki-fixture/release')
    await page.getByRole('button', { name: 'Edit', exact: true }).click()
    await appendEmoji(page.locator('main'), 'Check mark', '✅')
    await page.getByRole('button', { name: 'Save', exact: true }).click()
    await page.getByRole('button', { name: 'Edit', exact: true }).waitFor()
    assert.equal(wiki.contentMarkdown, 'Release ready ✅')
    assert.match(await page.locator('.aictiq-markdown').innerText(), /Release ready ✅/u)
    await page.reload()
    await page.getByRole('button', { name: 'Edit', exact: true }).click()
    assert.equal(await page.locator('.tiptap').innerText(), 'Release ready ✅')
    console.log('PASS wiki saves, renders and reopens Unicode emojis')

    org.canOperateFactory = true
    await page.goto('/o/fixture/factory/playbooks')
    await page.getByRole('button', { name: 'Manage Implement release', exact: true }).click()
    await page.getByRole('menuitem', { name: 'Edit', exact: true }).click()
    const playbookDialog = page.getByRole('dialog', { name: 'Edit playbook', exact: true })
    await appendEmoji(playbookDialog, 'Rocket', '🚀')
    assert.equal(await playbookDialog.isVisible(), true, 'picker selection keeps surrounding dialog open')
    await playbookDialog.getByRole('button', { name: 'Save changes', exact: true }).click()
    await playbookDialog.waitFor({ state: 'hidden' })
    assert.equal(instructions, 'Review checklist 🚀')
    await page.reload()
    await page.getByRole('button', { name: 'Manage Implement release', exact: true }).click()
    await page.getByRole('menuitem', { name: 'Edit', exact: true }).click()
    await playbookDialog.locator('.tiptap').waitFor()
    assert.equal(await playbookDialog.locator('.tiptap').innerText(), 'Review checklist 🚀')
    await playbookDialog.getByRole('button', { name: 'Insert emoji', exact: true }).click()
    await popup.waitFor()
    screenshots.push(['playbook-emoji-picker.png', await page.screenshot({ animations: 'disabled' })])
    await page.keyboard.press('Escape')
    await popup.waitFor({ state: 'hidden' })
    assert.equal(await playbookDialog.isVisible(), true, 'Escape dismisses picker without closing surrounding dialog')
    console.log('PASS playbook dialog saves/reopens Unicode emojis and dismisses only the picker')
    assert.deepEqual(errors, [])
    assert.deepEqual(unexpected, [])
    await mkdir(artifactDirectory, { recursive: true })
    for (const [name, bytes] of screenshots) await writeFile(resolve(artifactDirectory, name), bytes)
    console.log(`PASS raw template selection, native undo/redo, keyboard insertion, save/reopen and mobile; ${screenshots.length} screenshots in ${artifactDirectory}`)
  } catch (error) {
    console.error(await page.locator('[role="dialog"]').evaluateAll((elements) => elements.map((element) => element.outerHTML)))
    console.error({ errors, unexpected })
    throw error
  } finally {
    await browser.close()
  }
})
