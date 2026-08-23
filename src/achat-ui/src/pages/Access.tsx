import { useCallback, useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import {
  apiError, myAccessRequests, requestAccessByCode,
  type AccessRequestDto, type AccessRequestResponse,
} from '../api'

const STATUS: Record<string, { text: string; cls: string }> = {
  Pending: { text: 'pending', cls: 'pill accent' },
  Approved: { text: 'approved', cls: 'pill' },
  Rejected: { text: 'rejected', cls: 'pill danger' },
}

export default function Access() {
  const [code, setCode] = useState('')
  const [requests, setRequests] = useState<AccessRequestDto[]>([])
  const [message, setMessage] = useState<{ kind: 'ok' | 'err'; text: string } | null>(null)
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)

  const load = useCallback(() => {
    myAccessRequests().then(setRequests).catch(e => setError(apiError(e)))
  }, [])
  useEffect(() => { load() }, [load])

  const submit = async (e: React.FormEvent) => {
    e.preventDefault()
    const c = code.trim()
    if (!c) return
    setBusy(true); setMessage(null); setError('')
    try {
      const res: AccessRequestResponse = await requestAccessByCode(c)
      setCode('')
      if (res.state === 'already') {
        setMessage({ kind: 'ok', text: `You already have access to ${res.botName}.` })
      } else {
        const verb = res.state === 'reopened' ? 're-opened' : 'sent'
        setMessage({ kind: 'ok', text: `Your request to join ${res.botName} was ${verb}. The creator approves requests from their bot's settings.` })
      }
      load()
    } catch (err) {
      const r = (err as { response?: { status?: number } })?.response
      if (r?.status === 409) {
        // Already have access / you created it — treat as informational.
        const msg = (err as { response?: { data?: { message?: string } } })?.response?.data?.message
          ?? 'You already have access to this bot.'
        setMessage({ kind: 'ok', text: msg })
      } else {
        setMessage({ kind: 'err', text: apiError(err) })
      }
    } finally {
      setBusy(false)
    }
  }

  return (
    <div>
      <div className="page-header">
        <h2>Join a bot</h2>
        <span className="hint">Ask a friend for their bot's invite code, then request access here.</span>
      </div>

      <div className="card" style={{ marginBottom: '1rem' }}>
        <form onSubmit={submit} className="row" style={{ alignItems: 'center' }}>
          <input
            className="search-input"
            style={{ flex: 1 }}
            placeholder="Invite code (a long string of letters and numbers)"
            value={code}
            onChange={e => setCode(e.target.value)}
            autoFocus
          />
          <button className="btn-primary" disabled={busy || !code.trim()}>
            {busy ? 'Requesting…' : 'Request access'}
          </button>
        </form>
        {message && (
          <p className={message.kind === 'ok' ? 'hint' : 'error-msg'} style={{ marginTop: '0.75rem' }}>
            {message.text}
          </p>
        )}
        <p className="hint" style={{ marginTop: '0.75rem', marginBottom: 0 }}>
          Presenting a code only <b>requests</b> access — the bot's creator approves each one.
          Once approved, the bot appears in your list with a private conversation and memories
          of its own.
        </p>
      </div>

      <h3 style={{ marginBottom: '0.5rem' }}>Your requests</h3>
      {error && <p className="error-msg" style={{ marginBottom: '0.75rem' }}>{error}</p>}
      {requests.length === 0 ? (
        <div className="card empty">
          <p>You have not requested to join any bots yet.</p>
        </div>
      ) : (
        <div className="list-card">
          {requests.map(r => {
            const s = STATUS[r.status] ?? { text: r.status, cls: 'pill' }
            return (
              <div key={r.id} className="list-row">
                <div className="list-row-main">
                  <div className="list-row-title">
                    {r.status === 'Approved'
                      ? <Link to={`/bots/${r.botId}`}>{r.botName ?? `Bot #${r.botId}`}</Link>
                      : (r.botName ?? `Bot #${r.botId}`)}
                  </div>
                  <div className="list-row-meta">
                    requested {new Date(r.createdAt).toLocaleString()}
                    {r.decidedAt && ` · ${r.status.toLowerCase()} ${new Date(r.decidedAt).toLocaleString()}`}
                    {r.status === 'Rejected' && ' · you can request again any time'}
                  </div>
                </div>
                <div className="list-row-actions">
                  <span className={s.cls}>{s.text}</span>
                  {r.status === 'Approved' && <Link className="btn-ghost btn-sm" to={`/bots/${r.botId}`}>Open →</Link>}
                </div>
              </div>
            )
          })}
        </div>
      )}
    </div>
  )
}
