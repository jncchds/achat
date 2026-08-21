import { useCallback, useEffect, useState } from 'react'
import { apiError, listUsers, type User } from '../api'

export default function Admin() {
  const [users, setUsers] = useState<User[]>([])
  const [error, setError] = useState('')

  const load = useCallback(() => {
    listUsers().then(setUsers).catch(e => setError(apiError(e)))
  }, [])
  useEffect(() => { load() }, [load])

  return (
    <div>
      <div className="page-header">
        <h2>Admin</h2>
      </div>
      {error && <p className="error-msg" style={{ marginBottom: '1rem' }}>{error}</p>}
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
        User management (create / disable / reset) lands in Phase 8. The bootstrap admin is created from <span className="mono">BootstrapAdmin</span> settings on first run (§7.2).
      </p>
    </div>
  )
}
