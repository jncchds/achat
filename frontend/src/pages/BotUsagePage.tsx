import { useState } from 'react';
import { useParams, Link } from 'react-router-dom';
import {
  Box, Typography, Table, TableHead, TableRow, TableCell, TableBody,
  Paper, TableContainer, Pagination, Link as MuiLink,
} from '@mui/material';
import { useQuery } from '@tanstack/react-query';
import { usageApi } from '../api/usage';

export default function BotUsagePage() {
  const { botId } = useParams<{ botId: string }>();
  const [page, setPage] = useState(1);
  const pageSize = 20;

  const { data, isLoading } = useQuery({
    queryKey: ['bot-usage', botId, page],
    queryFn: () => usageApi.getBotUsage(botId!, page, pageSize),
  });

  const totalPages = data ? Math.ceil(data.totalCount / pageSize) : 1;

  return (
    <Box sx={{ p: 3 }}>
      <Typography variant="h5" sx={{ fontWeight: 700, mb: 2 }}>Usage</Typography>
      {isLoading && <Typography>Loading…</Typography>}
      <TableContainer component={Paper}>
        <Table size="small">
          <TableHead>
            <TableRow>
              <TableCell>Model</TableCell>
              <TableCell align="right">In Tokens</TableCell>
              <TableCell align="right">Out Tokens</TableCell>
              <TableCell>Conversation</TableCell>
              <TableCell>Time</TableCell>
            </TableRow>
          </TableHead>
          <TableBody>
            {data?.items.map(item => (
              <TableRow key={item.id}>
                <TableCell>{item.modelName}</TableCell>
                <TableCell align="right">{item.inputTokens}</TableCell>
                <TableCell align="right">{item.outputTokens}</TableCell>
                <TableCell>
                  {item.conversationId
                    ? <MuiLink component={Link} to={`/bots/${botId}/chat/${item.conversationId}`}>Open</MuiLink>
                    : '—'}
                </TableCell>
                <TableCell>{new Date(item.createdAt).toLocaleString()}</TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </TableContainer>
      {totalPages > 1 && (
        <Box sx={{ display: 'flex', justifyContent: 'center', mt: 2 }}>
          <Pagination count={totalPages} page={page} onChange={(_, p) => setPage(p)} />
        </Box>
      )}
    </Box>
  );
}
