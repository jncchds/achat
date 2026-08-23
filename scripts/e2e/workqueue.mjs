// Work queue (§12b) verification against a live docker instance:
//  1. chat turns fail against a dead endpoint (the user's message survives)
//  2. "Try again" (RetryLastAsync) re-runs the failed turn once the endpoint is fixed
//  3. memory extraction dead-letters after the attempt cap, with a visible failure
//  4. an admin retry re-arms it; the pass succeeds, stores memories, and the queue clears
//
// The throwaway instance should run with a fast queue:
//   ACHAT_Work__TickSeconds=2 ACHAT_Work__MaxAttempts=3
//   ACHAT_Work__BaseDelaySeconds=2 ACHAT_Work__MaxDelaySeconds=8
//   ACHAT_Extraction__MinNewMessages=2 ACHAT_Extraction__IdleMinutes=0
import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr'

const BASE = process.env.BASE || 'http://localhost:5002'
const LOCAL_LLM = {
  endpoint: 'http://192.168.2.33:1234/v1',
  gen: 'qwen/qwen3.8-27b',
  emb: 'text-embedding-nomic-embed-text-v2-moe',
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

// One hub round-trip: invoke the method and collect the run's events. The invoke
// resolves when the run has finished (the hub awaits it), so events are settled.
async function hubRun(method, botId, content, timeoutMs = 120000) {
  const conn = new HubConnectionBuilder()
    .withUrl(BASE + '/hubs/chat', { accessTokenFactory: () => token })
    .configureLogging(LogLevel.Warning)
    .build()
  const events = { deltas: '', completed: null, failed: null, invokeError: null }
  conn.on('chat.delta', m => { events.deltas += m.delta })
  conn.on('chat.completed', m => { events.completed = m })
  conn.on('chat.failed', m => { events.failed = m })
  await conn.start()
  try {
    await Promise.race([
      content === undefined
        ? conn.invoke(method, botId)
        : conn.invoke(method, botId, content),
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

// ── a bot on a broken preset: the models are real (local Qwen) but the endpoint
//    is dead. The models must stay fixed afterwards — a preset's embedding model
//    is locked while a bot uses it (§5.7) — so only the endpoint changes.
const preset = (await api('/api/presets', { method: 'POST', body: JSON.stringify({
  name: 'WQ-broken', provider: 'OpenAICompatible',
  baseEndpoint: 'http://127.0.0.1:9/v1',
  generationModel: LOCAL_LLM.gen,
  utilityModel: LOCAL_LLM.gen,
  embeddingModel: LOCAL_LLM.emb,
}) })).data
if (!preset.id) { console.error('preset create failed', JSON.stringify(preset).slice(0, 200)); process.exit(1) }

const bot = (await api('/api/bots', { method: 'POST', body: JSON.stringify({
  name: 'WQ-test', presetId: preset.id, corePersona: 'A concise test friend.',
}) })).data
if (!bot.id) { console.error('bot create failed', JSON.stringify(bot).slice(0, 200)); process.exit(1) }
const botId = bot.id

// ── 1. two failed turns (the endpoint is dead). The turns carry durable user
//    facts, so the extraction pass that follows has something to store.
const f1 = await hubRun('SendAsync', botId, "My name is Alex. My daughter Zoe just started kindergarten yesterday.")
check(f1.failed !== null && f1.completed === null,
  'first turn fails against the dead endpoint', (f1.failed?.message || f1.invokeError || '').slice(0, 80))

const f2 = await hubRun('SendAsync', botId, "Zoe's first day went great — she is obsessed with penguins now.")
check(f2.failed !== null && f2.completed === null,
  'second turn fails too (enough for extraction to arm)', (f2.failed?.message || '').slice(0, 80))

// ── 2. extraction dead-letters after the attempt cap ──
const workList = async () => (await api('/api/admin/work')).data
const dead = await poll(async () => {
  const list = await workList()
  return list.find(w => w.kind === 'Extraction' && w.status === 'Dead')
}, 'dead extraction item', 120000)
check(dead !== null, 'extraction dead-letters after the attempt cap',
  dead ? `${dead.attempts}/${dead.maxAttempts} attempts — ${String(dead.lastFailure).slice(0, 80)}` : 'no Dead item within 120s')

// ── 3. fix the preset (endpoint only — the models are already the local ones),
//    then "try again" on the last failed turn ──
const fix = await api(`/api/presets/${preset.id}`, { method: 'PUT', body: JSON.stringify({
  name: 'WQ-broken', provider: 'OpenAICompatible',
  baseEndpoint: LOCAL_LLM.endpoint,
  generationModel: LOCAL_LLM.gen,
  utilityModel: LOCAL_LLM.gen,
  embeddingModel: LOCAL_LLM.emb,
}) })
if (fix.status !== 200) { console.error('preset fix failed', fix.status, JSON.stringify(fix.data).slice(0, 200)); process.exit(1) }

const retry = await hubRun('RetryLastAsync', botId)
check(retry.completed !== null && (retry.completed.content || retry.deltas).length > 0,
  '"try again" re-runs the failed turn once the endpoint works',
  (retry.completed?.content || retry.deltas || retry.failed?.message || '').slice(0, 60))

// ── 4. admin retry re-arms the extraction; it succeeds and stores memories ──
if (dead) {
  const retried = await api(`/api/admin/work/${dead.id}/retry`, { method: 'POST' })
  check(retried.status === 200 && retried.data.status === 'Pending',
    'admin re-arms the dead extraction item', `HTTP ${retried.status}`)

  const cleared = await poll(async () => {
    const list = await workList()
    return list.find(w => w.kind === 'Extraction') === undefined
  }, 'queue to clear', 180000)
  check(cleared, 'extraction completes and leaves the queue', '')

  const memories = (await api(`/api/memories?botId=${botId}`)).data
  check(Array.isArray(memories) && memories.length >= 1,
    'memories were stored from the extraction pass', `${memories?.length ?? 0} memories`)
}

// ── the queue is clear at the end ──
const final = await workList()
check(Array.isArray(final) && final.length === 0, 'work queue is clear', `${final?.length ?? '?'} items`)

// ── cleanup ──
await api(`/api/bots/${botId}`, { method: 'DELETE' })
await api(`/api/presets/${preset.id}`, { method: 'DELETE' }).catch(() => {})

console.log(failures === 0 ? '\nALL PASSED' : `\n${failures} FAILED`)
process.exit(failures === 0 ? 0 : 1)
