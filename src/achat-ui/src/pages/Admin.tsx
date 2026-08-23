import { useCallback, useEffect, useState } from 'react'
import { apiError, createUser, listUsers, type User } from '../api'

export default function Admin() {
  const [users, setUsers] = useState<User[]>([])
  const [error, setError] = useState('')
  const [msg, setMsg] = useState('')
  const [busy, setBusy] = useState(false)
  const [nu, setNu] = useState({ username: '', displayName: '', password: '' })

  const load = useCallback(() => {
    listUsers().then(setUsers).catch(e => setError(apiError(e)))
  }, [])
  useEffect(() => { load() }, [load])

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
      setMsg('User created. They can now sign in and request to join a bot.')
      load()
    } catch (err) { setError(apiError(err)) } finally { setBusy(false) }
  }

  return (
    <div>
      <div className="page-header">
        <h2>Admin</h2>
      </div>
      {error && <p className="error-msg" style={{ marginBottom: '1rem' }}>{error}</p>}
      <h3 style={{ marginBottom: '0.75rem' }}>Create a user</h3>
      <p className="hint" style={{ marginTop: 0 }}>
        There is no self-service sign-up (§7.2) — an admin creates each user. Share their
        invite code for a bot from that bot's personality page.
      </p>
      <form onSubmit={addUser} className="row" style={{ alignItems: 'flex-end', marginBottom: '0.5rem' }}>
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
      {msg && <p className="hint" style={{ marginBottom: '1rem' }}>{msg}</p>}

      <h3 style={{ marginBottom: '0.75rem' }}>Users</h3>
      <div className="table-wrap">
        <table className="data">
          <thead>
            <tr>
              <th>Username</th>
              <th>Display name</th>
              <th>Role</th>
              <th>Timezone</th>
              <th>Created</th>
              <th>Last login</th>
            </tr>
          </thead>
          <tbody>
            {users.map(u => (
              <tr key={u.id}>
                <td className="mono">{u.username}</td>
                <td>{u.displayName}</td>
                <td>{u.isAdmin ? <span className="pill">admin</span> : <span className="pill neutral">user</span>}</td>
                <td>{u.timezone ?? '—'}</td>
                <td>{new Date(u.createdAt).toLocaleDateString()}</td>
                <td>{u.lastLoginAt ? new Date(u.lastLoginAt).toLocaleString() : 'never'}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <p className="hint" style={{ marginTop: '1rem' }}>
        User management (disable / reset / delete) lands in Phase 8. The bootstrap admin is created from <span className="mono">BootstrapAdmin</span> settings on first run (§7.2).
      </p>
    </div>
  )
}
