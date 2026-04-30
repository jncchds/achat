import { useState, type FormEvent } from 'react';
import { authApi } from '../api/auth';
import { useAuth } from '../contexts/AuthContext';

export function ProfilePage() {
  const { token } = useAuth();
  const [telegramId, setTelegramId] = useState('');
  const [success, setSuccess] = useState('');
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(false);

  const handleSubmit = async (e: FormEvent) => {
    e.preventDefault();
    setError('');
    setSuccess('');
    const id = parseInt(telegramId);
    if (!id) { setError('Invalid Telegram ID'); return; }
    setLoading(true);
    try {
      await authApi.setTelegram(id, token!);
      setSuccess('Telegram ID linked successfully.');
      setTelegramId('');
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed');
    } finally {
      setLoading(false);
    }
  };

  return (
    <div className="page">
      <div className="page-header">
        <h1>Profile</h1>
      </div>

      <div className="card form-card">
        <h3>Link Telegram Account</h3>
        <p className="muted" style={{ marginTop: 4 }}>
          Enter your Telegram user ID to enable Telegram bot access under your account.
        </p>
        {success && <div className="alert alert-success" style={{ marginTop: 14 }}>{success}</div>}
        {error && <div className="alert alert-error" style={{ marginTop: 14 }}>{error}</div>}
        <form onSubmit={handleSubmit} className="form" style={{ marginTop: 16 }}>
          <div className="form-group">
            <label>Telegram User ID</label>
            <input
              type="number"
              value={telegramId}
              onChange={e => setTelegramId(e.target.value)}
              placeholder="e.g. 123456789"
              required
              autoFocus
            />
          </div>
          <div className="form-actions">
            <button type="submit" className="btn btn-primary" disabled={loading}>
              {loading ? 'Linking…' : 'Link Telegram'}
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}
