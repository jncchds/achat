import { useState } from 'react';
import { Link } from 'react-router-dom';
import {
  Box, Button, Typography, Card, CardContent, CardActions,
  Dialog, DialogTitle, DialogContent, DialogActions, TextField,
  MenuItem, Select, InputLabel, FormControl, IconButton, Chip, Autocomplete,
} from '@mui/material';
import DeleteIcon from '@mui/icons-material/Delete';
import SettingsIcon from '@mui/icons-material/Settings';
import PersonIcon from '@mui/icons-material/Person';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { botsApi, type CreateBotRequest } from '../api/bots';
import { presetsApi } from '../api/presets';
import { useAuth } from '../store/AuthContext';

const GENDER_OPTIONS = ['He/Him', 'She/Her', 'They/Them', 'It/Its', 'Any pronouns', 'He/They', 'She/They', 'Xe/Xem', 'Ze/Zir'];
const LANGUAGE_OPTIONS = ['English', 'Spanish', 'French', 'German', 'Italian', 'Portuguese', 'Russian', 'Chinese', 'Japanese', 'Korean', 'Arabic', 'Hindi', 'Polish', 'Dutch', 'Swedish'];

const BOT_TRAITS = [
  'Friendly', 'Professional', 'Witty', 'Empathetic', 'Concise',
  'Creative', 'Curious', 'Analytical', 'Enthusiastic', 'Patient',
  'Direct', 'Playful', 'Thoughtful', 'Supportive', 'Formal',
  'Casual', 'Encouraging', 'Detail-oriented', 'Humorous', 'Calm',
  'Assertive', 'Diplomatic', 'Insightful', 'Motivational',
];

function buildPersonality(traits: string[]): string {
  if (traits.length === 0) return 'You are a helpful assistant.';
  const items = traits.map(t => t.toLowerCase());
  const last = items.pop()!;
  const traitStr = items.length > 0 ? `${items.join(', ')} and ${last}` : last;
  return `You are a helpful assistant. You are ${traitStr}.`;
}

export default function BotsPage() {
  const { user } = useAuth();
  const qc = useQueryClient();
  const { data: bots = [], isLoading } = useQuery({ queryKey: ['bots'], queryFn: botsApi.getAll });
  const { data: presets = [] } = useQuery({ queryKey: ['presets'], queryFn: presetsApi.getAll });
  const [open, setOpen] = useState(false);
  const [form, setForm] = useState<CreateBotRequest>({ name: '', presetId: '', personality: 'You are a helpful assistant.' });
  const [selectedTraits, setSelectedTraits] = useState<string[]>([]);

  function toggleTrait(trait: string) {
    setSelectedTraits(prev => {
      const next = prev.includes(trait)
        ? prev.filter(t => t !== trait)
        : prev.length < 5 ? [...prev, trait] : prev;
      setForm(f => ({ ...f, personality: buildPersonality(next) }));
      return next;
    });
  }

  const createMut = useMutation({ mutationFn: (d: CreateBotRequest) => botsApi.create(d), onSuccess: () => { qc.invalidateQueries({ queryKey: ['bots'] }); setOpen(false); } });
  const deleteMut = useMutation({ mutationFn: botsApi.delete, onSuccess: () => qc.invalidateQueries({ queryKey: ['bots'] }) });
  const requestAccessMut = useMutation({ mutationFn: (id: string) => botsApi.requestAccess(id), onSuccess: () => qc.invalidateQueries({ queryKey: ['bots'] }) });

  return (
    <Box sx={{ p: 3 }}>
      <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', mb: 2 }}>
        <Typography variant="h5" sx={{ fontWeight: 700 }}>Bots</Typography>
        <Button variant="contained" onClick={() => { setSelectedTraits([]); setForm({ name: '', presetId: presets[0]?.id ?? '', personality: 'You are a helpful assistant.' }); setOpen(true); }}>
          Create Bot
        </Button>
      </Box>

      {isLoading && <Typography>Loading…</Typography>}
      <Box sx={{ display: 'flex', flexWrap: 'wrap', gap: 2 }}>
        {bots.map(bot => (
          <Card key={bot.id} sx={{ width: 320 }}>
            <CardContent>
              <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, mb: 0.5 }}>
                <Typography variant="h6">{bot.name}</Typography>
                {bot.ownerId === user?.id && <Chip label="Owner" size="small" color="primary" />}
                {bot.hasTelegramToken && <Chip label="Telegram" size="small" color="success" />}
                {bot.gender && <Chip label={bot.gender} size="small" variant="outlined" />}
              </Box>
              <Typography variant="body2" color="text.secondary" noWrap>{bot.presetName}{bot.language ? ` · ${bot.language}` : ''}</Typography>
              <Typography variant="body2" sx={{ mt: 1, display: '-webkit-box', WebkitLineClamp: 2, WebkitBoxOrient: 'vertical', overflow: 'hidden' }}>
                {bot.personality}
              </Typography>
            </CardContent>
            <CardActions>
              <Button size="small" component={Link} to={`/bots/${bot.id}/chat`}>Chat</Button>
              {bot.ownerId === user?.id && <>
                <IconButton size="small" component={Link} to={`/bots/${bot.id}/settings`}><SettingsIcon /></IconButton>
                <IconButton size="small" component={Link} to={`/bots/${bot.id}/access`}><PersonIcon /></IconButton>
                <IconButton size="small" onClick={() => deleteMut.mutate(bot.id)} color="error"><DeleteIcon /></IconButton>
              </>}
              {bot.ownerId !== user?.id && (
                <Button size="small" onClick={() => requestAccessMut.mutate(bot.id)}>Request Access</Button>
              )}
            </CardActions>
          </Card>
        ))}
      </Box>

      <Dialog open={open} onClose={() => setOpen(false)} fullWidth maxWidth="sm">
        <DialogTitle>Create Bot</DialogTitle>
        <DialogContent sx={{ display: 'flex', flexDirection: 'column', gap: 2, pt: 2 }}>
          <TextField label="Bot Name" value={form.name} onChange={e => setForm({ ...form, name: e.target.value })} fullWidth required />
          <FormControl fullWidth required>
            <InputLabel>Preset</InputLabel>
            <Select value={form.presetId} label="Preset" onChange={e => setForm({ ...form, presetId: e.target.value })}>
              {presets.map(p => <MenuItem key={p.id} value={p.id}>{p.name}</MenuItem>)}
            </Select>
          </FormControl>
          <Box>
            <Typography variant="body2" color="text.secondary" gutterBottom>
              Personality traits — select up to 5 ({selectedTraits.length}/5)
            </Typography>
            <Box sx={{ display: 'flex', flexWrap: 'wrap', gap: 0.5, mb: 1.5 }}>
              {BOT_TRAITS.map(trait => (
                <Chip
                  key={trait}
                  label={trait}
                  size="small"
                  onClick={() => toggleTrait(trait)}
                  color={selectedTraits.includes(trait) ? 'primary' : 'default'}
                  variant={selectedTraits.includes(trait) ? 'filled' : 'outlined'}
                  disabled={!selectedTraits.includes(trait) && selectedTraits.length >= 5}
                />
              ))}
            </Box>
            <TextField
              label="Personality (system prompt)"
              value={form.personality}
              onChange={e => setForm({ ...form, personality: e.target.value })}
              fullWidth multiline minRows={3} required
              helperText="Auto-generated from traits above — you can edit freely"
            />
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
          </Box>
        </DialogContent>
        <DialogActions>
          <Button onClick={() => setOpen(false)}>Cancel</Button>
          <Button variant="contained" onClick={() => createMut.mutate(form)} disabled={createMut.isPending}>Create</Button>
        </DialogActions>
      </Dialog>
    </Box>
  );
}
