// Phase 8 (operational maturity) verification against a live docker instance:
//  - user management (§7.4): create → disable (login refused 403) → reset + re-enable
//  - kill switch (§12a, §2.5): pause a bot → chat refused with a clear notice
//  - quota (§12a, §2.5): cap at N → chat succeeds until N, then refused
//  - usage visibility (§2.5): per-user breakdown of who is spending the key
// Chat goes through the established sigtest.mjs SignalR helper.
const BASE = process.env.BASE || 'http://localhost:5002'

let token = ''
async function api(path, { method = 'GET', body } = {}) {
  const r = await fetch(BASE + path, {
    method, headers: { authorization: `Bearer ${token}`, 'content-type': 'application/json' }, body,
  })
  const text = await r.text()
  let data
  try { data = JSON.parse(text) } catch { data = text }
  return { status: r.status, data }
}

async function chat(botId, message) {
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
  const done = out.match(/COMPLETED (\{.*\})/)
  const failed = out.match(/FAILED (\{.*\})/)
  if (done) return { completed: true, ...JSON.parse(done[1]) }
  if (failed) return { completed: false, ...JSON.parse(failed[1]) }
  throw new Error('no COMPLETED/FAILED event:\n' + out.slice(-600))
}

let failures = 0
const check = (ok, label, extra = '') => {
  console.log((ok ? '✓' : '✗') + ' ' + label + (extra ? ' — ' + extra : ''))
  if (!ok) failures++
}

// ── login as admin ──
const login = await api('/api/auth/login', { method: 'POST', body: JSON.stringify({ username: 'admin', password: 'admin' }) })
token = login.data.token
if (!token) { console.error('login failed', JSON.stringify(login.data).slice(0, 200)); process.exit(1) }

// ── a bot on a working preset (reuse one that exists) ──
const presets = (await api('/api/presets')).data
// Prefer the reliable hosted preset; fall back to any with a generation model.
const preset = presets.find(p => /openrouter/i.test(p.name)) || presets.find(p => p.generationModel)
if (!preset) { console.error('no preset available'); process.exit(1) }

const bot = await api('/api/bots', { method: 'POST', body: JSON.stringify({
  name: 'V8Ctl', presetId: preset.id, corePersona: 'A concise test friend.',
}) })
if (!bot.data.id) { console.error('bot create failed', JSON.stringify(bot.data).slice(0, 200)); process.exit(1) }
const botId = bot.data.id

// ── 1. user management ──
const carol = await api('/api/admin/users', { method: 'POST', body: JSON.stringify({ username: 'carol', password: 'carolpass123' }) })
const carolId = carol.data.id
check(!!carolId, 'admin creates a user', carol.data.username)

// disable → login refused 403
await api(`/api/admin/users/${carolId}`, { method: 'PATCH', body: JSON.stringify({ isActive: false }) })
const disLogin = await api('/api/auth/login', { method: 'POST', body: JSON.stringify({ username: 'carol', password: 'carolpass123' }) })
check(disLogin.status === 403, 'disabled user is refused at login (403)', `HTTP ${disLogin.status}: ${disLogin.data?.message ?? ''}`)

// reset password + re-enable → login ok
await api(`/api/admin/users/${carolId}/reset-password`, { method: 'POST', body: JSON.stringify({ password: 'newcarolpass1' }) })
await api(`/api/admin/users/${carolId}`, { method: 'PATCH', body: JSON.stringify({ isActive: true }) })
const reLogin = await api('/api/auth/login', { method: 'POST', body: JSON.stringify({ username: 'carol', password: 'newcarolpass1' }) })
check(reLogin.status === 200 && reLogin.data.token, 'reset + re-enable → login succeeds', `HTTP ${reLogin.status}`)

// ── 2. kill switch ──
await api(`/api/bots/${botId}/settings`, { method: 'PATCH', body: JSON.stringify({ suspended: true }) })
const paused = await chat(botId, 'hello?')
check(!paused.completed && /pause|paused|creator/i.test(paused.message || ''),
  'paused bot refuses to answer (kill switch)', paused.message || 'no message')

await api(`/api/bots/${botId}/settings`, { method: 'PATCH', body: JSON.stringify({ suspended: false }) })

// ── 3. quota ──
// First chat (unlimited) should succeed and log an interaction.
const unlimited = await chat(botId, 'Say the word: penguin.')
check(unlimited.completed, 'bot answers when not suspended and unquotaed', (unlimited.content || '').slice(0, 40))

await api(`/api/bots/${botId}/settings`, { method: 'PATCH', body: JSON.stringify({ maxInteractions: 1 }) })
const overQuota = await chat(botId, 'hello again?')
check(!overQuota.completed && /limit|quota|interactions/i.test(overQuota.message || ''),
  'quota-exceeded bot refuses to answer', overQuota.message || 'no message')

// ── 4. usage visibility ──
const usage = (await api(`/api/bots/${botId}/usage`)).data
check(usage && usage.interactions >= 1, 'usage endpoint reports interactions', `${usage?.interactions} interactions`)
check(usage && Array.isArray(usage.byUser) && usage.byUser.length >= 1, 'usage has a per-user breakdown', `${usage?.byUser?.length} user(s)`)

// ── cleanup ──
await api(`/api/bots/${botId}`, { method: 'DELETE' })
await api(`/api/admin/users/${carolId}`, { method: 'DELETE' }).catch(() => {})

console.log(failures === 0 ? '\nALL PASSED' : `\n${failures} FAILED`)
process.exit(failures === 0 ? 0 : 1)
