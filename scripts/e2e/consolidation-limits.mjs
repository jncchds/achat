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
//
// F (real model, optional): KEYFILE=<key file> REAL_ONLY=1 against an instance with the default
// Turn__ConsolidateAbove (12) and no MaxInputTokens override, so the target range (§2.4) is the real
// one: the result must keep between Evolution:MinTraitsAfter (ABOVE/2) and ABOVE traits.
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
// 40 distinct, realistic traits (a few overlap on purpose) for the real-model run.
const REAL_TRAITS = [
  'Calls the flat heating "Steve" and asks after it like a mutual friend', 'Teases them about never closing the kitchen cupboards',
  'Remembers their sister Ann and asks how the Lisbon move is going', 'Uses gentle sarcasm when they are being too hard on themselves',
  'Sends a "good luck" note before every job interview', 'Shares terrible puns about cats, especially about Pixel',
  'Gets audibly excited about sourdough experiments', 'Asks about the grandmother every Sunday evening',
  'Has a running bet with them about whether the tram will be late', 'Switches to short, calm sentences when they are anxious',
  'Loves recommending obscure Icelandic music', 'Pretends to be offended when they forget its jokes',
  'Celebrates small wins like finishing a book', 'Keeps a mock-serious "Steve the heating" incident log',
  'Is more playful late at night', 'Avoids giving advice unless asked, asks questions instead',
  'Uses the nickname "captain" when they take charge of something', 'Quotes their own past sayings back to them',
  'Makes up tiny bedtime stories about Pixel the cat', 'Is protective of their sleep and nudges them to go to bed',
  'Enjoys planning the imaginary Iceland trip itinerary', 'Teases them about borrowing a bike from Tomas',
  'Gets curious about library life since the new job', 'Uses lots of weather metaphors for moods',
  'Laughs at its own jokes before finishing them', 'Is honest but kind when they ask for feedback on writing',
  'Brings up the "Steve" joke whenever it gets cold', 'Asks for photos of every sourdough loaf',
  'Remembers that they hate coriander', 'Has a soft spot for rainy-day conversations',
  'Encourages them to call Ann more often', 'Celebrates Fridays with a silly ritual phrase',
  'Gets competitive about word games', 'Is patient when they vent about work',
  'Likes to end chats with a small question for tomorrow', 'Makes cat puns, especially about Pixel',
  'Adopted their habit of saying "right then" to change topics', 'Admires their stubbornness about fixing things themselves',
  'Notices when they sound tired and says so gently', 'Treats the Iceland trip as a shared dream they plan together',
]
async function botWithRealTraits(name) {
  const bot = (await api('/api/bots', { method: 'POST', body: JSON.stringify({
    name, presetId: preset.id, corePersona: 'A warm, easygoing friend with a dry sense of humour.', speechStyle: 'Casual.' }) })).data
  await firstChat(bot.id)
  for (const text of REAL_TRAITS.slice(0, parseInt(process.env.REAL_TRAITS || '40', 10))) {
    const r = await api(`/api/bots/${bot.id}/persona/traits`, { method: 'POST', body: JSON.stringify({ text }) })
    if (r.status !== 200) throw new Error(`add trait: ${r.status} ${JSON.stringify(r.data)}`)
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

if (!process.env.REAL_ONLY) {
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
}

// ── F (optional) real hosted model ──────────────────────────────────────────
// KEYFILE=<file with OPENROUTER_API_KEY=...>; the key only goes to the preset API, is never printed.
if (process.env.KEYFILE) {
  const fs = await import('node:fs')
  const key = (fs.readFileSync(process.env.KEYFILE, 'utf8').match(/^OPENROUTER_API_KEY=(.+)$/m) || [])[1]?.trim()
    || fs.readFileSync(process.env.KEYFILE, 'utf8').trim()
  const model = process.env.REAL_MODEL || 'nvidia/nemotron-3-nano-omni-30b-a3b-reasoning:free'
  const real = (await api('/api/presets', { method: 'POST', body: JSON.stringify({
    name: 'OpenRouter-free-T1', provider: 'OpenAICompatible', baseEndpoint: 'https://openrouter.ai/api/v1', apiKey: key,
    generationModel: model, utilityModel: model, embeddingModel: 'nvidia/nemotron-3.5-embed-1b:free', temperature: 0.3 }) })).data
  preset = real
  const above = parseInt(process.env.ABOVE || '12', 10)
  const min = parseInt(process.env.MIN_AFTER || String(Math.max(1, Math.floor(above / 2))), 10)
  const f = await botWithRealTraits('LimitsReal')
  const before = (await api(`/api/bots/${f.id}/persona/traits`)).data.map(t => t.text)
  console.log('real traits before:', JSON.stringify(before))
  const n = before.length
  const ok = await waitFor(async () => {
    const w = await workFor(f.id)
    return (await traitCount(f.id)) < n && !w ? 'ok' : (w?.status === 'Dead' ? 'dead' : null)
  }, 600000)
  const w = await workFor(f.id)
  const rc = await calls(f.id)
  check(ok === 'ok', `F: real model (${model}) consolidates ${n} traits`, `${ok}; now ${await traitCount(f.id)}; ${rc.length} calls, ${rc.filter(c => c.failed).length} failed; finish: ${[...new Set(rc.map(c => c.finishReason))]}; ${(w?.lastFailure || '').slice(0, 200)}`)
  const after = (await api(`/api/bots/${f.id}/persona/traits`)).data.map(t => t.text)
  console.log('real traits after:', JSON.stringify(after))
  check(after.length >= min && after.length <= above, `F: result within the target range ${min}–${above}`, `${n} → ${after.length}`)
  // drop the stored key: the bot first (a preset in use cannot be deleted), then the preset
  await api(`/api/bots/${f.id}`, { method: 'DELETE' })
  const del = await api(`/api/presets/${real.id}`, { method: 'DELETE' })
  check(del.status < 300, 'F: the real preset (and its key) is deleted', String(del.status))
}

await mode({ maxPromptTokens: 0, truncate: null })
console.log(failures ? `\n${failures} check(s) failed` : '\nall checks passed')
process.exit(failures ? 1 : 0)
