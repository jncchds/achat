// Phase 3 E2E: continuity beyond the recent window (via summary) + gap awareness.
// Runs against the throwaway instance on :5002 (HistoryTurns=4, SummaryTailTokens=50,
// GapThresholdMinutes=1).
const BASE = 'http://localhost:5002'
const sleep = ms => new Promise(r => setTimeout(r, ms))

async function api(path, opts = {}) {
  const res = await fetch(BASE + path, {
    ...opts,
    headers: { 'Content-Type': 'application/json', ...(opts.headers || {}), ...(opts.token ? { Authorization: `Bearer ${opts.token}` } : {}) },
  })
  if (!res.ok) throw new Error(`${path} → ${res.status}: ${(await res.text()).slice(0, 300)}`)
  return res.json()
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

// ── 1. login + bot ──────────────────────────────────────────────────────────
const login = await api('/api/auth/login', { method: 'POST', body: JSON.stringify({ username: 'admin', password: 'admin', timezone: 'Europe/Moscow' }) })
const token = login.token

const botName = process.env.PHASE3_BOT || 'Echo'
const bots = await api('/api/bots', { token })
let bot = bots.find(b => b.name === botName)
if (!bot) {
  bot = await api('/api/bots', {
    method: 'POST', token,
    body: JSON.stringify({
      name: botName, presetId: 1,
      corePersona: 'A cheerful, meticulous friend who remembers every detail you share. Warm, a little playful, always pays attention.',
      speechStyle: 'Casual and lively. Asks follow-up questions.',
      greeting: "Hey! Good to see you.",
    }),
  })
}
console.log('bot:', bot.id, bot.name)

// ── 2. plant facts across 6 messages ────────────────────────────────────────
const plants = [
  'Hey Echo, quick one: my cat is named Pixel.',
  'Also — I moved to a new apartment on the 14th. It has a little balcony.',
  'Random: my birthday is in March, in case you ever ask.',
  'Anyway, I start a pottery class next month, on Tuesdays.',
  'How has your week been?',
  'Meh. Tired. But the new place is nice.',
]
for (const p of plants) {
  const r = await chat(token, bot.id, p)
  console.log('·', r.content.slice(0, 80))
  await sleep(2500)
}

// ── 3. wait for the summary worker to cover the whole exchange ──────────────
// Sweeps run every 15s; wait for a pass, then settle so the LAST planted message
// is folded in before we test recall.
let summary = null
for (let i = 0; i < 24; i++) {
  await sleep(5000)
  summary = await fetch(`http://localhost:5002/api/interactions?take=5`, { headers: { Authorization: `Bearer ${token}` } })
    .then(r => r.ok ? r.json() : [])
    .then(list => list.find(x => x.purpose === 'Summarisation' && x.botId === bot.id))
    .catch(() => null)
  if (summary) break
}
if (!summary) { console.log('FAIL: no Summarisation interaction appeared'); process.exit(1) }
console.log('✓ summarisation ran →', summary.model, summary.failed ? 'FAILED: ' + summary.httpStatus : 'ok')
console.log('… settling 30s so the latest messages are folded in …')
await sleep(30_000)

const detail = await api(`/api/interactions/${summary.id}`, { token })
console.log('── summary content ──')
console.log(detail.responseContent?.slice(0, 400))

// ── 4. ask about facts now OUTSIDE the 4-message window ────────────────────
const recall = await chat(token, bot.id, 'Right — what is my cat called again, and what month is my birthday?')
console.log('── recall answer ──', recall.content)
const recallOk = /pixel/i.test(recall.content) && /march/i.test(recall.content)
console.log(recallOk ? '✓ recalled both planted facts (cat + birthday)' : '✗ recall incomplete')

// ── 5. the gap: wait > 1 minute, then check the bot notices ─────────────────
console.log('… waiting 75s for the gap threshold (1 min) to pass …')
await sleep(75_000)
const gap = await chat(token, bot.id, 'Hey. Long time no talk — how long has it actually been?')
console.log('── gap answer ──', gap.content)
const gapOk = /(hour|minute|while|bit|couple|a little)/i.test(gap.content)
console.log(gapOk ? '✓ bot reacted to the gap' : '✗ bot did not acknowledge the gap')

// ── 6. the request must carry the gap marker ────────────────────────────────
const last = await api('/api/interactions?take=1', { token })
const lastDetail = await api(`/api/interactions/${last[0].id}`, { token })
const req = JSON.parse(lastDetail.requestBody)
const marker = req.find(m => /— (an hour or two|\d+ hours?|a day|\d+ days|\d+ months?) later —/.test(m.content))
console.log(marker ? `✓ gap marker present in: "${marker.content.slice(0, 60)}…"` : '✗ no gap marker in request')

console.log(recallOk && gapOk ? '\nPHASE 3 E2E: PASS' : '\nPHASE 3 E2E: FAIL (see ✗)')
process.exit(recallOk ? 0 : 1)
