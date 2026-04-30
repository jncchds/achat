import { useParams } from 'react-router-dom';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { accessApi } from '../api/access';
import { botsApi } from '../api/bots';
import { useAuth } from '../contexts/AuthContext';

export function AccessRequestsPage() {
  const { id: botId } = useParams<{ id: string }>();
  const { token } = useAuth();
  const qc = useQueryClient();

  const { data: bot } = useQuery({
    queryKey: ['bot', botId],
    queryFn: () => botsApi.get(botId!, token!),
    enabled: !!botId,
  });

  const { data: requests, isLoading } = useQuery({
    queryKey: ['access-requests', botId],
    queryFn: () => accessApi.listRequests(botId!, token!),
    enabled: !!botId,
    refetchInterval: 10_000,
  });

  const invalidate = () => qc.invalidateQueries({ queryKey: ['access-requests', botId] });
  const approveMut = useMutation({ mutationFn: (id: string) => accessApi.approve(botId!, id, token!), onSuccess: invalidate });
  const denyMut = useMutation({ mutationFn: (id: string) => accessApi.deny(botId!, id, token!), onSuccess: invalidate });

  return (
    <div className="page">
      <div className="page-header">
        <h1>Access Requests — {bot?.name ?? '…'}</h1>
      </div>

      {isLoading && <p className="muted">Loading…</p>}
      {requests?.length === 0 && <div className="empty-state"><p>No pending access requests.</p></div>}

      {requests && requests.length > 0 && (
        <div className="table-wrap">
          <table className="table">
            <thead>
              <tr><th>Name</th><th>Type</th><th>ID</th><th>Requested</th><th></th></tr>
            </thead>
            <tbody>
              {requests.map(r => (
                <tr key={r.id}>
                  <td>{r.displayName ?? <span className="muted">—</span>}</td>
                  <td>{r.subjectType}</td>
                  <td><code>{r.subjectId}</code></td>
                  <td>{new Date(r.requestedAt).toLocaleString()}</td>
                  <td>
                    <div className="td-actions">
                      <button className="btn btn-success btn-sm" onClick={() => approveMut.mutate(r.id)} disabled={approveMut.isPending}>Approve</button>
                      <button className="btn btn-danger btn-sm" onClick={() => denyMut.mutate(r.id)} disabled={denyMut.isPending}>Deny</button>
                    </div>
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
