import { useParams, useNavigate } from 'react-router-dom';
import {
  Box, Button, Typography, Paper, Chip, Alert, CircularProgress
} from '@mui/material';
import CheckIcon from '@mui/icons-material/Check';
import CloseIcon from '@mui/icons-material/Close';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { botsApi } from '../api/bots';

export default function BotAccessPage() {
  const { botId } = useParams<{ botId: string }>();
  const navigate = useNavigate();
  const qc = useQueryClient();

  const { data: requests = [], isLoading } = useQuery({
    queryKey: ['access-requests', botId],
    queryFn: () => botsApi.getAccessRequests(botId!)
  });


  const respondMut = useMutation({
    mutationFn: ({ requestId, approve }: { requestId: string; approve: boolean }) =>
      botsApi.respondToAccessRequest(botId!, requestId, approve),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['access-requests', botId] });
    }
  });

  const statusColor = (status: string) => {
    if (status === 'Approved') return 'success';
    if (status === 'Rejected') return 'error';
    return 'default';
  };

  return (
    <Box sx={{ p: 3 }}>
      <Button onClick={() => navigate(-1)} sx={{ mb: 2 }}>← Back</Button>
      <Typography variant="h5" sx={{ fontWeight: 700, mb: 3 }}>Access Requests</Typography>

      {isLoading && <CircularProgress />}
      {requests.length === 0 && !isLoading && <Alert severity="info">No access requests yet.</Alert>}

      {requests.map(r => (
        <Paper key={r.id} sx={{ p: 2, mb: 2, display: 'flex', alignItems: 'center', gap: 2 }}>
          <Box sx={{ flex: 1 }}>
            <Typography sx={{ fontWeight: 600 }}>{r.requesterUsername}</Typography>
            <Typography variant="caption" color="text.secondary">{new Date(r.createdAt).toLocaleString()}</Typography>
          </Box>
          <Chip label={r.status} color={statusColor(r.status) as any} size="small" />
          {r.status === 'Pending' && <>
            <Button size="small" color="success" startIcon={<CheckIcon />} onClick={() => respondMut.mutate({ requestId: r.id, approve: true })}>Approve</Button>
            <Button size="small" color="error" startIcon={<CloseIcon />} onClick={() => respondMut.mutate({ requestId: r.id, approve: false })}>Reject</Button>
          </>}
        </Paper>
      ))}
    </Box>
  );
}
