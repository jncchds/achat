import { useParams, useNavigate } from 'react-router-dom';
import {
  Box, Button, Typography, Paper, Chip, Divider, Alert, Accordion,
  AccordionSummary, AccordionDetails,
} from '@mui/material';
import ExpandMoreIcon from '@mui/icons-material/ExpandMore';
import { useQuery } from '@tanstack/react-query';
import { botsApi } from '../api/bots';

export default function BotEvolutionPage() {
  const { botId } = useParams<{ botId: string }>();
  const navigate = useNavigate();

  const { data: bot } = useQuery({ queryKey: ['bot', botId], queryFn: () => botsApi.get(botId!) });
  const { data: history = [], isLoading } = useQuery({
    queryKey: ['bot-evolution', botId],
    queryFn: () => botsApi.getEvolutionHistory(botId!),
  });

  return (
    <Box sx={{ p: 3 }}>
      <Button onClick={() => navigate(-1)} sx={{ mb: 2 }}>← Back</Button>
      <Typography variant="h5" sx={{ fontWeight: 700, mb: 1 }}>
        Evolution History{bot ? `: ${bot.name}` : ''}
      </Typography>
      <Typography variant="body2" color="text.secondary" sx={{ mb: 3 }}>
        Each entry shows what the personality was, why it changed, and what it became.
      </Typography>

      {isLoading && <Typography color="text.secondary">Loading…</Typography>}

      {!isLoading && history.length === 0 && (
        <Alert severity="info">No evolution history yet. Trigger an evolution from the bot settings page.</Alert>
      )}

      {history.map((entry, i) => (
        <Paper key={entry.id} sx={{ mb: 2, overflow: 'hidden' }}>
          <Box sx={{ px: 2, py: 1.5, display: 'flex', alignItems: 'center', gap: 1, bgcolor: 'grey.50' }}>
            <Typography variant="subtitle2" sx={{ flexGrow: 1 }}>
              {new Date(entry.evolvedAt).toLocaleString()}
            </Typography>
            {entry.direction && (
              <Chip label={`Direction: ${entry.direction}`} size="small" color="primary" variant="outlined" />
            )}
            <Chip label={`#${history.length - i}`} size="small" variant="outlined" />
          </Box>

          <Box sx={{ px: 2, py: 1.5 }}>
            <Typography variant="body2" sx={{ fontWeight: 600, mb: 0.5 }}>Reasoning</Typography>
            <Typography variant="body2" color="text.secondary" sx={{ whiteSpace: 'pre-wrap' }}>
              {entry.reasoning || '(no reasoning captured)'}
            </Typography>
          </Box>

          <Divider />

          <Accordion disableGutters elevation={0} sx={{ '&:before': { display: 'none' } }}>
            <AccordionSummary expandIcon={<ExpandMoreIcon />} sx={{ px: 2, py: 0.5 }}>
              <Typography variant="body2" sx={{ fontWeight: 600 }}>Previous personality</Typography>
            </AccordionSummary>
            <AccordionDetails sx={{ px: 2, pt: 0, pb: 2 }}>
              <Typography variant="body2" color="text.secondary" sx={{ whiteSpace: 'pre-wrap', fontFamily: 'monospace', fontSize: '0.8rem' }}>
                {entry.oldPersonality}
              </Typography>
            </AccordionDetails>
          </Accordion>

          <Divider />

          <Accordion disableGutters elevation={0} sx={{ '&:before': { display: 'none' } }}>
            <AccordionSummary expandIcon={<ExpandMoreIcon />} sx={{ px: 2, py: 0.5 }}>
              <Typography variant="body2" sx={{ fontWeight: 600 }}>New personality</Typography>
            </AccordionSummary>
            <AccordionDetails sx={{ px: 2, pt: 0, pb: 2 }}>
              <Typography variant="body2" sx={{ whiteSpace: 'pre-wrap', fontFamily: 'monospace', fontSize: '0.8rem' }}>
                {entry.newPersonality}
              </Typography>
            </AccordionDetails>
          </Accordion>
        </Paper>
      ))}
    </Box>
  );
}
