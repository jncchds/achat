// Phase 5 E2E: directives + evolved traits + versioning/revert.
// Runs against a throwaway instance with fast evolution triggers.
const BASE = process.env.ACHAT_BASE || 'http://localhost:5002'
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

// ── setup ────────────────────────────────────────────────────────────────────
const login = await api('/api/auth/login', { method: 'POST', body: JSON.stringify({ username: 'admin', password: 'admin', timezone: 'Europe/Moscow' }) })
const token = login.token

const botName = process.env.PHASE5_BOT || 'Sage'
const presetId = parseInt(process.env.PHASE5_PRESET || '1', 10)
const bots = await api('/api/bots', { token })
let bot = bots.find(b => b.name === botName)
if (!bot) {
  bot = await api('/api/bots', {
    method: 'POST', token,
    body: JSON.stringify({
      name: botName, presetId,
      corePersona: 'A thoughtful friend with a dry sense of humour. Values honesty over comfort.',
      speechStyle: 'Direct, a little wry.',
      greeting: 'There you are, Sam.',
    }),
  })
}
console.log('bot:', bot.id, bot.name)

// ── directives (§2.4) ───────────────────────────────────────────────────────
const d1 = await api(`/api/bots/${bot.id}/directives`, { method: 'POST', token, body: JSON.stringify({ text: 'Call me Sam — never "sir" or any title.' }) })
const d2 = await api(`/api/bots/${bot.id}/directives`, { method: 'POST', token, body: JSON.stringify({ text: 'Be more blunt. Skip the diplomatic padding.' }) })
check(d1.id && d2.id, 'two directives added', `${d1.id}, ${d2.id}`)

const dirToggle = await api(`/api/bots/${bot.id}/directives/${d2.id}`, { method: 'PUT', token, body: JSON.stringify({ isActive: false }) })
check(dirToggle.isActive === false, 'directive toggled off')
await api(`/api/bots/${bot.id}/directives/${d2.id}`, { method: 'PUT', token, body: JSON.stringify({ isActive: true }) })
const dirs = await api(`/api/bots/${bot.id}/directives`, { token })
check(dirs.length === 2 && dirs.every(d => d.isActive), 'directive re-activated; list has both')

// ── conversation that gives the evolution pass material ─────────────────────
const turns = [
  "Sam here. Long day — the flat's heating gave up again. Third time this winter.",
  "Tell me about that. What's it doing this time?",
  "Hissing like a snake. I've named it Steve, since it keeps me up at night.",
  "Steve. I respect a problem with a name. Hissing or banging?",
  "Hissing. And look — you're the only one I tell about Steve.",
  "Then Steve is ours. Ours and hissing, at that.",
  "Ha! Ours. I like that. Ours and hissing.",
  "Right. Now go to sleep, Sam — Steve can wait until morning."
]
for (const t of turns) {
  await chat(token, bot.id, t)
  await sleep(1500)
}
console.log('conversation done — waiting for the evolution pass…')

// ── wait for evolution to produce a non-empty version ────────────────────────
let versions = []
for (let i = 0; i < 36; i++) {
  await sleep(5000)
  versions = (await api(`/api/bots/${bot.id}/persona/versions`, { token })) || []
  const evolved = versions.find(v => v.source === 'Evolution' && v.content.trim())
  if (evolved) break
}
const evolvedV = versions.find(v => v.source === 'Evolution' && v.content.trim())
check(!!evolvedV, 'evolution produced a version', evolvedV ? evolvedV.content.slice(0, 90).replace(/\n/g, ' ') : '(none)')
check(versions.some(v => v.source === 'Initial'), 'initial baseline present in history')
check(!!evolvedV && /steve|hissing/i.test(evolvedV.content), 'evolved traits reference the settled material (Steve/hissing)')

// ── the next prompt must carry core → evolved → directives in order ─────────
const probe = await chat(token, bot.id, 'Quick check — what do you call me, and what is the heating called?')
const interactions = await api('/api/interactions?take=5', { token })
const latestChat = interactions.find(x => x.purpose === 'Chat')
const detail = await api(`/api/interactions/${latestChat.id}`, { token })
const reqBody = detail.requestBody || ''
const sys = reqBody.slice(0, 20000)
const coreIdx = sys.indexOf('thoughtful friend')
const evolvedIdx = evolvedV ? sys.indexOf(evolvedV.content.slice(0, 40)) : -1
const dirIdx = sys.indexOf('Call me Sam')
check(coreIdx >= 0, 'prompt contains core persona')
check(evolvedIdx > coreIdx, 'prompt contains evolved traits after core', `core@${coreIdx} evolved@${evolvedIdx}`)
check(dirIdx > (evolvedIdx >= 0 ? evolvedIdx : coreIdx), 'directives injected last', `dir@${dirIdx}`)
check(/sam/i.test(probe.content) && /steve/i.test(probe.content), 'bot obeys directive (calls user Sam) + knows Steve', probe.content.slice(0, 120))

// ── edit the evolved layer → UserEdited version ─────────────────────────────
const edited = await api(`/api/bots/${bot.id}/persona`, {
  method: 'PUT', token,
  body: JSON.stringify({ content: (evolvedV ? evolvedV.content : '') + ' Sam calls the bot "my other flatmate".' }),
})
check(edited.source === 'UserEdited', 'edit appends a UserEdited version', `v${edited.id}`)

// ── revert to the evolution version ─────────────────────────────────────────
const afterEdit = await api(`/api/bots/${bot.id}/persona/versions`, { token })
const target = afterEdit.find(v => v.source === 'Evolution')
const reverted = await api(`/api/bots/${bot.id}/persona/versions/${target.id}/revert`, { method: 'POST', token })
check(reverted.source === 'Revert', 'revert appends a Revert version', `v${reverted.id}`)
check(reverted.content === target.content, 'reverted content matches the target version')

// ── final history shape ──────────────────────────────────────────────────────
const final = await api(`/api/bots/${bot.id}/persona/versions`, { token })
const newest = final[final.length - 1]
check(newest.source === 'Revert', 'current version is the revert', newest.source)
check(final.length >= 4, 'full history readable (initial + evolution + edit + revert)', final.map(v => v.source).join(' → '))

// ── evolution toggle round-trips ─────────────────────────────────────────────
const off = await api(`/api/bots/${bot.id}`, { method: 'PUT', token, body: JSON.stringify({
  name: bot.name, corePersona: bot.corePersona, speechStyle: bot.speechStyle, greeting: bot.greeting,
  presetId: bot.presetId, evolutionEnabled: false }) })
check(off.evolutionEnabled === false, 'evolution can be switched off')
await api(`/api/bots/${bot.id}`, { method: 'PUT', token, body: JSON.stringify({
  name: bot.name, corePersona: bot.corePersona, speechStyle: bot.speechStyle, greeting: bot.greeting,
  presetId: bot.presetId, evolutionEnabled: true }) })

console.log(failures === 0 ? '\nPHASE 5 E2E: PASS' : `\nPHASE 5 E2E: FAIL (${failures})`)
process.exit(failures === 0 ? 0 : 1)
