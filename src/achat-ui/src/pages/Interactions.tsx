import { Fragment, useCallback, useEffect, useState } from 'react'
import { apiError, getInteraction, listBots, listInteractions, type Bot, type Interaction, type InteractionDetail } from '../api'

function purposeLabel(p: string) {
  return p.charAt(0).toUpperCase() + p.slice(1).toLowerCase()
}

function prettyJson(s: string) {
  try { return JSON.stringify(JSON.parse(s), null, 2) } catch { return s }
}

function TimeCell({ iso }: { iso: string }) {
  const d = new Date(iso)
  return <>{d.toLocaleDateString()} <span className="hint">{d.toLocaleTimeString()}</span></>
}

export default function Interactions() {
  const [items, setItems] = useState<Interaction[]>([])
  const [bots, setBots] = useState<Bot[]>([])
  const [botId, setBotId] = useState<number | ''>('')
  const [failedOnly, setFailedOnly] = useState(false)
  const [purpose, setPurpose] = useState('')
  const [error, setError] = useState('')
  const [expanded, setExpanded] = useState<number | null>(null)
  const [detail, setDetail] = useState<InteractionDetail | null>(null)

  const load = useCallback(() => {
    const params: { botId?: number; failed?: boolean; purpose?: string; take?: number } = { take: 200 }
    if (botId !== '') params.botId = botId
    if (failedOnly) params.failed = true
    if (purpose) params.purpose = purpose
    listInteractions(params).then(setItems).catch(e => setError(apiError(e)))
  }, [botId, failedOnly, purpose])

  useEffect(() => { load() }, [load])
  useEffect(() => { listBots().then(setBots).catch(() => {}) }, [])

  const toggle = (i: Interaction) => {
    if (expanded === i.id) { setExpanded(null); setDetail(null); return }
    setExpanded(i.id)
    setDetail(null)
    getInteraction(i.id).then(setDetail).catch(e => setError(apiError(e)))
  }

  const purposes = Array.from(new Set(items.map(i => i.purpose)))

  return (
    <div>
      <div className="page-header">
        <h2>LLM Interactions</h2>
        <span className="hint">Every call the system makes on your behalf — the window into “why did my friend say that” (§4.4).</span>
      </div>
      {error && <p className="error-msg" style={{ marginBottom: '1rem' }}>{error}</p>}

      <div className="card" style={{ marginBottom: '1rem', display: 'flex', gap: '1.25rem', alignItems: 'flex-end', flexWrap: 'wrap' }}>
        <label style={{ margin: 0, flex: '1 1 180px' }}>Bot
          <select value={botId} onChange={e => setBotId(e.target.value === '' ? '' : parseInt(e.target.value))}>
            <option value="">All bots</option>
            {bots.map(b => <option key={b.id} value={b.id}>{b.name}</option>)}
          </select>
        </label>
        <label style={{ margin: 0, flex: '1 1 180px' }}>Purpose
          <select value={purpose} onChange={e => setPurpose(e.target.value)}>
            <option value="">All</option>
            {purposes.map(p => <option key={p} value={p}>{purposeLabel(p)}</option>)}
          </select>
        </label>
        <label className="checkbox-label" style={{ margin: 0 }}>
          <input type="checkbox" checked={failedOnly} onChange={e => setFailedOnly(e.target.checked)} />
          Failed only
        </label>
        <span className="hint" style={{ margin: 0 }}>{items.length} shown (newest first)</span>
      </div>

      <div className="table-wrap">
        <table className="data">
          <thead>
            <tr>
              <th>Time</th>
              <th>Bot</th>
              <th>User</th>
              <th>Purpose</th>
              <th>Model</th>
              <th>Tokens</th>
              <th>Outcome</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            {items.length === 0 && (
              <tr><td colSpan={8} className="empty">No interactions recorded yet.</td></tr>
            )}
            {items.map(i => (
              <Fragment key={i.id}>
                <tr className="clickable" onClick={() => toggle(i)}>
                  <td><TimeCell iso={i.createdAt} /></td>
                  <td>{i.botName ?? '—'}</td>
                  <td>{i.username}</td>
                  <td>{purposeLabel(i.purpose)}</td>
                  <td className="mono">{i.model}</td>
                  <td>{i.totalTokens ?? '—'}</td>
                  <td>{i.failed ? <span className="pill danger">failed</span> : <span className="pill success">ok</span>}</td>
                  <td>{expanded === i.id ? '▲' : '▼'}</td>
                </tr>
                {expanded === i.id && (
                  <tr>
                    <td colSpan={8} style={{ background: 'var(--input-bg)' }}>
                      {detail ? (
                        <>
                          {detail.failureReason && <><div className="hint" style={{ marginTop: '0.5rem' }}>Failure</div><pre className="code">{detail.failureReason}</pre></>}
                          <div className="hint" style={{ marginTop: '0.5rem' }}>Request (as sent)</div>
                          <pre className="code">{prettyJson(detail.requestBody)}</pre>
                          {detail.reasoning && <><div className="hint" style={{ marginTop: '0.5rem' }}>Reasoning</div><pre className="code">{detail.reasoning}</pre></>}
                          {detail.responseContent && <><div className="hint" style={{ marginTop: '0.5rem' }}>Response</div><pre className="code">{detail.responseContent}</pre></>}
                          <div className="hint" style={{ marginTop: '0.5rem' }}>
                            Finish: {detail.finishReason ?? '—'} · Tokens {detail.promptTokens}/{detail.completionTokens}/{detail.totalTokens}
                          </div>
                        </>
                      ) : <p className="hint">Loading…</p>}
                    </td>
                  </tr>
                )}
              </Fragment>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  )
}
