import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { botsApi } from '../api/bots';
import { useAuth } from '../contexts/AuthContext';

export function BotsPage() {
  const { token } = useAuth();
  const qc = useQueryClient();

  const { data: bots, isLoading, error } = useQuery({
    queryKey: ['bots'],
    queryFn: () => botsApi.list(token!),
  });

  const deleteMut = useMutation({
    mutationFn: (id: string) => botsApi.delete(id, token!),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['bots'] }),
  });

  if (isLoading) return <div className="page"><p className="muted">Loading…</p></div>;
  if (error) return <div className="page"><div className="alert alert-error">{String(error)}</div></div>;

  return (
    <div className="page">
      <div className="page-header">
        <h1>My Bots</h1>
        <Link to="/bots/new" className="btn btn-primary">+ New Bot</Link>
      </div>

      {bots?.length === 0 && (
        <div className="empty-state"><p>No bots yet. Create your first bot!</p></div>
      )}

      <div className="card-grid">
        {bots?.map(bot => (
          <div key={bot.id} className="card">
            <div className="card-header">
              <h3>{bot.name}</h3>
              {bot.hasTelegramToken && <span className="badge badge-tg">TG</span>}
            </div>
            <p className="card-desc">{bot.characterDescription}</p>
            {(bot.age || bot.gender) && (
              <p className="card-meta">
                {bot.age && `Age: ${bot.age}`}{bot.age && bot.gender && ' · '}{bot.gender}
              </p>
            )}
            <div className="card-actions">
              <Link to={`/bots/${bot.id}/chat`} className="btn btn-primary btn-sm">Chat</Link>
              <Link to={`/bots/${bot.id}/settings`} className="btn btn-secondary btn-sm">Settings</Link>
              <button
                className="btn btn-danger btn-sm"
                onClick={() => { if (confirm(`Delete "${bot.name}"?`)) deleteMut.mutate(bot.id); }}
              >
                Delete
              </button>
            </div>
          </div>
        ))}
      </div>
    </div>
  );
}
