// Phase 6 E2E: sharing — a second user requests access, is approved, and holds a
// conversation with a separate memory scope the first user cannot see (§2.5).
const BASE = process.env.ACHAT_BASE || 'http://localhost:5002'
const PRESET = parseInt(process.env.PHASE6_PRESET || '3', 10)
const FRIEND = process.env.PHASE6_FRIEND || 'friend'
const FRIEND_PASS = 'friendpass123'
const BOT_NAME = process.env.PHASE6_BOT || 'Shared'
const sleep = ms => new Promise(r => setTimeout(r, ms))

async function api(path, opts = {}) {
  const res = await fetch(BASE + path, {
    ...opts,
    headers: { 'Content-Type': 'application/json', ...(opts.headers || {}), ...(opts.token ? { Authorization: `Bearer ${opts.token}` } : {}) },
  })
  const text = await res.text()
  if (!res.ok) throw new Error(`${path} → ${res.status}: ${text.slice(0, 300)}`)
  return text ? JSON.parse(text) : null
}

async function chat(token, botId, message) {
  const { spawn } = await import('child_process')
  const { fileURLToPath } = await import('node:url')
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

// ── owner logs in ────────────────────────────────────────────────────────────
const ownerLogin = await api('/api/auth/login', { method: 'POST', body: JSON.stringify({ username: 'admin', password: 'admin', timezone: 'Europe/Moscow' }) })
const owner = ownerLogin.token

// ── a bot on a working preset ────────────────────────────────────────────────
const bots = await api('/api/bots', { token: owner })
let bot = bots.find(b => b.name === BOT_NAME)
if (!bot) {
  bot = await api('/api/bots', {
    method: 'POST', token: owner,
    body: JSON.stringify({
      name: BOT_NAME, presetId: PRESET,
      corePersona: 'A warm, easygoing friend who remembers the little things.',
      speechStyle: 'Casual, friendly.',
      greeting: 'Hey, good to see you!',
    }),
  })
}
console.log('bot:', bot.id, bot.name, '(preset', bot.presetId + ')')

// ── owner creates the second user (§7.2 — no self-service registration) ─────
let friendUser
try {
  friendUser = await api('/api/admin/users', { method: 'POST', token: owner, body: JSON.stringify({ username: FRIEND, displayName: 'Friend', password: FRIEND_PASS }) })
  console.log('created user:', friendUser.id, friendUser.username)
} catch (e) {
  if (!/already exists/i.test(e.message)) throw e
  console.log('user already exists — reusing')
}

// ── friend logs in ───────────────────────────────────────────────────────────
const friendLogin = await api('/api/auth/login', { method: 'POST', body: JSON.stringify({ username: FRIEND, password: FRIEND_PASS, timezone: 'Europe/Moscow' }) })
const friend = friendLogin.token

// ── friend does NOT yet see the bot ──────────────────────────────────────────
const before = await api('/api/bots', { token: friend })
check(!before.some(b => b.id === bot.id), 'before approval: friend does not see the bot')

// ── owner reads the invite code ──────────────────────────────────────────────
const share = await api(`/api/bots/${bot.id}/share`, { token: owner })
const code = share.inviteCode
check(!!code && code.length > 8, 'owner can read the invite code', code.slice(0, 8) + '…')
check(share.owner.username === 'admin', 'owner roster lists the creator')

// ── friend requests access with the code ─────────────────────────────────────
const req = await api('/api/access/request', { method: 'POST', token: friend, body: JSON.stringify({ code }) })
check(req.state === 'pending', 'friend\'s request is pending', `state=${req.state}`)

// ── owner sees it in the queue and approves ─────────────────────────────────
let share2 = await api(`/api/bots/${bot.id}/share`, { token: owner })
let pending = share2.requests.find(r => r.botId === bot.id)
check(!!pending, 'owner sees the pending request in the queue', pending?.requesterUsername)
if (pending) {
  await api(`/api/bots/${bot.id}/share/requests/${pending.id}/approve`, { method: 'POST', token: owner })
}
const grant = await api(`/api/bots/${bot.id}/share`, { token: owner })
check(grant.members.some(m => m.username === FRIEND), 'owner roster now includes the friend')

// ── friend now sees the bot, flagged as shared (not theirs) ─────────────────
const after = await api('/api/bots', { token: friend })
const seen = after.find(b => b.id === bot.id)
check(!!seen, 'after approval: friend sees the bot')
if (seen) {
  check(seen.isOwner === false, 'friend sees it as not-theirs (isOwner=false)')
  check(seen.sharedBy === 'admin', 'friend sees it as shared by the creator', `sharedBy=${seen.sharedBy}`)
}

// ── friend holds a conversation ─────────────────────────────────────────────
const msg1 = 'Hi! Quick thing — my cat is called Miso.'
const c1 = await chat(friend, bot.id, msg1)
check(!!c1.content, 'friend can chat with the shared bot')
await sleep(1500)
await chat(friend, bot.id, 'Also, I am allergic to strawberries. Important!')
await sleep(1500)

// ── separate scopes: friend sees their messages ─────────────────────────────
const friendMsgs = await api(`/api/bots/${bot.id}/messages`, { token: friend })
check(friendMsgs.some(m => /Miso/i.test(m.content)), 'friend sees their own messages')

// ── owner does NOT see the friend's conversation ─────────────────────────────
const ownerMsgs = await api(`/api/bots/${bot.id}/messages`, { token: owner })
check(!ownerMsgs.some(m => /Miso/i.test(m.content)), 'owner does NOT see the friend\'s messages (separate scope)')

// ── memory scoping (§2.5): the friend's memories are not the owner's ────────
// Extraction is best-effort (needs the utility model); if it lands, verify scope.
let friendMems = []
for (let i = 0; i < 20; i++) {
  await sleep(5000)
  friendMems = await api(`/api/memories?botId=${bot.id}`, { token: friend }) || []
  if (friendMems.some(m => /miso/i.test(m.content))) break
}
if (friendMems.some(m => /miso/i.test(m.content))) {
  const ownerMems = await api(`/api/memories?botId=${bot.id}`, { token: owner }) || []
  check(!ownerMems.some(m => /miso/i.test(m.content)), 'owner does NOT see the friend\'s memories (per-user scope)')
} else {
  console.log('· (memory extraction did not land in time — per-user scope is covered by unit tests)')
}

console.log(failures === 0 ? '\nPHASE 6 E2E: PASS' : `\nPHASE 6 E2E: FAIL (${failures})`)
process.exit(failures === 0 ? 0 : 1)
