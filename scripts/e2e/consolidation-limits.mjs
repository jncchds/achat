// Consolidation under token limits (§2.4, §12b) against a throwaway instance and the mock LLM.
//   A  60 traits, mock accepts the app's input budget → consolidated in chunks, no failed call
//   B  60 traits, mock context smaller than the app's budget → context-length 400 → halves → success,
//      the rejected call is still logged (§4.4)
//   C  mock answers finish_reason=length (empty) → ONE attempt, work item Dead, actionable reason
//   D  mode reset + admin retry → the dead item completes
//   E  human add over the item-length cap → 400
//
// Needs `node scripts/e2e/mockllm.mjs` on 18999 (control: POST /_mode) and an instance on 5002:
//   -e ACHAT_Work__TickSeconds=2 -e ACHAT_Work__MaxAttempts=3 -e ACHAT_Work__BaseDelaySeconds=2
//   -e ACHAT_Turn__ConsolidateAbove=2 -e ACHAT_Evolution__CooldownHours=0
//   -e ACHAT_Evolution__MaxInputTokens=800 -e ACHAT_Turn__MaxTraits=200 -e ACHAT_Turn__MaxTraitsTotalChars=50000
//   BASE=http://localhost:5002 MOCK_CTL=http://localhost:18999 node scripts/e2e/consolidation-limits.mjs
const BASE = process.env.BASE || 'http://localhost:5002'
const MOCK_CTL = process.env.MOCK_CTL || 'http://localhost:18999'
const N = parseInt(process.env.TRAITS || '60', 10)

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

let failures = 0
const check = (ok, label, extra = '') => {
  console.log((ok ? '✓' : '✗') + ' ' + label + (extra ? ' — ' + extra : ''))
  if (!ok) failures++
}
import { spawn } from 'node:child_process'
import { fileURLToPath } from 'node:url'
// The creator conversation is created by the first chat turn; the producer arms consolidation on it.
async function firstChat(botId) {
  const sigtest = fileURLToPath(new URL('./sigtest.mjs', import.meta.url))
  await new Promise(resolve => {
    const pr = spawn('node', [sigtest, token, String(botId), 'hello'], { stdio: 'ignore', env: { ...process.env, ACHAT_BASE: BASE } })
    pr.on('close', resolve)
  })
}
const sleep = ms => new Promise(r => setTimeout(r, ms))

token = (await (await fetch(BASE + '/api/auth/login', {
  method: 'POST', headers: { 'content-type': 'application/json' },
  body: JSON.stringify({ username: 'admin', password: 'admin' }) })).json()).token

let preset = (await api('/api/presets')).data.find(p => p.name === 'Mock')
if (!preset) preset = (await api('/api/presets', { method: 'POST', body: JSON.stringify({
  name: 'Mock', provider: 'OpenAICompatible', baseEndpoint: 'http://host.docker.internal:18999/v1',
  generationModel: 'mock-model', utilityModel: 'mock-model-small', embeddingModel: 'mock-embed', temperature: 0.7 }) })).data

// A fresh bot with N traits, added by hand (its newest version is then a UserEdited one; the
// instance runs with Evolution__CooldownHours=0, so consolidation is due straight away).
async function botWithTraits(name, n) {
  const bot = (await api('/api/bots', { method: 'POST', body: JSON.stringify({
    name, presetId: preset.id, corePersona: 'A warm, easygoing friend.', speechStyle: 'Casual.' }) })).data
  await firstChat(bot.id)
  for (let i = 1; i <= n; i++) {
    const r = await api(`/api/bots/${bot.id}/persona/traits`, { method: 'POST',
      body: JSON.stringify({ text: `Habit ${String(i).padStart(3, '0')}: teases about the flat heating and the cat, as an old running joke` }) })
    if (r.status !== 200) throw new Error(`add trait ${i}: ${r.status} ${JSON.stringify(r.data)}`)
  }
  return bot
}
const workFor = async botId => (await api('/api/admin/work')).data.find(w => w.botId === botId && w.kind === 'Evolution')
const traitCount = async botId => (await api(`/api/bots/${botId}/persona/traits`)).data.length
const calls = async botId => (await api(`/api/interactions?botId=${botId}&purpose=PersonaEvolution&take=200`)).data
async function waitFor(fn, ms = 90000) {
  const t = Date.now()
  while (Date.now() - t < ms) { const v = await fn(); if (v) return v; await sleep(1000) }
  return null
}
// consolidated = the item is gone and the list shrank
const done = async (bot, n) => waitFor(async () => (await traitCount(bot.id)) < n && !(await workFor(bot.id)))

// ── A ───────────────────────────────────────────────────────────────────────
await mode({ maxPromptTokens: 0, truncate: null })
const a = await botWithTraits('LimitsA', N)
check(await done(a, N), 'A: 60 traits consolidate in chunks under the input budget', `now ${await traitCount(a.id)} traits`)
const aCalls = await calls(a.id)
check(aCalls.length >= 2 && aCalls.every(c => !c.failed), 'A: several calls, none failed', `${aCalls.length} calls`)

// ── B ───────────────────────────────────────────────────────────────────────
await mode({ maxPromptTokens: 600 })
const b = await botWithTraits('LimitsB', N)
check(await done(b, N), 'B: consolidates although the model context is smaller than the budget', `now ${await traitCount(b.id)} traits`)
const bCalls = await calls(b.id)
const rejected = bCalls.filter(c => c.failed)
check(rejected.length >= 1 && rejected.every(c => c.httpStatus === 400), 'B: the rejected call is logged (§4.4) with HTTP 400', `${rejected.length} of ${bCalls.length}`)

// ── C ───────────────────────────────────────────────────────────────────────
await mode({ maxPromptTokens: 0, truncate: 'empty' })
const c = await botWithTraits('LimitsC', N)
const dead = await waitFor(async () => { const w = await workFor(c.id); return w?.status === 'Dead' ? w : null })
check(!!dead, 'C: truncation dead-letters the item', dead ? `attempts ${dead.attempts}/${dead.maxAttempts}` : 'not dead')
check(dead?.attempts === 1, 'C: after ONE attempt, not all of them')
check(/finish_reason=length/.test(dead?.lastFailure || '') && /Evolution:MaxTokens/.test(dead?.lastFailure || ''),
  'C: reason is actionable', (dead?.lastFailure || '').slice(0, 120))
check(await traitCount(c.id) === N, 'C: the trait list is untouched')

// ── D ───────────────────────────────────────────────────────────────────────
await mode({ truncate: null })
await api(`/api/admin/work/${dead.id}/retry`, { method: 'POST' })
check(await done(c, N), 'D: admin retry after the cause is gone consolidates', `now ${await traitCount(c.id)} traits`)

// ── E ───────────────────────────────────────────────────────────────────────
const e = await api(`/api/bots/${c.id}/persona/traits`, { method: 'POST', body: JSON.stringify({ text: 'x'.repeat(201) }) })
check(e.status === 400, 'E: a human trait over the length cap is a 400', e.data?.message)

await mode({ maxPromptTokens: 0, truncate: null })
console.log(failures ? `\n${failures} check(s) failed` : '\nall checks passed')
process.exit(failures ? 1 : 0)
