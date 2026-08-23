import { useCallback, useEffect, useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import {
  addDirective, apiError, deleteDirective, editPersona, getBot, listDirectives,
  listPersonaVersions, revertPersona, updateBot, updateDirective,
  type Bot, type Directive, type PersonaVersion,
} from '../api'

const SOURCE_LABEL: Record<string, { text: string; cls: string }> = {
  Initial: { text: 'initial', cls: 'pill' },
  Evolution: { text: 'evolved', cls: 'pill accent' },
  UserEdited: { text: 'edited by you', cls: 'pill accent' },
  Revert: { text: 'revert', cls: 'pill' },
}

export default function BotSettings() {
  const { botId } = useParams()
  const bot = parseInt(botId!, 10)

  const [botRow, setBotRow] = useState<Bot | null>(null)
  const [directives, setDirectives] = useState<Directive[]>([])
  const [versions, setVersions] = useState<PersonaVersion[]>([])
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)

  // core persona editor
  const [corePersona, setCorePersona] = useState('')
  const [speechStyle, setSpeechStyle] = useState('')
  const [personaDirty, setPersonaDirty] = useState(false)

  // directives
  const [newDirective, setNewDirective] = useState('')
  const [editingDirId, setEditingDirId] = useState<number | null>(null)
  const [dirText, setDirText] = useState('')

  // evolved traits
  const [editingTraits, setEditingTraits] = useState(false)
  const [traitsText, setTraitsText] = useState('')
  const [viewingVersion, setViewingVersion] = useState<number | null>(null)

  const load = useCallback(() => {
    getBot(bot).then(b => {
      setBotRow(b)
      setCorePersona(prev => (personaDirty ? prev : b.corePersona))
      setSpeechStyle(prev => (personaDirty ? prev : b.speechStyle ?? ''))
    }).catch(e => setError(apiError(e)))
    listDirectives(bot).then(setDirectives).catch(e => setError(apiError(e)))
    listPersonaVersions(bot).then(v => setVersions(v.slice().reverse())).catch(e => setError(apiError(e)))
  }, [bot, personaDirty])

  useEffect(() => { load() }, [load])

  const run = async (fn: () => Promise<unknown>) => {
    setBusy(true); setError('')
    try { await fn(); await load() }
    catch (e) { setError(apiError(e)) } finally { setBusy(false) }
  }

  // ── core persona + evolution toggle ──────────────────────────────────────
  const savePersona = () => {
    if (!botRow) return
    return run(() => updateBot(bot, {
      name: botRow.name,
      avatarUrl: botRow.avatarUrl,
      corePersona: corePersona.trim(),
      speechStyle: speechStyle.trim() || null,
      greeting: botRow.greeting,
      presetId: botRow.presetId,
    }).then(() => setPersonaDirty(false)))
  }

  const toggleEvolution = (on: boolean) => {
    if (!botRow) return
    return run(() => updateBot(bot, {
      name: botRow.name,
      avatarUrl: botRow.avatarUrl,
      corePersona: botRow.corePersona,
      speechStyle: botRow.speechStyle,
      greeting: botRow.greeting,
      presetId: botRow.presetId,
      evolutionEnabled: on,
    }))
  }

  // ── directives ────────────────────────────────────────────────────────────
  const addDir = () => {
    const text = newDirective.trim()
    if (!text) return
    return run(() => addDirective(bot, text)).then(() => setNewDirective(''))
  }

  const startEditDir = (d: Directive) => { setEditingDirId(d.id); setDirText(d.text) }
  const saveDir = (d: Directive) =>
    run(() => updateDirective(bot, d.id, { text: dirText.trim() })).then(() => setEditingDirId(null))
  const toggleDir = (d: Directive) =>
    run(() => updateDirective(bot, d.id, { isActive: !d.isActive }))
  const removeDir = (d: Directive) => {
    if (!confirm(`Remove the directive “${d.text}”?`)) return
    return run(() => deleteDirective(bot, d.id))
  }

  // ── evolved traits ────────────────────────────────────────────────────────
  const current = versions[0] // newest first
  const startEditTraits = () => { setEditingTraits(true); setTraitsText(current?.content ?? '') }
  const saveTraits = () =>
    run(() => editPersona(bot, traitsText.trim())).then(() => setEditingTraits(false))
  const doRevert = (v: PersonaVersion) => {
    if (!confirm(`Revert to the version from ${new Date(v.createdAt).toLocaleString()}?\n\nA new version carrying that content is appended to the history — nothing is lost.`)) return
    return run(() => revertPersona(bot, v.id))
  }

  if (!botRow) {
    return (
      <div>
        <div className="page-header"><h2>Bot settings</h2></div>
        {error && <p className="error-msg">{error}</p>}
        <div className="card empty">Loading…</div>
      </div>
    )
  }

  return (
    <div>
      <div className="page-header">
        <h2>{botRow.name} — personality</h2>
        <span className="hint">
          Three layers (§2.4): your <b>core</b> persona, the <b>evolved traits</b> the friendship
          grows, and your <b>directives</b> — which always win.
        </span>
        <div className="row" style={{ marginTop: '0.5rem' }}>
          <Link className="btn-ghost btn-sm" to={`/bots/${bot}`}>← Back to chat</Link>
        </div>
      </div>
      {error && <p className="error-msg" style={{ marginBottom: '1rem' }}>{error}</p>}

      {/* ── core ── */}
      <div className="card" style={{ marginBottom: '1rem' }}>
        <h3 style={{ marginTop: 0 }}>Core — who they fundamentally are</h3>
        <div className="form-grid">
          <label className="span-2">Core persona
            <textarea rows={5} value={corePersona}
              onChange={e => { setCorePersona(e.target.value); setPersonaDirty(true) }}
              placeholder="Background, temperament, what they care about…" />
          </label>
          <label className="span-2">Speech style
            <input value={speechStyle}
              onChange={e => { setSpeechStyle(e.target.value); setPersonaDirty(true) }}
              placeholder="Short, warm, a little dry…" />
          </label>
        </div>
        <div className="form-actions">
          <button disabled={busy || !personaDirty} onClick={() => void savePersona()}>
            {busy ? 'Saving…' : 'Save core'}
          </button>
        </div>
      </div>

      {/* ── evolution toggle ── */}
      <div className="card" style={{ marginBottom: '1rem' }}>
        <div className="row">
          <label className="check-row">
            <input type="checkbox" checked={botRow.evolutionEnabled} disabled={busy}
              onChange={e => void toggleEvolution(e.target.checked)} />
            <span>
              <b>Evolution on</b>
              <div className="hint">
                A background pass rewrites the evolved-traits layer when your conversation
                has produced enough new material (slow cadence — personality that shifts
                hourly is noise, not character). Only your conversations shape them.
              </div>
            </span>
          </label>
        </div>
      </div>

      {/* ── directives ── */}
      <div className="card" style={{ marginBottom: '1rem' }}>
        <h3 style={{ marginTop: 0 }}>Directives — deliberate, always win</h3>
        <p className="hint" style={{ marginTop: 0 }}>
          Standing instructions, injected verbatim and closest to the generation. Toggle one
          off to keep it without using it.
        </p>
        <div className="list" style={{ marginBottom: '1rem' }}>
          {directives.length === 0 && (
            <p className="hint">None yet — e.g. “Be more blunt”, “Never bring up my ex”.</p>
          )}
          {directives.map(d => (
            <div key={d.id} className={`card memory-card${d.isActive ? '' : ' faded'}`} style={{ marginBottom: '0.5rem' }}>
              <div className="memory-head">
                {!d.isActive && <span className="pill">inactive</span>}
                <span className="hint" style={{ marginLeft: 'auto' }}>
                  {new Date(d.createdAt).toLocaleDateString()}
                </span>
              </div>
              {editingDirId === d.id ? (
                <div className="memory-edit">
                  <textarea value={dirText} onChange={e => setDirText(e.target.value)} rows={2} autoFocus />
                  <div className="row">
                    <button className="btn-primary btn-sm" disabled={busy} onClick={() => void saveDir(d)}>Save</button>
                    <button className="btn-ghost btn-sm" disabled={busy} onClick={() => setEditingDirId(null)}>Cancel</button>
                  </div>
                </div>
              ) : (
                <p className="memory-content">{d.text}</p>
              )}
              <div className="row memory-actions">
                <button className="btn-ghost btn-sm" disabled={busy} onClick={() => void toggleDir(d)}>
                  {d.isActive ? 'Toggle off' : 'Activate'}
                </button>
                {editingDirId !== d.id && (
                  <button className="btn-ghost btn-sm" disabled={busy} onClick={() => startEditDir(d)}>Edit</button>
                )}
                <button className="btn-ghost btn-sm danger-text" disabled={busy} onClick={() => void removeDir(d)}>Remove</button>
              </div>
            </div>
          ))}
        </div>
        <div className="row">
          <input className="search-input" style={{ flex: 1 }}
            placeholder='Add a directive… e.g. "never sound corporate"'
            value={newDirective}
            onChange={e => setNewDirective(e.target.value)}
            onKeyDown={e => { if (e.key === 'Enter') { e.preventDefault(); void addDir() } }} />
          <button className="btn-primary" disabled={busy || !newDirective.trim()} onClick={() => void addDir()}>Add</button>
        </div>
      </div>

      {/* ── evolved traits ── */}
      <div className="card" style={{ marginBottom: '1rem' }}>
        <h3 style={{ marginTop: 0 }}>Evolved traits — how the friendship shaped them</h3>
        <p className="hint" style={{ marginTop: 0 }}>
          Rewritten by a background pass from your conversations. You can edit this directly —
          a human edit becomes the new baseline, and the next evolution refines it rather than
          overwriting it.
        </p>

        {!current || current.content === '' ? (
          <div className="card empty" style={{ marginBottom: '1rem' }}>
            Nothing yet. After you and {botRow.name} have talked a while, the first evolution
            pass will describe how the friendship has settled in.
          </div>
        ) : editingTraits ? (
          <div className="memory-edit" style={{ marginBottom: '1rem' }}>
            <textarea value={traitsText} onChange={e => setTraitsText(e.target.value)} rows={8} autoFocus />
            <div className="row">
              <button className="btn-primary btn-sm" disabled={busy} onClick={() => void saveTraits()}>
                {busy ? 'Saving…' : 'Save (new version)'}
              </button>
              <button className="btn-ghost btn-sm" disabled={busy} onClick={() => setEditingTraits(false)}>Cancel</button>
            </div>
          </div>
        ) : (
          <p className="memory-content" style={{ marginBottom: '1rem' }}>{current.content}</p>
        )}
        {current && current.content !== '' && !editingTraits && (
          <div className="row memory-actions" style={{ marginBottom: '1.25rem' }}>
            <button className="btn-ghost btn-sm" disabled={busy} onClick={startEditTraits}>Edit</button>
          </div>
        )}

        {versions.length > 0 && (
          <>
            <h3 style={{ margin: '0 0 0.5rem' }}>Version history</h3>
            <div className="list">
              {versions.map((v, i) => (
                <div key={v.id} className={`card memory-card${i === 0 ? '' : ' faded'}`} style={{ marginBottom: '0.5rem' }}>
                  <div className="memory-head">
                    <span className={SOURCE_LABEL[v.source]?.cls ?? 'pill'}>{SOURCE_LABEL[v.source]?.text ?? v.source}</span>
                    {i === 0 && <span className="pill accent">current</span>}
                    <span className="hint" style={{ marginLeft: 'auto' }}>
                      {new Date(v.createdAt).toLocaleString()}
                      {v.content ? ` · ${v.content.split(/\s+/).length} words` : ' · empty'}
                    </span>
                  </div>
                  {viewingVersion === v.id ? (
                    <>
                      <p className="memory-content">{v.content || <em className="hint">empty baseline</em>}</p>
                      <div className="row memory-actions">
                        <button className="btn-ghost btn-sm" onClick={() => setViewingVersion(null)}>Close</button>
                        {i !== 0 && (
                          <button className="btn-ghost btn-sm" disabled={busy} onClick={() => void doRevert(v)}>
                            Revert to this
                          </button>
                        )}
                      </div>
                    </>
                  ) : (
                    <>
                      <p className="memory-content">{v.content
                        ? (v.content.length > 160 ? v.content.slice(0, 160) + '…' : v.content)
                        : <em className="hint">empty baseline</em>}</p>
                      <div className="row memory-actions">
                        <button className="btn-ghost btn-sm" onClick={() => setViewingVersion(v.id)}>View</button>
                        {i !== 0 && (
                          <button className="btn-ghost btn-sm danger-text" disabled={busy} onClick={() => void doRevert(v)}>
                            Revert to this
                          </button>
                        )}
                      </div>
                    </>
                  )}
                </div>
              ))}
            </div>
          </>
        )}
      </div>
    </div>
  )
}
