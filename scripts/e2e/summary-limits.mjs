// Summary pass under token limits (§5.3, §12b) against a throwaway instance and the mock LLM.
//   A  a long tail (over Chat:SummaryMaxInputTokens) → summarised in several steps, none failed,
//      every prompt under the budget, each message in exactly one step
//   B  mock context smaller than the app's budget → context-length 400 → halved steps → success,
//      the rejected call is still logged (§4.4)
//   C  mock truncates summaries (finish_reason=length) → ONE attempt, work item Dead, actionable reason
//   D  mode reset + admin retry → the dead item completes
//
// Needs `node scripts/e2e/mockllm.mjs` on 18999 (control: POST /_mode) and an instance on 5002:
//   -e ACHAT_Work__TickSeconds=2 -e ACHAT_Work__MaxAttempts=3 -e ACHAT_Work__BaseDelaySeconds=2
//   -e ACHAT_Chat__SummaryTailTokens=2500 -e ACHAT_Chat__SummaryMaxInputTokens=1200
//   BASE=http://localhost:5002 MOCK_CTL=http://localhost:18999 node scripts/e2e/summary-limits.mjs
//
// Optional real model (R): KEYFILE=<file with the OpenRouter key> [REAL_MODEL=...] — a long
// conversation summarises without failure. The key only goes to the preset API, is never printed,
// and the preset is deleted afterwards.
import { spawn } from 'node:child_process'
import { fileURLToPath } from 'node:url'

const BASE = process.env.BASE || 'http://localhost:5002'
const MOCK_CTL = process.env.MOCK_CTL || 'http://localhost:18999'
const TURNS = parseInt(process.env.TURNS || '8', 10)
const BUDGET = parseInt(process.env.SUMMARY_BUDGET || '1200', 10) // = Chat:SummaryMaxInputTokens of the instance

let token = ''
async function api(path, { method = 'GET', body } = {}) {
  const r = await fetch(BASE + path, {
    method, body,
    headers: { authorization: `Bearer ${token}`, 'content-type': 'application/json' },
  })
  const text = await r.text()
  let data
  try { data = JSON.parse(text) } catch { data = text }
  return { status: r.status, data }
}
const mode = body => fetch(MOCK_CTL + '/_mode', { method: 'POST', body: JSON.stringify(body) }).then(r => r.json())
const sleep = ms => new Promise(r => setTimeout(r, ms))

let failures = 0
const check = (ok, label, extra = '') => {
  console.log((ok ? '✓' : '✗') + ' ' + label + (extra ? ' — ' + extra : ''))
  if (!ok) failures++
}

const sigtest = fileURLToPath(new URL('./sigtest.mjs', import.meta.url))
const chat = (botId, text) => new Promise(resolve => {
  const pr = spawn('node', [sigtest, token, String(botId), text], { stdio: 'ignore', env: { ...process.env, ACHAT_BASE: BASE } })
  pr.on('close', resolve)
})

token = (await (await fetch(BASE + '/api/auth/login', {
  method: 'POST', headers: { 'content-type': 'application/json' },
  body: JSON.stringify({ username: 'admin', password: 'admin' }) })).json()).token

let preset = (await api('/api/presets')).data.find(p => p.name === 'Mock')
if (!preset) preset = (await api('/api/presets', { method: 'POST', body: JSON.stringify({
  name: 'Mock', provider: 'OpenAICompatible', baseEndpoint: 'http://host.docker.internal:18999/v1',
  generationModel: 'mock-model', utilityModel: 'mock-model-small', embeddingModel: 'mock-embed', temperature: 0.7 }) })).data

// A long user turn: ~1400 chars (~350 tokens) with a numbered marker so steps can be checked.
const longTurn = i => `turn ${String(i).padStart(2, '0')}: ` +
  `Today I want to tell you about part ${i} of the long story of my flat, the heating we call Steve, ` +
  'my sister Ann who keeps visiting, and the cat who sleeps on the radiator. '.repeat(14)

async function botWithLongChat(name, turns, texts = longTurn) {
  const bot = (await api('/api/bots', { method: 'POST', body: JSON.stringify({
    name, presetId: preset.id, corePersona: 'A warm, easygoing friend.', speechStyle: 'Casual.' }) })).data
  for (let i = 1; i <= turns; i++) await chat(bot.id, texts(i))
  return bot
}
const workFor = async botId => (await api('/api/admin/work')).data.find(w => w.botId === botId && w.kind === 'Summary')
const calls = async botId => (await api(`/api/interactions?botId=${botId}&purpose=Summarisation&take=200`)).data
const detail = async id => (await api(`/api/interactions/${id}`)).data
async function waitFor(fn, ms = 120000) {
  const t = Date.now()
  while (Date.now() - t < ms) { const v = await fn(); if (v) return v; await sleep(1000) }
  return null
}
// summarised = at least one successful call and no pending item
const done = async bot => waitFor(async () => (await calls(bot.id)).some(c => !c.failed) && !(await workFor(bot.id)))
// the turn markers of the user messages in a step (only the "New messages" part, not the previous summary)
const turnsIn = body => [...body.slice(body.indexOf('New messages since then')).matchAll(/\nuser: turn (\d\d)/g)]
  .map(m => parseInt(m[1], 10))

// ── A ───────────────────────────────────────────────────────────────────────
await mode({ maxPromptTokens: 0, truncate: null, truncateKind: 'evolution' })
const a = await botWithLongChat('SumA', TURNS)
check(await done(a), 'A: a long tail summarises')
const aCalls = (await calls(a.id)).sort((x, y) => x.id - y.id)
check(aCalls.length >= 2 && aCalls.every(c => !c.failed), 'A: in several steps, none failed', `${aCalls.length} calls`)
const aBodies = await Promise.all(aCalls.map(c => detail(c.id)))
const est = s => Math.ceil(JSON.parse(s).reduce((n, m) => n + m.content.length, 0) / 4)
check(aBodies.every(b => est(b.requestBody) <= BUDGET), 'A: every prompt within Chat:SummaryMaxInputTokens',
  aBodies.map(b => est(b.requestBody)).join(', '))
const seen = aBodies.flatMap(b => turnsIn(JSON.parse(b.requestBody)[1].content))
check(JSON.stringify(seen) === JSON.stringify([...new Set(seen)].sort((x, y) => x - y)), 'A: oldest first, each message in one step', seen.join(','))

// ── B ───────────────────────────────────────────────────────────────────────
await mode({ maxPromptTokens: Math.floor(BUDGET * 0.75), limitKind: 'background' }) // chat turns stay unaffected
const b = await botWithLongChat('SumB', TURNS)
check(await done(b), 'B: summarises although the model context is smaller than the budget')
const bCalls = await calls(b.id)
const rejected = bCalls.filter(c => c.failed)
check(rejected.length >= 1 && rejected.every(c => c.httpStatus === 400), 'B: the rejected call is logged with HTTP 400',
  `${rejected.length} of ${bCalls.length}`)

// ── C ───────────────────────────────────────────────────────────────────────
await mode({ maxPromptTokens: 0, limitKind: 'all', truncate: 'cut', truncateKind: 'summary' })
const c = await botWithLongChat('SumC', TURNS)
const dead = await waitFor(async () => { const w = await workFor(c.id); return w?.status === 'Dead' ? w : null })
check(!!dead, 'C: truncation dead-letters the item', dead ? `attempts ${dead.attempts}/${dead.maxAttempts}` : 'not dead')
check(dead?.attempts === 1, 'C: after ONE attempt, not all of them')
check(/finish_reason=length/.test(dead?.lastFailure || '') && /Chat:SummaryMaxTokens/.test(dead?.lastFailure || ''),
  'C: reason is actionable', (dead?.lastFailure || '').slice(0, 140))
check((await calls(c.id)).every(x => x.failed), 'C: no cut-off summary was stored (every call failed)')

// ── D ───────────────────────────────────────────────────────────────────────
await mode({ truncate: null, truncateKind: 'evolution' })
if (dead) await api(`/api/admin/work/${dead.id}/retry`, { method: 'POST' })
check(await done(c), 'D: admin retry after the cause is gone summarises')

// ── R (optional) real hosted model ──────────────────────────────────────────
if (process.env.KEYFILE) {
  const fs = await import('node:fs')
  const raw = fs.readFileSync(process.env.KEYFILE, 'utf8')
  const key = (raw.match(/^OPENROUTER_API_KEY=(.+)$/m) || [])[1]?.trim() || raw.trim()
  const model = process.env.REAL_MODEL || 'nvidia/nemotron-3-nano-omni-30b-a3b-reasoning:free'
  const real = (await api('/api/presets', { method: 'POST', body: JSON.stringify({
    name: 'OpenRouter-free-T2', provider: 'OpenAICompatible', baseEndpoint: 'https://openrouter.ai/api/v1', apiKey: key,
    generationModel: model, utilityModel: model, temperature: 0.3 }) })).data
  try {
    preset = real
    const facts = [
      'My sister Ann is moving to Lisbon in March.', 'The heating is called Steve and it broke again.',
      'My cat Pixel only eats salmon.', 'I start a new job at the library on Monday.',
      'I promised to call my grandmother on Sunday.', 'I am learning to bake sourdough.',
      'My friend Tomas lent me his bike.', 'I want to visit Iceland next summer.',
    ]
    const turn = i => `${facts[(i - 1) % facts.length]} ` + 'Also, rambling a bit about my week, the weather, the tram, and work. '.repeat(10)
    const r = await botWithLongChat('SumReal', parseInt(process.env.REAL_TURNS || '10', 10), turn)
    const ok = await waitFor(async () => {
      const w = await workFor(r.id)
      if (w?.status === 'Dead') return 'dead'
      return (await calls(r.id)).some(x => !x.failed) && !w ? 'ok' : null
    }, 900000)
    const rc = (await calls(r.id)).sort((x, y) => x.id - y.id)
    const w = await workFor(r.id)
    check(ok === 'ok', `R: real model (${model}) summarises a long conversation`,
      `${ok}; ${rc.length} calls, ${rc.filter(x => x.failed).length} failed; finish: ${[...new Set(rc.map(x => x.finishReason))]}; ${(w?.lastFailure || '').slice(0, 200)}`)
    const last = rc.filter(x => !x.failed).at(-1)
    if (last) console.log('real summary:', (await detail(last.id)).responseContent)
  } finally {
    await api(`/api/presets/${real.id}`, { method: 'DELETE' }) // drop the stored key
  }
}

await mode({ maxPromptTokens: 0, limitKind: 'all', truncate: null, truncateKind: 'evolution' })
console.log(failures ? `\n${failures} check(s) failed` : '\nall checks passed')
process.exit(failures ? 1 : 0)
