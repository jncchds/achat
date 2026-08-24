// Hub-level check of the detached-run contract (§4.2):
//   reattach idle → null; in-flight → snapshot; concurrent send → refused;
//   stop → chat.stopped; a run started here completes with events.
// Against the containerized app at :5001 (admin/admin); throwaway bot deleted after.
import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr'

const BASE = 'http://localhost:5001'
const j = async (method, path, body) => {
  const res = await fetch(BASE + path, {
    method,
    headers: { 'Content-Type': 'application/json' },
    body: body ? JSON.stringify(body) : undefined,
  })
  const text = await res.text()
  let json = null; try { json = JSON.parse(text) } catch { /* 204 etc. */ }
  return { status: res.status, body: json }
}

const login = await j('POST', '/api/auth/login', { username: 'admin', password: 'admin' })
const token = login.body.token
const H = { 'Authorization': `Bearer ${token}`, 'Content-Type': 'application/json' }

const created = await (await fetch(BASE + '/api/bots', {
  method: 'POST', headers: H,
  body: JSON.stringify({ name: 'E2E Hub', corePersona: 'A concise narrator.', speechStyle: 'terse', greeting: null, presetId: 3 }),
})).json()
const botId = created.id
console.log('temp bot', botId)

const LONG = 'Write a detailed 800-word story about a lighthouse keeper, his cat, a storm, and the sea, with a twist at the end.'

const conn = new HubConnectionBuilder()
  .withUrl(BASE + '/hubs/chat', { accessTokenFactory: () => token })
  .configureLogging(LogLevel.Warning)
  .build()

const events = []
conn.on('chat.started', () => { events.push('started') })
conn.on('chat.delta', () => { events.push('delta') })
conn.on('chat.completed', () => { events.push('completed') })
conn.on('chat.stopped', () => { events.push('stopped') })
conn.on('chat.failed', m => { events.push(`failed: ${m.message}`) })
conn.onclose(e => console.log('  !! onclose:', e ? e.message : '(clean)'))
await conn.start()

const wait = async (pred, ms, what) => {
  const t0 = Date.now()
  while (Date.now() - t0 < ms) {
    if (pred()) return true
    await new Promise(r => setTimeout(r, 200))
  }
  throw new Error(`timeout: ${what} — events: [${events.slice(0, 8).join(", ")}...] total=${events.length}`)
}
const has = t => events.includes(t)
const count = t => events.filter(e => e === t).length

// 1) idle reattach → null
const idle = await conn.invoke('ReattachAsync', botId)
console.log('1) idle reattach =', idle, idle === null ? '✓' : '✗ (expected null)')

// 2) start a long run; wait for the first delta
await conn.invoke('SendAsync', botId, LONG)
await wait(() => has('delta'), 240000, 'first delta')
console.log('2) first delta received ✓')

// 3) in-flight reattach → a snapshot of the run (content may still be short —
//    reasoning models stream empty content while thinking)
const snap = await conn.invoke('ReattachAsync', botId)
console.log('3) in-flight reattach =',
  snap ? `✓ userMessageId=${snap.userMessageId} contentLen=${snap.content.length}` : '✗ null (run already finished?)')

// 4) concurrent send must be refused while a run is in flight
let refused = false
try {
  await conn.invoke('SendAsync', botId, 'another request')
} catch {
  refused = true // the only SendAsync failure mid-run is the refusal
}
console.log('4) concurrent send refused =', refused, refused ? '✓' : '✗ (run may have finished early)')

// 5) wait for completion
await wait(() => has('completed'), 300000, 'completed')
console.log(`5) completed ✓ (deltas: ${count('delta')})`)

// 6) start another run and Stop it mid-stream → chat.stopped
const deltasBefore = count('delta')
await conn.invoke('SendAsync', botId, LONG)
await wait(() => count('delta') > deltasBefore + 2, 240000, 'delta for stop test')
await conn.invoke('StopAsync', botId)
await wait(() => has('stopped'), 30000, 'chat.stopped')
console.log('6) stop → chat.stopped ✓')

// cleanup
await fetch(BASE + `/api/bots/${botId}`, { method: 'DELETE', headers: H })
await conn.stop()
console.log('DONE — bot', botId, 'deleted')
