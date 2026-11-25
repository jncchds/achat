// Work queue (§12b) verification against a live docker instance, using the mock LLM:
//  1. chat turns fail against a dead endpoint (the user's message survives)
//  2. summarisation and persona consolidation dead-letter after the attempt cap, with a visible failure
//  3. "Try again" (RetryLastAsync) re-runs the failed turn once the endpoint is fixed
//  4. an admin retry re-arms both items; they succeed and the queue clears
//
// Needs `node mockllm.mjs` on 18999 and a throwaway instance with a fast queue:
//   ACHAT_Work__TickSeconds=2 ACHAT_Work__MaxAttempts=3
//   ACHAT_Work__BaseDelaySeconds=2 ACHAT_Work__MaxDelaySeconds=8
//   ACHAT_Chat__SummaryTailTokens=20 ACHAT_Turn__ConsolidateAbove=2 ACHAT_Evolution__CooldownHours=0
import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr'

const BASE = process.env.BASE || 'http://localhost:5002'
const MOCK = {
  endpoint: 'http://host.docker.internal:18999/v1',
  gen: 'mock-model',
  util: 'mock-model-small',
  emb: 'mock-embed',
}

let token = ''
async function api(path, { method = 'GET', body } = {}) {
  const r = await fetch(BASE + path, {
    method,
    headers: { authorization: `Bearer ${token}`, 'content-type': 'application/json' },
    body,
  })
  const text = await r.text()
  let data
  try { data = JSON.parse(text) } catch { data = text }
  return { status: r.status, data }
}

// One hub round-trip: invoke the method and collect the run's events. The run is
// detached from the connection (§4.2), so the invoke returns at once; wait for the
// terminal chat.completed / chat.failed event.
async function hubRun(method, botId, content, timeoutMs = 120000) {
  const conn = new HubConnectionBuilder()
    .withUrl(BASE + '/hubs/chat', { accessTokenFactory: () => token })
    .configureLogging(LogLevel.Warning)
    .build()
  const events = { deltas: '', completed: null, failed: null, invokeError: null }
  conn.on('chat.delta', m => { events.deltas += m.delta })
  conn.on('chat.started', () => {})
  conn.on('chat.completed', m => { events.completed = m })
  conn.on('chat.failed', m => { events.failed = m })
  await conn.start()
  try {
    await Promise.race([
      (async () => {
        if (content === undefined) await conn.invoke(method, botId)
        else await conn.invoke(method, botId, content)
        while (!events.completed && !events.failed) await new Promise(r => setTimeout(r, 200))
      })(),
      new Promise((_, rej) => setTimeout(() => rej(new Error(`timeout after ${timeoutMs}ms`)), timeoutMs)),
    ])
  } catch (e) { events.invokeError = String(e) }
  await conn.stop()
  return events
}

let failures = 0
const check = (ok, label, extra = '') => {
  console.log((ok ? '✓' : '✗') + ' ' + label + (extra ? ' — ' + extra : ''))
  if (!ok) failures++
}

const sleep = ms => new Promise(r => setTimeout(r, ms))

async function poll(fn, what, timeoutMs = 120000, everyMs = 2000) {
  const deadline = Date.now() + timeoutMs
  for (;;) {
    const v = await fn()
    if (v) return v
    if (Date.now() > deadline) return null
    await sleep(everyMs)
  }
}

// ── login ──
const login = await api('/api/auth/login', { method: 'POST', body: JSON.stringify({ username: 'admin', password: 'admin' }) })
token = login.data.token
if (!token) { console.error('login failed', JSON.stringify(login.data).slice(0, 200)); process.exit(1) }

// ── a bot on a broken preset: the models are the mock's but the endpoint is dead.
//    The models must stay fixed afterwards — a preset's embedding model is locked while
//    a bot uses it (§5.7) — so only the endpoint changes.
const presetBody = endpoint => JSON.stringify({
  name: 'WQ-broken', provider: 'OpenAICompatible', baseEndpoint: endpoint,
  generationModel: MOCK.gen, utilityModel: MOCK.util, embeddingModel: MOCK.emb,
})
const preset = (await api('/api/presets', { method: 'POST', body: presetBody('http://127.0.0.1:9/v1') })).data
if (!preset.id) { console.error('preset create failed', JSON.stringify(preset).slice(0, 200)); process.exit(1) }

const bot = (await api('/api/bots', { method: 'POST', body: JSON.stringify({
  name: 'WQ-test', presetId: preset.id, corePersona: 'A concise test friend.',
}) })).data
if (!bot.id) { console.error('bot create failed', JSON.stringify(bot).slice(0, 200)); process.exit(1) }
const botId = bot.id

// ── 1. two failed turns (the endpoint is dead); the user's messages are kept and
//    make the summary tail long enough to arm summarisation ──
const f1 = await hubRun('SendAsync', botId, "My name is Alex. My daughter Zoe just started kindergarten yesterday.")
check(f1.failed !== null && f1.completed === null,
  'first turn fails against the dead endpoint', (f1.failed?.message || f1.invokeError || '').slice(0, 80))
const f2 = await hubRun('SendAsync', botId, "Zoe's first day went great — she is obsessed with penguins now.")
check(f2.failed !== null && f2.completed === null,
  'second turn fails too', (f2.failed?.message || '').slice(0, 80))

// ── 2. a human adds three traits (more than ConsolidateAbove=2, no cooldown) → consolidation arms ──
for (const text of ['teases them about the kindergarten drop-off', 'asks after Zoe first', 'keeps answers short']) {
  const t = await api(`/api/bots/${botId}/persona/traits`, { method: 'POST', body: JSON.stringify({ text }) })
  if (t.status !== 200 && t.status !== 201) { console.error('trait add failed', t.status, JSON.stringify(t.data).slice(0, 200)); process.exit(1) }
}

// ── 3. both background passes dead-letter after the attempt cap ──
const workList = async () => (await api('/api/admin/work')).data
const deadOf = async kind => poll(async () => {
  const list = await workList()
  return list.find(w => w.kind === kind && w.status === 'Dead')
}, `dead ${kind} item`, 120000)
const deadSummary = await deadOf('Summary')
check(deadSummary !== null && deadSummary !== undefined, 'summarisation dead-letters after the attempt cap',
  deadSummary ? `${deadSummary.attempts}/${deadSummary.maxAttempts} attempts — ${String(deadSummary.lastFailure).slice(0, 80)}` : 'no Dead item within 120s')
const deadEvo = await deadOf('Evolution')
check(deadEvo !== null && deadEvo !== undefined, 'consolidation dead-letters after the attempt cap',
  deadEvo ? `${deadEvo.attempts}/${deadEvo.maxAttempts} attempts — ${String(deadEvo.lastFailure).slice(0, 80)}` : 'no Dead item within 120s')
const noExtraction = (await workList()).every(w => w.kind !== 'Extraction')
check(noExtraction, 'no Extraction work is ever armed (retired in §4.7)')

// ── 4. fix the preset (endpoint only), then "try again" on the last failed turn ──
const fix = await api(`/api/presets/${preset.id}`, { method: 'PUT', body: presetBody(MOCK.endpoint) })
if (fix.status !== 200) { console.error('preset fix failed', fix.status, JSON.stringify(fix.data).slice(0, 200)); process.exit(1) }

const retry = await hubRun('RetryLastAsync', botId)
check(retry.completed !== null && (retry.completed.content || retry.deltas).length > 0,
  '"try again" re-runs the failed turn once the endpoint works',
  (retry.completed?.content || retry.deltas || retry.failed?.message || '').slice(0, 60))

// ── 5. admin retry re-arms both; they succeed and the queue clears ──
for (const [dead, label] of [[deadSummary, 'summary'], [deadEvo, 'consolidation']]) {
  if (!dead) continue
  const retried = await api(`/api/admin/work/${dead.id}/retry`, { method: 'POST' })
  check(retried.status === 200 && retried.data.status === 'Pending', `admin re-arms the dead ${label} item`, `HTTP ${retried.status}`)
}
const cleared = await poll(async () => (await workList()).length === 0, 'queue to clear', 120000)
check(cleared, 'both passes complete and leave the queue')
const versions = (await api(`/api/bots/${botId}/persona/versions`)).data
check(Array.isArray(versions) && versions.some(v => v.source === 'Consolidation'), 'consolidation recorded a Consolidation version',
  (versions || []).map(v => v.source).join(' → '))

// ── the queue is clear at the end ──
const final = await workList()
check(Array.isArray(final) && final.length === 0, 'work queue is clear', `${final?.length ?? '?'} items`)

// ── cleanup ──
await api(`/api/bots/${botId}`, { method: 'DELETE' })
await api(`/api/presets/${preset.id}`, { method: 'DELETE' }).catch(() => {})

console.log(failures === 0 ? '\nALL PASSED' : `\n${failures} FAILED`)
process.exit(failures === 0 ? 0 : 1)
