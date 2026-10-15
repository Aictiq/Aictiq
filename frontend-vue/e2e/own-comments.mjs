// Run against a local, disposable seeded API and Vite server. No production data.
// PLAYWRIGHT_MODULE=/path/to/playwright-core/index.mjs node e2e/own-comments.mjs
// Optional: E2E_BASE_URL, E2E_ADMIN_EMAIL, E2E_ADMIN_PASSWORD, E2E_ORG_SLUG,
// E2E_SCREENSHOT_DIR. Screenshots are written only after every assertion passes.
import assert from 'node:assert/strict'
import console from 'node:console'
import { mkdir, writeFile } from 'node:fs/promises'
import process from 'node:process'
import { setTimeout } from 'node:timers'

const { chromium } = await import(process.env.PLAYWRIGHT_MODULE || 'playwright')
const baseURL = process.env.E2E_BASE_URL || 'http://localhost:5173'
const slug = process.env.E2E_ORG_SLUG || 'aictiq-dev'
const screenshotDir = process.env.E2E_SCREENSHOT_DIR || '/tmp/aictiq97/shots'
const stamp = Date.now().toString(36).toUpperCase()
const projectKey = `C${stamp}`.slice(-10)
const browser = await chromium.launch({ channel: 'chromium', args: ['--disable-dev-shm-usage'] })
const author = await browser.newContext({ baseURL, viewport: { width: 1440, height: 1080 } })
const observer = await browser.newContext({ baseURL, viewport: { width: 1440, height: 1080 } })
const screenshots = []
const headers = { 'X-Aictiq-Request': '1' }
async function api(context, method, path, data, expectedStatus) {
  const response = await context.request.fetch(`/api/v1${path}`, { method, data, headers })
  if (expectedStatus) assert.equal(response.status(), expectedStatus, `${method} ${path}`)
  else assert.ok(response.ok(), `${method} ${path}: ${response.status()} ${await response.text()}`)
  return response.status() === 204 ? null : response.json()
}
async function until(check, message) {
  const deadline = Date.now() + 15000
  do {
    if (await check()) { console.log(`PASS ${message}`); return }
    await new Promise((resolve) => setTimeout(resolve, 100))
  } while (Date.now() < deadline)
  throw new Error(message)
}
async function capture(page, name) {
  screenshots.push([name, await page.screenshot({ fullPage: false, animations: 'disabled' })])
}
try {
  await api(author, 'POST', '/auth/login', {
    email: process.env.E2E_ADMIN_EMAIL || 'admin@aictiq.local',
    password: process.env.E2E_ADMIN_PASSWORD || 'AdminPassword123!',
  })
  const project = await api(author, 'POST', `/orgs/${slug}/projects`, {
    name: `Comment editing fixture ${stamp}`, key: projectKey, visibility: 'organization',
  })
  const item = await api(author, 'POST', `/orgs/${slug}/projects/${project.key}/items/`, {
    type: 'story', title: 'Review the release checklist', descriptionMarkdown: 'Fixture data for comment editing and deletion.',
  })
  const invitation = await api(author, 'POST', `/orgs/${slug}/invitations/`, {
    email: `comment-reviewer-${stamp.toLowerCase()}@example.test`, role: 'admin',
  })
  await api(observer, 'POST', '/auth/register', {
    email: invitation.invitation.email, password: 'FixtureReviewer123!',
    firstName: 'Fixture', lastName: 'Reviewer', invitationToken: invitation.token,
  })
  await api(observer, 'POST', `/invitations/${invitation.token}/accept`)
  const commentsPath = `/orgs/${slug}/items/${item.key}/comments`
  const root = await api(author, 'POST', commentsPath, {
    bodyMarkdown: 'The release checklist is ready for review.\n\n- Confirm the release date\n- Notify the team',
  })
  const reply = await api(observer, 'POST', commentsPath, {
    bodyMarkdown: 'I will verify the release date and keep this reply in the discussion.', parentCommentId: root.id,
  })
  const other = await api(observer, 'POST', commentsPath, {
    bodyMarkdown: 'Reviewer note: the announcement draft is ready.',
  })
  await api(observer, 'PUT', `${commentsPath}/${root.id}/reactions`, { emoji: '👍' })
  const page = await author.newPage()
  const livePage = await observer.newPage()
  const realtime = new Map()
  for (const current of [page, livePage]) {
    const state = { handshaken: false, joined: false }
    realtime.set(current, state)
    current.on('websocket', (socket) => {
      const joins = new Set()
      socket.on('framesent', ({ payload }) => {
        for (const packet of String(payload).split('\u001e').filter(Boolean)) {
          const frame = JSON.parse(packet)
          if (frame.target === 'JoinProject' && frame.arguments?.[0] === project.key) joins.add(frame.invocationId)
        }
      })
      socket.on('framereceived', ({ payload }) => {
        for (const packet of String(payload).split('\u001e').filter(Boolean)) {
          const frame = JSON.parse(packet)
          if (Object.keys(frame).length === 0) state.handshaken = true
          if (frame.type === 3 && joins.has(frame.invocationId) && !frame.error) state.joined = true
        }
      })
    })
  }
  let observerNavigations = 0
  livePage.on('framenavigated', (frame) => { if (frame === livePage.mainFrame()) observerNavigations++ })
  const path = `/o/${slug}/p/${project.key}/items/${item.key}`
  // Initial page mounts create default project board/presence rows; load sessions
  // sequentially. Warm up the notification hub on a shell-only page first so a new
  // fixture user's notification/project hubs do not race to create their presence.
  for (const current of [page, livePage]) {
    await current.goto('/about')
    await current.getByRole('heading', { name: 'About Aictiq' }).waitFor()
    await until(() => realtime.get(current).handshaken, 'notification hub handshake completed')
    const skipTour = current.getByRole('button', { name: 'Skip tour', exact: true })
    try { await skipTour.waitFor({ timeout: 3000 }); await skipTour.click() } catch { /* Returning fixture admin already dismissed it. */ }
    await current.goto(path)
    await current.locator(`#comment-${root.id}`).waitFor()
    await until(() => realtime.get(current).joined, 'project hub JoinProject completed successfully')
  }
  const ownComment = page.locator(`#comment-${root.id}`)
  const liveComment = livePage.locator(`#comment-${root.id}`)
  await ownComment.getByRole('button', { name: 'Edit', exact: true }).waitFor()
  await liveComment.waitFor()
  assert.equal(await liveComment.getByRole('button', { name: 'Edit', exact: true }).count(), 0, 'org admin cannot edit another author')
  assert.equal(await liveComment.getByRole('button', { name: 'Delete', exact: true }).count(), 0, 'org admin cannot delete another author')
  assert.equal(await page.locator(`#comment-${other.id}`).getByRole('button', { name: 'Edit', exact: true }).count(), 0)
  await api(observer, 'PATCH', `${commentsPath}/${root.id}`, { bodyMarkdown: 'Forbidden edit' }, 403)
  await api(observer, 'DELETE', `${commentsPath}/${root.id}`, undefined, 403)
  const observerInitialNavigations = observerNavigations
  console.log('PASS only each comment author has edit and delete controls; another org admin is denied by API')
  await ownComment.scrollIntoViewIfNeeded()
  await ownComment.getByRole('button', { name: 'Edit', exact: true }).click()
  const form = ownComment.getByTestId('edit-comment-form')
  const editor = form.locator('[contenteditable="true"]')
  await editor.waitFor()
  assert.match(await editor.innerText(), /The release checklist is ready for review/)
  await editor.fill('')
  await until(() => form.getByRole('button', { name: 'Save', exact: true }).isDisabled(), 'empty edit cannot be saved')
  assert.match(await form.getByRole('alert').innerText(), /1–20,000/)
  await editor.fill('x'.repeat(20001))
  await until(() => form.getByRole('button', { name: 'Save', exact: true }).isDisabled(), 'overlong edit cannot be saved')
  await editor.fill('A draft that should be cancelled.')
  await form.getByRole('button', { name: 'Cancel', exact: true }).click()
  assert.match(await ownComment.innerText(), /The release checklist is ready for review/)
  assert.doesNotMatch(await ownComment.innerText(), /A draft that should be cancelled/)
  console.log('PASS cancel preserves original comment')
  await ownComment.getByRole('button', { name: 'Edit', exact: true }).click()
  await editor.fill('The release checklist is approved.\n\nConfirm the release date, then notify the team.')
  await capture(page, '1-edit-own-comment.png')
  await form.getByRole('button', { name: 'Save', exact: true }).click()
  await until(async () => (await ownComment.innerText()).includes('The release checklist is approved.') && (await form.count()) === 0, 'author sees saved comment')
  await until(async () => (await liveComment.innerText()).includes('The release checklist is approved.'), 'second session receives edited comment without reload')
  assert.match(await ownComment.innerText(), /\(edited\)/)
  const edited = (await api(author, 'GET', commentsPath)).items.find((entry) => entry.id === root.id)
  assert.equal(edited.reactions[0].emoji, '👍', 'reaction survives editing')
  assert.ok(edited.revisions.length >= 1, 'edit retains revision history')
  await ownComment.scrollIntoViewIfNeeded()
  await capture(page, '2-edited-comment.png')
  await ownComment.getByRole('button', { name: 'Delete', exact: true }).click()
  const dialog = page.getByRole('dialog', { name: 'Delete your comment?' })
  await dialog.waitFor()
  assert.match(await dialog.innerText(), /Replies in the thread will stay/)
  await dialog.getByRole('button', { name: 'Cancel', exact: true }).click()
  assert.match(await ownComment.innerText(), /The release checklist is approved/)
  await ownComment.getByRole('button', { name: 'Delete', exact: true }).click()
  await dialog.waitFor()
  await capture(page, '3-delete-confirmation.png')
  await dialog.getByRole('button', { name: 'Delete comment', exact: true }).click()
  await until(async () => (await ownComment.innerText()).includes('This comment was deleted.'), 'author sees deleted placeholder')
  await until(async () => (await liveComment.innerText()).includes('This comment was deleted.'), 'second session receives deletion without reload')
  await dialog.waitFor({ state: 'hidden' })
  assert.equal(await ownComment.getByRole('button', { name: 'Edit', exact: true }).count(), 0)
  assert.equal(await ownComment.getByRole('button', { name: 'Delete', exact: true }).count(), 0)
  assert.match(await page.locator(`#comment-${reply.id}`).innerText(), /keep this reply in the discussion/)
  assert.match(await livePage.locator(`#comment-${reply.id}`).innerText(), /keep this reply in the discussion/)
  assert.equal(observerNavigations, observerInitialNavigations, 'second session never reloads or navigates during comment mutations')
  await ownComment.scrollIntoViewIfNeeded()
  await capture(page, '4-deleted-comment-with-replies.png')
  await api(author, 'POST', `/orgs/${slug}/projects/${project.key}/archive`)
  await livePage.reload()
  await livePage.locator(`#comment-${other.id}`).waitFor()
  await until(async () => await livePage.locator(`#comment-${other.id}`).getByRole('button', { name: 'Edit', exact: true }).count() === 0, 'archived project hides own comment edit controls')
  assert.equal(await livePage.locator(`#comment-${other.id}`).getByRole('button', { name: 'Delete', exact: true }).count(), 0)
  await mkdir(screenshotDir, { recursive: true })
  for (const [name, bytes] of screenshots) await writeFile(`${screenshotDir}/${name}`, bytes)
  console.log(`PASS own-comments browser e2e; ${screenshots.length} screenshots in ${screenshotDir}`)
} catch (error) {
  for (const context of [author, observer]) {
    for (const page of context.pages()) console.error(page.url(), await page.locator('body').innerText())
  }
  throw error
} finally {
  await browser.close()
}
