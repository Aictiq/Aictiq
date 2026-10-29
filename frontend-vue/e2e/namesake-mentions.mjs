// Run against a local, disposable seeded API and Vite server. No production data.
// PLAYWRIGHT_MODULE=/path/to/playwright-core/index.mjs node e2e/namesake-mentions.mjs
// Optional: E2E_BASE_URL, E2E_ADMIN_EMAIL, E2E_ADMIN_PASSWORD, E2E_ORG_SLUG, E2E_MAILPIT_URL,
// E2E_SCREENSHOT_DIR. Screenshots are written only after every assertion passes.
import assert from 'node:assert/strict'
import console from 'node:console'
import { mkdir, writeFile } from 'node:fs/promises'
import process from 'node:process'
import { setTimeout } from 'node:timers'

const { chromium } = await import(process.env.PLAYWRIGHT_MODULE || 'playwright')
const baseURL = process.env.E2E_BASE_URL || 'http://localhost:5173'
const mailpit = process.env.E2E_MAILPIT_URL
const slug = process.env.E2E_ORG_SLUG || 'aictiq-dev'
const screenshotDir = process.env.E2E_SCREENSHOT_DIR || '/tmp/aictiq117/shots'
const stamp = Date.now().toString(36).toUpperCase()
const projectKey = `N${stamp}`.slice(-10)
const browser = await chromium.launch({ channel: 'chromium', args: ['--disable-dev-shm-usage'] })
const author = await browser.newContext({ baseURL, viewport: { width: 1440, height: 1000 } })
const screenshots = []
const headers = { 'X-Aictiq-Request': '1' }
async function api(context, method, path, data) {
  const response = await context.request.fetch(`/api/v1${path}`, { method, data, headers })
  assert.ok(response.ok(), `${method} ${path}: ${response.status()} ${await response.text()}`)
  return response.status() === 204 ? null : response.json()
}
async function until(check, message, timeout = 15000) {
  const deadline = Date.now() + timeout
  do {
    if (await check()) { console.log(`PASS ${message}`); return }
    await new Promise((resolve) => setTimeout(resolve, 200))
  } while (Date.now() < deadline)
  throw new Error(message)
}
async function capture(page, name) {
  screenshots.push([name, await page.screenshot({ fullPage: false, animations: 'disabled' })])
}
async function mailTo(address) {
  const response = await fetch(`${mailpit}/api/v1/search?query=${encodeURIComponent(`to:"${address}"`)}`)
  // Invitations go to the same addresses; only mention mail counts here.
  return ((await response.json()).messages ?? []).filter((message) => / mentioned you in /.test(message.Subject))
}
try {
  await api(author, 'POST', '/auth/login', {
    email: process.env.E2E_ADMIN_EMAIL || 'admin@aictiq.local',
    password: process.env.E2E_ADMIN_PASSWORD || 'AdminPassword123!',
  })
  const project = await api(author, 'POST', `/orgs/${slug}/projects`, {
    name: `Namesake mentions ${stamp}`, key: projectKey, visibility: 'organization',
  })
  const item = await api(author, 'POST', `/orgs/${slug}/projects/${project.key}/items/`, {
    type: 'story', title: 'Validate the release notes', descriptionMarkdown: 'Fixture data for namesake mentions.',
  })
  // Two people with the same name and different addresses, as on scalarratio.
  const namesakes = []
  for (const which of ['hotmail', 'gmail']) {
    const context = await browser.newContext({ baseURL })
    const email = `namesake-${which}-${stamp.toLowerCase()}@example.test`
    const invitation = await api(author, 'POST', `/orgs/${slug}/invitations/`, { email, role: 'member' })
    await api(context, 'POST', '/auth/register', {
      email, password: 'FixtureNamesake123!', firstName: 'Blago', lastName: 'Culjak', invitationToken: invitation.token,
    })
    await api(context, 'POST', `/invitations/${invitation.token}/accept`)
    const me = await api(context, 'GET', '/auth/me')
    namesakes.push({ id: me.id ?? me.user?.id, email })
    await context.close()
  }
  const [hotmail, gmail] = namesakes
  assert.ok(hotmail.id && gmail.id, 'both namesakes have ids')
  const commentsPath = `/orgs/${slug}/items/${item.key}/comments`

  const page = await author.newPage()
  await page.goto('/about')
  const skipTour = page.getByRole('button', { name: 'Skip tour', exact: true })
  try { await skipTour.waitFor({ timeout: 3000 }); await skipTour.click() } catch { /* Already dismissed. */ }
  await page.goto(`/o/${slug}/p/${project.key}/items/${item.key}`)
  const box = page.locator('form .tiptap').first()
  await box.waitFor()

  // The picker shows both, told apart by their addresses.
  await box.click()
  await page.keyboard.type('@Bla')
  const list = page.getByTestId('mention-list')
  await list.waitFor()
  // A rerun leaves earlier namesakes in the org, so look for this run's two.
  await until(async () => {
    const details = await list.getByTestId('mention-detail').allTextContents()
    return [gmail.email, hotmail.email].every((email) => details.includes(email))
  }, 'mention list offers both Blago Culjak accounts')
  console.log('PASS each namesake shows their email in the mention list')
  await capture(page, '01-namesakes-in-mention-list')

  // Pick the gmail one: only that account is tagged and mailed.
  await list.getByRole('option').filter({ hasText: gmail.email }).click()
  const token = `@BlagoCuljak-${gmail.id.replace(/-/g, '').slice(0, 8)}`
  await until(async () => (await box.textContent()).includes(token), `picker wrote ${token}`)
  await page.keyboard.type('can you validate these notes?')
  await page.getByRole('button', { name: 'Send', exact: true }).click()
  await page.getByTestId('comment').filter({ hasText: 'can you validate these notes?' }).waitFor()
  const afterPick = (await api(author, 'GET', `${commentsPath}?order=desc`)).items[0]
  assert.deepEqual(afterPick.mentions, [gmail.id])
  console.log('PASS the picked tag mentions only the gmail account')
  await capture(page, '02-picked-namesake-posted')
  if (mailpit) {
    await until(async () => (await mailTo(gmail.email)).length === 1, 'the gmail account got one mention email', 60000)
    assert.equal((await mailTo(hotmail.email)).length, 0, 'the hotmail account got no email')
    console.log('PASS the hotmail account got no email')
  }

  // A hand-typed tag fits both: the author is warned and nobody is tagged.
  await box.click()
  await page.keyboard.type('@BlagoCuljak')
  await page.keyboard.press('Escape')
  await page.keyboard.type(' please double check.')
  await page.getByRole('button', { name: 'Send', exact: true }).click()
  const warning = page.locator('[data-sonner-toast]').filter({ hasText: '@BlagoCuljak matches more than one person.' })
  await warning.waitFor()
  assert.match(await warning.textContent(), /Nobody was notified for it/)
  console.log('PASS a hand-typed ambiguous tag warns the author')
  const afterTyped = (await api(author, 'GET', `${commentsPath}?order=desc`)).items[0]
  assert.deepEqual(afterTyped.mentions, [])
  console.log('PASS the ambiguous tag tagged nobody')
  await capture(page, '03-ambiguous-tag-warning')

  await mkdir(screenshotDir, { recursive: true })
  for (const [name, image] of screenshots) await writeFile(`${screenshotDir}/${name}.png`, image)
  console.log(`Screenshots written to ${screenshotDir}`)
} finally {
  await browser.close()
}
