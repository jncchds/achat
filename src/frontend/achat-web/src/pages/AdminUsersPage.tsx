import { useState, type FormEvent } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { adminApi } from '../api/admin';
import { useAuth } from '../contexts/AuthContext';

export function AdminUsersPage() {
  const { token } = useAuth();
  const qc = useQueryClient();

  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [formError, setFormError] = useState('');

  const { data: users, isLoading, error } = useQuery({
    queryKey: ['admin-users'],
    queryFn: () => adminApi.listUsers(token!),
  });

  const createMut = useMutation({
    mutationFn: () => adminApi.createUser({ email, password }, token!),
    onSuccess: () => {
      setEmail('');
      setPassword('');
      setFormError('');
      qc.invalidateQueries({ queryKey: ['admin-users'] });
    },
    onError: (err) => {
      setFormError(err instanceof Error ? err.message : 'Failed to create user');
    },
  });

  const deleteMut = useMutation({
    mutationFn: (id: string) => adminApi.deleteUser(id, token!),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['admin-users'] }),
  });

  const handleCreate = async (e: FormEvent) => {
    e.preventDefault();
    setFormError('');
    createMut.mutate();
  };

  return (
    <div className="page">
      <div className="page-header">
        <h1>User Management</h1>
      </div>

      <div className="card form-card">
        <h3>Create User</h3>
        {formError && <div className="alert alert-error" style={{ marginTop: 12 }}>{formError}</div>}
        <form onSubmit={handleCreate} className="form" style={{ marginTop: 16 }}>
          <div className="form-group">
            <label>Email</label>
            <input
              type="email"
              value={email}
              onChange={e => setEmail(e.target.value)}
              required
            />
          </div>
          <div className="form-group">
            <label>Password</label>
            <input
              type="password"
              value={password}
              onChange={e => setPassword(e.target.value)}
              required
              minLength={8}
            />
          </div>
          <div className="form-actions">
            <button type="submit" className="btn btn-primary" disabled={createMut.isPending}>
              {createMut.isPending ? 'Creating…' : 'Create User'}
            </button>
          </div>
        </form>
      </div>

      <h2 className="section-title" style={{ marginTop: 32 }}>Users</h2>

      {isLoading && <p className="muted">Loading…</p>}
      {error && <div className="alert alert-error">{String(error)}</div>}
      {users?.length === 0 && <div className="empty-state"><p>No users yet.</p></div>}

      {users && users.length > 0 && (
        <div className="table-wrap">
          <table className="table">
            <thead>
              <tr><th>Email</th><th>Role</th><th>Created</th><th></th></tr>
            </thead>
            <tbody>
              {users.map(u => (
                <tr key={u.id}>
                  <td>{u.email}</td>
                  <td>{u.isAdmin ? <span className="badge">Admin</span> : 'User'}</td>
                  <td>{new Date(u.createdAt).toLocaleString()}</td>
                  <td>
                    <button
                      className="btn btn-danger btn-sm"
                      disabled={u.isAdmin || deleteMut.isPending}
                      title={u.isAdmin ? 'Cannot delete admin' : undefined}
                      onClick={() => {
                        if (confirm(`Delete user "${u.email}"?`)) deleteMut.mutate(u.id);
                      }}
                    >
                      Delete
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
