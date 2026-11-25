// Trait list E2E (§2.4, §4.7) against the mock LLM — turn-envelope step 3.
//   creator turn whose reply adds a trait → BotTrait + BotTurn version + footer payload
//   a second user's turn with the same reply → nothing added
//   human edit, then an immediate bot trait → dropped (cooldown) and noted
//   revert restores the list
//
// Prereqs: `node mockllm.mjs` on 18999; throwaway instance on 5002 (README, E2E scripts).
//   ACHAT_BASE=http://localhost:5002 node scripts/e2e/traits.mjs
import { spawn } from 'node:child_process'
import { fileURLToPath } from 'node:url'

const BASE = process.env.ACHAT_BASE || 'http://localhost:5002'
const FRIEND = 'traitfriend', FRIEND_PASS = 'friendpass123'

async function api(path, opts = {}) {
  const res = await fetch(BASE + path, {
    ...opts,
    headers: { 'Content-Type': 'application/json', ...(opts.token ? { Authorization: `Bearer ${opts.token}` } : {}) },
  })
  const text = await res.text()
  if (!res.ok) throw new Error(`${path} → ${res.status}: ${text.slice(0, 300)}`)
  return text ? JSON.parse(text) : null
}

async function chat(token, botId, message) {
  const sigtest = fileURLToPath(new URL('./sigtest.mjs', import.meta.url))
  const out = await new Promise(resolve => {
    const p = spawn('node', [sigtest, token, String(botId), message],
      { stdio: ['ignore', 'pipe', 'pipe'], env: { ...process.env, ACHAT_BASE: BASE } })
    let data = ''
    p.stdout.on('data', d => data += d)
    p.stderr.on('data', d => data += d)
    p.on('close', () => resolve(data))
  })
  const m = out.match(/COMPLETED (\{.*\})/)
  if (!m) throw new Error('no COMPLETED event:\n' + out.slice(-800))
  return JSON.parse(m[1])
}

let failures = 0
const check = (ok, label, extra = '') => {
  console.log((ok ? '✓' : '✗') + ' ' + label + (extra ? ' — ' + extra : ''))
  if (!ok) failures++
}

const owner = (await api('/api/auth/login', { method: 'POST', body: JSON.stringify({ username: 'admin', password: 'admin' }) })).token

// ── mock preset + a fresh bot ────────────────────────────────────────────────
let preset = (await api('/api/presets', { token: owner })).find(p => p.name === 'Mock')
if (!preset) preset = await api('/api/presets', { method: 'POST', token: owner, body: JSON.stringify({
  name: 'Mock', provider: 'OpenAICompatible', baseEndpoint: 'http://host.docker.internal:18999/v1',
  generationModel: 'mock-model', utilityModel: 'mock-model-small', embeddingModel: 'mock-embed', temperature: 0.7 }) })
const bot = await api('/api/bots', { method: 'POST', token: owner, body: JSON.stringify({
  name: 'TraitBot', presetId: preset.id, corePersona: 'A warm, easygoing friend.', speechStyle: 'Casual.' }) })
console.log('bot', bot.id)

// the bot is shared with a second user
try { await api('/api/admin/users', { method: 'POST', token: owner, body: JSON.stringify({ username: FRIEND, displayName: 'Friend', password: FRIEND_PASS }) }) } catch { /* exists */ }
const friend = (await api('/api/auth/login', { method: 'POST', body: JSON.stringify({ username: FRIEND, password: FRIEND_PASS }) })).token
const share = await api(`/api/bots/${bot.id}/share`, { token: owner })
const code = share.inviteCode
await api('/api/access/request', { method: 'POST', token: friend, body: JSON.stringify({ code }) })
const pending = (await api(`/api/bots/${bot.id}/share`, { token: owner })).requests || []
for (const r of pending) await api(`/api/bots/${bot.id}/share/requests/${r.id}/approve`, { method: 'POST', token: owner })

const offersTraits = b => /(\\+u0022|\\*")traits(\\+u0022|\\*")/.test(b)
const versions = async () => await api(`/api/bots/${bot.id}/persona/versions`, { token: owner })
const traits = async () => await api(`/api/bots/${bot.id}/persona/traits`, { token: owner })

try {
  // ── 1. creator turn adds a trait ──────────────────────────────────────────
  const r1 = await chat(owner, bot.id, 'I cooked again. [[trait:teases them about their cooking]]')
  check(r1.traitChanges?.added?.[0] === 'teases them about their cooking', 'creator turn: chat.completed carries traitChanges', JSON.stringify(r1.traitChanges))
  let t = await traits()
  check(t.length === 1 && t[0].text === 'teases them about their cooking', 'a BotTrait row exists')
  let v = await versions()
  check(v[v.length - 1].source === 'BotTurn' && v[v.length - 1].items.length === 1, 'a BotTurn version with the snapshot', v.map(x => x.source).join(' → '))

  // ── 2. the prompt now lists it with its id, and offers traits ─────────────
  await chat(owner, bot.id, 'hello again')
  const latest = (await api('/api/interactions?take=5', { token: owner })).find(x => x.purpose === 'Chat')
  const body = (await api(`/api/interactions/${latest.id}`, { token: owner })).requestBody
  check(body.includes(`[${t[0].id}] teases them about their cooking`), 'creator prompt shows the trait with its id')
  check(offersTraits(body), 'creator prompt offers the traits field')

  // ── 3. a second user's identical reply changes nothing ────────────────────
  const r2 = await chat(friend, bot.id, 'I cooked too. [[trait:calls them chef]]')
  check(!r2.traitChanges, 'second user: no traitChanges')
  check((await traits()).length === 1, 'second user: nothing added')
  const fb = (await api('/api/interactions?take=5', { token: owner })).find(x => x.purpose === 'Chat')
  const friendBody = (await api(`/api/interactions/${fb.id}`, { token: owner })).requestBody
  check(!offersTraits(friendBody), 'second user prompt does not offer the traits field')

  // ── 4. human edit → the next bot trait is dropped by the cooldown ─────────
  const edited = await api(`/api/bots/${bot.id}/persona/traits/${t[0].id}`, { method: 'PUT', token: owner, body: JSON.stringify({ text: 'teases them gently about their cooking' }) })
  check(edited.source === 'UserEdited', 'human edit appends a UserEdited version')
  const r3 = await chat(owner, bot.id, 'more food [[trait:brings up the soup]]')
  check(!r3.traitChanges, 'cooldown: bot trait dropped')
  check((await traits()).length === 1, 'cooldown: list unchanged')
  const ix = (await api('/api/interactions?take=3', { token: owner })).find(x => x.purpose === 'Chat')
  const detail = await api(`/api/interactions/${ix.id}`, { token: owner })
  check(/cooldown/.test(detail.notes || ''), 'the drop is noted on the interaction', detail.notes)

  // ── 5. revert restores the list ───────────────────────────────────────────
  v = await versions()
  const botTurn = v.find(x => x.source === 'BotTurn')
  const rev = await api(`/api/bots/${bot.id}/persona/versions/${botTurn.id}/revert`, { method: 'POST', token: owner })
  check(rev.source === 'Revert', 'revert appends a Revert version')
  t = await traits()
  check(t.length === 1 && t[0].text === 'teases them about their cooking', 'revert restored the original item text', t[0]?.text)
} finally {
  await api(`/api/bots/${bot.id}`, { method: 'DELETE', token: owner }).catch(() => {})
}

console.log(failures === 0 ? '\nTRAITS E2E: PASS' : `\nTRAITS E2E: FAIL (${failures})`)
process.exit(failures === 0 ? 0 : 1)
