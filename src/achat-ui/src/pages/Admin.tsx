import { useCallback, useEffect, useState } from 'react'
import {
  apiError, createUser, deleteUser, listUsers, resetUserPassword, setUserActive,
  listWork, retryWork, discardWork, getStoredUser, type User, type WorkItem,
} from '../api'

export default function Admin() {
  const [users, setUsers] = useState<User[]>([])
  const [error, setError] = useState('')
  const [msg, setMsg] = useState('')
  const [busy, setBusy] = useState(false)
  const [nu, setNu] = useState({ username: '', displayName: '', password: '' })
  const [work, setWork] = useState<WorkItem[]>([])
  const selfId = getStoredUser()?.id ?? null

  const load = useCallback(() => {
    listUsers().then(setUsers).catch(e => setError(apiError(e)))
    listWork().then(setWork).catch(() => { /* work list is best-effort on this page */ })
  }, [])
  useEffect(() => { load() }, [load])

  const flash = (m: string) => { setMsg(m); setError(''); setTimeout(() => setMsg(''), 6000) }

  const addUser = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!nu.username.trim() || !nu.password.trim()) return
    setBusy(true); setError(''); setMsg('')
    try {
      await createUser({
        username: nu.username.trim(),
        displayName: nu.displayName.trim() || null,
        password: nu.password,
      })
      setNu({ username: '', displayName: '', password: '' })
      flash('User created. They can now sign in and request to join a bot.')
      load()
    } catch (err) { setError(apiError(err)) } finally { setBusy(false) }
  }

  const toggleActive = async (u: User) => {
    const next = !u.isActive
    if (next) {
      // enabling is safe
    } else {
      if (!window.confirm(`Disable ${u.username}? They will be signed out of new work and cannot log in until re-enabled. Their bots and data are kept.`))
        return
    }
    setError('')
    try {
      await setUserActive(u.id, next)
      flash(next ? `${u.username} re-enabled.` : `${u.username} disabled — they can no longer sign in.`)
      load()
    } catch (err) { setError(apiError(err)) }
  }

  const resetPassword = async (u: User) => {
    const pw = window.prompt(`Set a new password for ${u.username} (min 4 characters):`, '')
    if (pw === null) return
    if (pw.length < 4) { setError('Password must be at least 4 characters.'); return }
    setError('')
    try {
      await resetUserPassword(u.id, pw)
      flash(`Password reset for ${u.username}. Share it securely.`)
    } catch (err) { setError(apiError(err)) }
  }

  const remove = async (u: User) => {
    if (!window.confirm(
      `Delete ${u.username}? This cannot be undone. Users who own bots or have logged interactions cannot be deleted — disable them instead.`,
    )) return
    setError('')
    try {
      await deleteUser(u.id)
      flash(`${u.username} deleted.`)
      load()
    } catch (err) { setError(apiError(err)) }
  }

  return (
    <div>
      <div className="page-header">
        <h2>Admin</h2>
      </div>
      {error && <p className="error-msg" role="alert" style={{ marginBottom: '1rem' }}>{error}</p>}
      {msg && <p className="hint" role="status" style={{ marginBottom: '1rem' }}>{msg}</p>}

      <h3 style={{ marginBottom: '0.75rem' }}>Create a user</h3>
      <p className="hint" style={{ marginTop: 0 }}>
        There is no self-service sign-up (§7.2) — an admin creates each user. Share their
        invite code for a bot from that bot's personality page.
      </p>
      <form onSubmit={addUser} className="row" style={{ alignItems: 'flex-end', marginBottom: '1rem' }}>
        <label style={{ flex: '1 1 12rem' }}>Username
          <input value={nu.username} onChange={e => setNu({ ...nu, username: e.target.value })} required />
        </label>
        <label style={{ flex: '1 1 12rem' }}>Display name <span className="hint">(optional)</span>
          <input value={nu.displayName} onChange={e => setNu({ ...nu, displayName: e.target.value })} />
        </label>
        <label style={{ flex: '1 1 12rem' }}>Password <span className="hint">(min 4)</span>
          <input type="password" value={nu.password} onChange={e => setNu({ ...nu, password: e.target.value })} required minLength={4} />
        </label>
        <button className="btn-primary" disabled={busy || !nu.username.trim() || !nu.password.trim()}>
          {busy ? 'Creating…' : 'Create user'}
        </button>
      </form>

      <h3 style={{ marginBottom: '0.75rem' }}>Users</h3>
      <div className="table-wrap">
        <table className="data">
          <thead>
            <tr>
              <th>Username</th>
              <th>Display name</th>
              <th>Role</th>
              <th>Status</th>
              <th>Timezone</th>
              <th>Last login</th>
              <th aria-label="Actions" />
            </tr>
          </thead>
          <tbody>
            {users.map(u => {
              const isSelf = u.id === selfId
              return (
                <tr key={u.id} aria-disabled={!u.isActive}>
                  <td className="mono">{u.username}{isSelf && <span className="hint"> (you)</span>}</td>
                  <td>{u.displayName}</td>
                  <td>{u.isAdmin ? <span className="pill">admin</span> : <span className="pill neutral">user</span>}</td>
                  <td>
                    {u.isActive
                      ? <span className="pill neutral">active</span>
                      : <span className="pill danger">disabled</span>}
                  </td>
                  <td>{u.timezone ?? '—'}</td>
                  <td>{u.lastLoginAt ? new Date(u.lastLoginAt).toLocaleString() : 'never'}</td>
                  <td>
                    <div className="row" style={{ justifyContent: 'flex-end', gap: '0.25rem' }}>
                      <button
                        className="btn-ghost"
                        disabled={isSelf && !u.isActive}
                        onClick={() => toggleActive(u)}
                        title={isSelf && !u.isActive ? 'You cannot re-enable yourself from here' : (u.isActive ? 'Disable' : 'Enable')}
                      >
                        {u.isActive ? 'Disable' : 'Enable'}
                      </button>
                      <button className="btn-ghost" onClick={() => resetPassword(u)} title={`Reset password`}>
                        Reset
                      </button>
                      {!isSelf && (
                        <button className="btn-danger" onClick={() => remove(u)} title="Delete user">
                          Delete
                        </button>
                      )}
                    </div>
                  </td>
                </tr>
              )
            })}
          </tbody>
        </table>
      </div>
      <p className="hint" style={{ marginTop: '1rem' }}>
        Disabling is the safe, reversible control — the account is refused at login and on every
        request, but their bots and data are kept (§7.4). Deleting a user who owns bots or has
        logged interactions is blocked; disable them instead. The bootstrap admin is created from
        <span className="mono"> BootstrapAdmin</span> settings on first run (§7.2).
      </p>

      <h3 style={{ marginTop: '2rem', marginBottom: '0.75rem' }}>Background work</h3>
      <p className="hint" style={{ marginTop: 0 }}>
        Memory extraction, personality evolution, and summarisation run off the request path as
        durable background work (§12b). A failed pass retries with backoff; after the attempt cap
        it dead-letters here instead of hammering the provider. Retry re-arms an item, discard
        removes it (the worker may re-arm it if its trigger still holds).
      </p>
      {work.length === 0
        ? <p className="hint">No background work in flight or waiting — the queue is clear.</p>
        : (
          <div className="table-wrap">
            <table className="data">
              <thead>
                <tr>
                  <th>Kind</th>
                  <th>Bot</th>
                  <th>Status</th>
                  <th>Attempts</th>
                  <th>Next try</th>
                  <th>Last failure</th>
                  <th aria-label="Actions" />
                </tr>
              </thead>
              <tbody>
                {work.map(w => (
                  <tr key={w.id}>
                    <td className="mono">{w.kind}</td>
                    <td>{w.botName}</td>
                    <td>
                      {w.status === 'Dead' && <span className="pill danger">dead</span>}
                      {w.status === 'Running' && <span className="pill">running</span>}
                      {w.status === 'Pending' && <span className="pill neutral">pending</span>}
                    </td>
                    <td>{w.attempts}/{w.maxAttempts}</td>
                    <td>{new Date(w.nextTryAt).toLocaleString()}</td>
                    <td className="mono" style={{ maxWidth: '26rem', overflowWrap: 'anywhere' }}>
                      {w.lastFailure ?? '—'}
                    </td>
                    <td>
                      <div className="row" style={{ justifyContent: 'flex-end', gap: '0.25rem' }}>
                        <button
                          className="btn-ghost"
                          disabled={w.status === 'Running'}
                          onClick={async () => {
                            try { await retryWork(w.id); load() }
                            catch (err) { setError(apiError(err)) }
                          }}
                          title="Reset attempts and run as soon as possible"
                        >
                          Retry
                        </button>
                        <button
                          className="btn-danger"
                          disabled={w.status === 'Running'}
                          onClick={async () => {
                            if (!window.confirm('Discard this work item? The worker may re-arm it if its trigger still holds.')) return
                            try { await discardWork(w.id); load() }
                            catch (err) { setError(apiError(err)) }
                          }}
                          title="Remove from the queue"
                        >
                          Discard
                        </button>
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
    </div>
  )
}
