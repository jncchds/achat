// E2E: detached generation (§4.2) — the run outlives the browser.
//   s1: leave the page mid-generation  → the reply still completes
//   s2: explicit Stop                  → still cancels, partial persisted, Try again
//   s3: come back while a run is live  → re-attach, the tail keeps streaming
// Against the containerized app at :5001 (admin/admin). Uses a throwaway bot on
// the OpenRouter preset (fast, reliable) and deletes it afterwards.

import { chromium } from 'playwright'

const BASE = 'http://localhost:5001'
const results = []
const ok = (name, cond, extra = '') => {
  results.push({ name, cond })
  console.log(`${cond ? 'PASS' : 'FAIL'}  ${name}${extra ? ` — ${extra}` : ''}`)
}

const browser = await chromium.launch()
const ctx = await browser.newContext({ viewport: { width: 1280, height: 900 } })
const page = await ctx.newPage()

// ── auth + token for direct API polling ───────────────────────────────────────
await page.goto(`${BASE}/login`)
await page.fill('input[autocomplete="username"]', 'admin')
await page.fill('input[autocomplete="current-password"]', 'admin')
await page.click('button[type="submit"]')
await page.waitForURL('**/bots', { timeout: 15000 })
const token = await page.evaluate(() => localStorage.getItem('achat_token'))
if (!token) { console.error('no token'); process.exit(1) }

const api = async (method, path, body) => {
  const res = await ctx.request[method](`${BASE}${path}`, {
    headers: { 'Authorization': `Bearer ${token}`, 'Content-Type': 'application/json' },
    ...(body ? { data: body } : {}),
  })
  const text = await res.text()
  let json = null
  try { json = JSON.parse(text) } catch { /* 204 etc. */ }
  return { status: res.status(), body: json }
}

// ── throwaway bot on preset 3 (OpenRouter) ────────────────────────────────────
const created = await api('post', '/api/bots', {
  name: 'E2E Detached', corePersona: 'A concise, warm narrator.',
  speechStyle: 'terse', greeting: null, presetId: 3,
})
if (!(created.status === 201 || created.status === 200) || !created.body?.id) {
  console.error('create temp bot failed', created.status, created.body)
  process.exit(1)
}
const botId = created.body.id
ok('setup: temp bot on preset 3', true, `bot #${botId}`)

const messages = async () => (await api('get', `/api/bots/${botId}/messages`)).body ?? []
const lastAssistant = (ms) => {
  const a = ms.filter(m => m.role === 'Assistant')
  return a[a.length - 1] ?? null
}

// Wait until the newest bot bubble has visible text = first content delta arrived.
const firstDelta = async (timeoutMs = 150000) => {
  const t0 = Date.now()
  while (Date.now() - t0 < timeoutMs) {
    const txt = await page.locator('.msg.bot .msg-bubble').last()
      .evaluate(el => el.textContent || '').catch(() => '')
    if (txt.trim().length > 0) return txt
    await page.waitForTimeout(250)
  }
  return null
}

const waitForCompletion = async (timeoutMs = 150000) => {
  const t0 = Date.now()
  while (Date.now() - t0 < timeoutMs) {
    const la = lastAssistant(await messages())
    if (la && la.isComplete) return la
    await page.waitForTimeout(1000)
  }
  return null
}

// ── s1: leave the page mid-generation → the reply still completes ─────────────
await page.goto(`${BASE}/bots/${botId}`)
await page.waitForSelector('.chat-input')
await page.fill('.chat-input', 'Tell me a two-sentence story about a lighthouse keeper and his cat. Keep it very short.')
await page.click('button:has-text("Send")')
const d1 = await firstDelta()
ok('s1: generation started (first delta seen)', d1 !== null, d1?.slice(0, 60))

if (d1) {
  await page.goto(`${BASE}/bots`) // ← leave the page mid-generation
  const done = await waitForCompletion()
  ok('s1: reply COMPLETED server-side after I left', done !== null)

  await page.goto(`${BASE}/bots/${botId}`)
  await page.waitForSelector('.msg.bot .msg-bubble')
  await page.waitForTimeout(500)
  const server = lastAssistant(await messages())
  const uiTxt = (await page.locator('.msg.bot .msg-bubble').last().textContent())?.trim() ?? ''
  ok('s1: full reply visible when I return', server !== null && uiTxt === server.content.trim(), uiTxt.slice(0, 80))
  ok('s1: no error bubble', (await page.locator('.msg-error').count()) === 0)
}

// ── s2: explicit Stop still cancels; partial + Try again ──────────────────────
await page.fill('.chat-input', 'Now write a much longer, ten-sentence story about a lighthouse keeper, his cat, and a great storm.')
await page.click('button:has-text("Send")')
const d2 = await firstDelta()
ok('s2: generation started', d2 !== null)

if (d2) {
  await page.click('.btn-danger.chat-send') // Stop
  await page.waitForSelector('button.chat-send:not(.btn-danger)', { timeout: 20000 }).catch(() => {})
  await page.waitForTimeout(500)
  const la = lastAssistant(await messages())
  ok('s2: partial persisted (incomplete, non-empty)', la !== null && la.isComplete === false && la.content.length > 0, la?.content?.slice(0, 60))
  ok('s2: Try again offered', (await page.locator('button:has-text("Try again")').count()) >= 1)
  ok('s2: no "stopped" caption', (await page.locator('.msg-partial').count()) === 0)
}

// ── s3: a second tab loads while a run is in flight → re-attach ───────────────
// Longer generation so the in-flight window is wide and stable.
await page.fill('.chat-input', 'Write a detailed, at-least-300-word story about the keeper, the cat, the storm and the sea, with a twist at the end.')
await page.click('button:has-text("Send")')
const d3 = await firstDelta()
ok('s3: generation started', d3 !== null)

if (d3) {
  // A fresh tab on the same conversation adopts the in-flight run (§4.2).
  // Note: reasoning models emit empty content deltas while thinking — the run
  // is "in flight" as soon as the user turn is the last row.
  const page2 = await ctx.newPage()
  await page2.goto(`${BASE}/bots/${botId}`)
  await page2.waitForSelector('.chat-input')
  await page2.waitForTimeout(1200)

  const stopVisible = (await page2.locator('.btn-danger.chat-send').count()) === 1
  const cursorVisible = (await page2.locator('.msg.bot .msg-bubble .cursor').count()) > 0
  const ms = await messages()
  const stillInFlight = ms.length > 0 && ms[ms.length - 1].role === 'User'
  const completedInFlight = lastAssistant(ms) !== null && lastAssistant(ms).isComplete === true

  // Either the run was still in flight when the tab loaded (re-attach adopted it)
  // — or it finished in the interim, in which case the full reply must be there.
  ok('s3: re-attached (busy/streaming) OR completed-with-reply',
     stopVisible || cursorVisible || completedInFlight,
     `stop=${stopVisible} cursor=${cursorVisible} inFlight=${stillInFlight}`)

  const done = await waitForCompletion()
  ok('s3: reply completed', done !== null)
  await page2.waitForTimeout(500)
  const uiTxt = (await page2.locator('.msg.bot .msg-bubble').last().textContent())?.trim() ?? ''
  ok('s3: final text matches server', done !== null && uiTxt === done.content.trim(), uiTxt.slice(0, 80))
  await page2.close()
}

// ── cleanup ────────────────────────────────────────────────────────────────────
const del = await api('delete', `/api/bots/${botId}`)
ok('cleanup: temp bot deleted', del.status === 204 || del.status === 200, `status ${del.status}`)

await browser.close()
const failed = results.filter(r => !r.cond)
console.log(failed.length === 0 ? 'ALL PASSED' : `${failed.length} FAILED`)
process.exit(failed.length === 0 ? 0 : 1)
