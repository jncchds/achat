import { useState } from 'react';
import {
  Box, Typography, Table, TableHead, TableRow, TableCell, TableBody,
  Paper, TableContainer, Pagination, Chip, Tooltip
} from '@mui/material';
import { useQuery } from '@tanstack/react-query';
import { usageApi } from '../api/usage';
import { useAuth } from '../store/AuthContext';

export default function LlmUsagePage() {
  const { isAdmin } = useAuth();
  const [page, setPage] = useState(1);
  const pageSize = 20;

  const { data, isLoading } = useQuery({
    queryKey: ['llm-usage', page, isAdmin],
    queryFn: () => isAdmin ? usageApi.getAllUsage(page, pageSize) : usageApi.getMyUsage(page, pageSize)
  });

  const totalPages = data ? Math.ceil(data.totalCount / pageSize) : 1;

  return (
    <Box sx={{ p: 3 }}>
      <Typography variant="h5" sx={{ fontWeight: 700, mb: 2 }}>LLM Usage</Typography>
      {isLoading && <Typography>Loading…</Typography>}
      <TableContainer component={Paper}>
        <Table size="small">
          <TableHead>
            <TableRow>
              {isAdmin && <TableCell>User</TableCell>}
              <TableCell>Bot</TableCell>
              <TableCell>Model</TableCell>
              <TableCell align="right">In Tokens</TableCell>
              <TableCell align="right">Out Tokens</TableCell>
              <TableCell>Time</TableCell>
            </TableRow>
          </TableHead>
          <TableBody>
            {data?.items.map(item => (
              <TableRow key={item.id}>
                {isAdmin && <TableCell>{item.username}</TableCell>}
                <TableCell>{item.botName ?? <Chip label="—" size="small" />}</TableCell>
                <TableCell>
                  <Tooltip title={item.endpoint}><span>{item.modelName}</span></Tooltip>
                </TableCell>
                <TableCell align="right">{item.inputTokens}</TableCell>
                <TableCell align="right">{item.outputTokens}</TableCell>
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
