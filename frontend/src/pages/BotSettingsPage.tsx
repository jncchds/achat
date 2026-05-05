import { useState, useEffect } from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import {
  Box, Button, Typography, TextField, FormControl, InputLabel,
  Select, MenuItem, Alert, Paper, Autocomplete, IconButton, InputAdornment, Tooltip,
} from '@mui/material';
import ClearIcon from '@mui/icons-material/Clear';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { botsApi, type UpdateBotRequest } from '../api/bots';
import { presetsApi } from '../api/presets';

const GENDER_OPTIONS = ['He/Him', 'She/Her', 'They/Them', 'It/Its', 'Any pronouns', 'He/They', 'She/They', 'Xe/Xem', 'Ze/Zir'];
const LANGUAGE_OPTIONS = ['English', 'Spanish', 'French', 'German', 'Italian', 'Portuguese', 'Russian', 'Chinese', 'Japanese', 'Korean', 'Arabic', 'Hindi', 'Polish', 'Dutch', 'Swedish'];

export default function BotSettingsPage() {
  const { botId } = useParams<{ botId: string }>();
  const navigate = useNavigate();
  const qc = useQueryClient();

  const { data: bot } = useQuery({ queryKey: ['bot', botId], queryFn: () => botsApi.get(botId!) });
  const { data: presets = [] } = useQuery({ queryKey: ['presets'], queryFn: presetsApi.getAll });

  const [form, setForm] = useState<UpdateBotRequest>({});
  const [personality, setPersonality] = useState('');
  const [telegramToken, setTelegramToken] = useState('');
  const [nudgeDir, setNudgeDir] = useState('');
  const [newPersonality, setNewPersonality] = useState('');
  const [replaceOpen, setReplaceOpen] = useState(false);
  const [alert, setAlert] = useState('');

  useEffect(() => {
    if (!bot) return;
    setForm({ name: bot.name, presetId: bot.presetId, unknownUserReply: bot.unknownUserReply, gender: bot.gender ?? undefined, language: bot.language ?? undefined, evolutionIntervalHours: bot.evolutionIntervalHours ?? undefined });
    setPersonality(bot.personality);
  }, [bot]);

  const updateMut = useMutation({
    mutationFn: (d: UpdateBotRequest) => botsApi.update(botId!, d),
    onSuccess: () => { qc.invalidateQueries({ queryKey: ['bot', botId] }); setAlert('Saved!'); }
  });
  const replaceMut = useMutation({
    mutationFn: (p: string) => botsApi.replacePersonality(botId!, p),
    onSuccess: () => { qc.invalidateQueries({ queryKey: ['bot', botId] }); setReplaceOpen(false); setAlert('Personality replaced and conversations cleared.'); }
  });
  const nudgeMut = useMutation({
    mutationFn: () => botsApi.nudge(botId!, nudgeDir || undefined),
    onSuccess: () => setAlert('Evolution nudged!')
  });

  if (!bot) return <Typography sx={{ p: 3 }}>Loading…</Typography>;

  return (
    <Box sx={{ p: 3 }}>
      <Button onClick={() => navigate(-1)} sx={{ mb: 2 }}>← Back</Button>
      <Typography variant="h5" sx={{ fontWeight: 700, mb: 3 }}>Bot Settings: {bot.name}</Typography>

      {alert && <Alert severity="success" sx={{ mb: 2 }} onClose={() => setAlert('')}>{alert}</Alert>}

      <Paper sx={{ p: 3, mb: 3 }}>
        <Typography variant="h6" sx={{ mb: 2 }}>General</Typography>
        <Box sx={{ display: 'flex', flexDirection: 'column', gap: 2 }}>
          <TextField label="Name" value={form.name ?? ''} onChange={e => setForm({ ...form, name: e.target.value })} fullWidth />
          <FormControl fullWidth>
            <InputLabel>Preset</InputLabel>
            <Select value={form.presetId ?? ''} label="Preset" onChange={e => setForm({ ...form, presetId: e.target.value })}>
              {presets.map(p => <MenuItem key={p.id} value={p.id}>{p.name}</MenuItem>)}
            </Select>
          </FormControl>
          <TextField label="Unknown User Reply" value={form.unknownUserReply ?? ''} onChange={e => setForm({ ...form, unknownUserReply: e.target.value })} fullWidth />
          <Autocomplete
            freeSolo options={GENDER_OPTIONS}
            value={form.gender ?? ''} onInputChange={(_, v) => setForm({ ...form, gender: v || undefined })}
            renderInput={params => <TextField {...params} label="Pronouns / Gender (optional)" />}
          />
          <Autocomplete
            freeSolo options={LANGUAGE_OPTIONS}
            value={form.language ?? ''} onInputChange={(_, v) => setForm({ ...form, language: v || undefined })}
            renderInput={params => <TextField {...params} label="Language (optional)" />}
          />
          <TextField label="Evolution Interval (hours, blank=global)" type="number"
            value={form.evolutionIntervalHours ?? ''} onChange={e => setForm({ ...form, evolutionIntervalHours: e.target.value ? Number(e.target.value) : undefined })} fullWidth />
          <TextField label="Telegram Token (leave blank to keep)" value={telegramToken} onChange={e => setTelegramToken(e.target.value)} type="password" fullWidth
            slotProps={{
              input: {
                endAdornment: bot.hasTelegramToken && !telegramToken ? (
                  <InputAdornment position="end">
                    <Tooltip title="Clear token">
                      <IconButton size="small" onClick={() => updateMut.mutate({ telegramToken: '' })}>
                        <ClearIcon fontSize="small" />
                      </IconButton>
                    </Tooltip>
                  </InputAdornment>
                ) : undefined,
              },
            }}
          />
        </Box>
        <Button variant="contained" sx={{ mt: 2 }} onClick={() => updateMut.mutate({ ...form, telegramToken: telegramToken || undefined })} disabled={updateMut.isPending}>
          Save Changes
        </Button>
      </Paper>

      <Paper sx={{ p: 3, mb: 3 }}>
        <Typography variant="h6" sx={{ mb: 2 }}>Personality</Typography>
        <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>Current personality (read-only, managed by evolution). Replace only to fully reset.</Typography>
        <TextField value={personality} multiline minRows={4} fullWidth slotProps={{ input: { readOnly: true } }} />
        <Button color="warning" sx={{ mt: 1 }} onClick={() => { setNewPersonality(personality); setReplaceOpen(true); }}>
          Replace Personality & Clear Conversations
        </Button>
        {replaceOpen && (
          <Box sx={{ mt: 2 }}>
            <Alert severity="warning" sx={{ mb: 1 }}>This will delete all conversations for this bot.</Alert>
            <TextField label="New Personality" value={newPersonality} onChange={e => setNewPersonality(e.target.value)} fullWidth multiline minRows={4} />
            <Box sx={{ mt: 1, display: 'flex', gap: 1 }}>
              <Button variant="contained" color="warning" onClick={() => replaceMut.mutate(newPersonality)} disabled={replaceMut.isPending}>Confirm Replace</Button>
              <Button onClick={() => setReplaceOpen(false)}>Cancel</Button>
            </Box>
          </Box>
        )}
      </Paper>

      <Paper sx={{ p: 3 }}>
        <Typography variant="h6" sx={{ mb: 2 }}>Personality Evolution</Typography>
        <Typography variant="body2" color="text.secondary" sx={{ mb: 1 }}>Last evolved: {bot.lastEvolvedAt ? new Date(bot.lastEvolvedAt).toLocaleString() : 'Never'}</Typography>
        <TextField label="Nudge Direction (optional, e.g. 'be more friendly')" value={nudgeDir} onChange={e => setNudgeDir(e.target.value)} fullWidth sx={{ mb: 2 }} />
        <Box sx={{ display: 'flex', gap: 1 }}>
          <Button variant="outlined" onClick={() => nudgeMut.mutate()} disabled={nudgeMut.isPending}>Trigger Evolution Now</Button>
          <Button variant="text" onClick={() => navigate(`/bots/${botId}/evolution`)}>View Evolution History</Button>
        </Box>
      </Paper>
    </Box>
  );
}
