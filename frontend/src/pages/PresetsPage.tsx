import { useState } from 'react';
import {
  Box, Button, Typography, Card, CardContent, CardActions,
  Dialog, DialogTitle, DialogContent, DialogActions, TextField, MenuItem,
  IconButton, Alert, Tooltip, Select, InputLabel, FormControl, Autocomplete
} from '@mui/material';
import DeleteIcon from '@mui/icons-material/Delete';
import EditIcon from '@mui/icons-material/Edit';
import RefreshIcon from '@mui/icons-material/Refresh';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { presetsApi, type PresetDto, type CreatePresetRequest, type ProviderType, type GetModelsInlineRequest } from '../api/presets';

const PROVIDER_TYPES: ProviderType[] = ['Ollama', 'OpenAI', 'GoogleAI'];

interface FormState {
  name: string; providerType: ProviderType; providerUrl: string;
  apiToken: string; generationModel: string; embeddingModel: string;
  timeoutSeconds: string;
}

const emptyForm = (): FormState => ({
  name: '', providerType: 'Ollama', providerUrl: 'http://localhost:11434',
  apiToken: '', generationModel: '', embeddingModel: '', timeoutSeconds: ''
});

export default function PresetsPage() {
  const qc = useQueryClient();
  const { data: presets = [], isLoading } = useQuery({ queryKey: ['presets'], queryFn: presetsApi.getAll });
  const [open, setOpen] = useState(false);
  const [editing, setEditing] = useState<PresetDto | null>(null);
  const [form, setForm] = useState<FormState>(emptyForm());
  const [models, setModels] = useState<string[]>([]);
  const [loadingModels, setLoadingModels] = useState(false);
  const [modelError, setModelError] = useState('');

  const createMut = useMutation({ mutationFn: (d: CreatePresetRequest) => presetsApi.create(d), onSuccess: () => { qc.invalidateQueries({ queryKey: ['presets'] }); setOpen(false); } });
  const updateMut = useMutation({ mutationFn: ({ id, d }: { id: string; d: CreatePresetRequest }) => presetsApi.update(id, d), onSuccess: () => { qc.invalidateQueries({ queryKey: ['presets'] }); setOpen(false); } });
  const deleteMut = useMutation({ mutationFn: presetsApi.delete, onSuccess: () => qc.invalidateQueries({ queryKey: ['presets'] }) });

  const openCreate = () => { setEditing(null); setForm(emptyForm()); setModels([]); setOpen(true); };
  const openEdit = (p: PresetDto) => {
    setEditing(p);
    setForm({ name: p.name, providerType: p.providerType, providerUrl: p.providerUrl, apiToken: '', generationModel: p.generationModel, embeddingModel: p.embeddingModel ?? '', timeoutSeconds: p.timeoutSeconds != null ? String(p.timeoutSeconds) : '' });
    setModels([]);
    setOpen(true);
  };

  const handleSave = () => {
    const payload: CreatePresetRequest = { name: form.name, providerType: form.providerType, providerUrl: form.providerUrl, apiToken: form.apiToken || undefined, generationModel: form.generationModel, embeddingModel: form.embeddingModel || undefined, timeoutSeconds: form.timeoutSeconds ? Number(form.timeoutSeconds) : undefined };
    if (editing) updateMut.mutate({ id: editing.id, d: payload });
    else createMut.mutate(payload);
  };

  const loadModels = async () => {
    setLoadingModels(true); setModelError('');
    try {
      const payload: GetModelsInlineRequest = {
        providerType: form.providerType,
        providerUrl: form.providerUrl,
        apiToken: form.apiToken || undefined,
        generationModel: form.generationModel || undefined,
      };
      const m = await presetsApi.getModelsInline(payload);
      setModels(m);
    } catch { setModelError('Failed to load models'); }
    finally { setLoadingModels(false); }
  };

  return (
    <Box sx={{ p: 3 }}>
      <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', mb: 2 }}>
        <Typography variant="h5" sx={{ fontWeight: 700 }}>LLM Presets</Typography>
        <Button variant="contained" onClick={openCreate}>Add Preset</Button>
      </Box>

      {isLoading && <Typography>Loading…</Typography>}
      <Box sx={{ display: 'flex', flexWrap: 'wrap', gap: 2 }}>
        {presets.map(p => (
          <Card key={p.id} sx={{ width: 300 }}>
            <CardContent>
              <Typography variant="h6">{p.name}</Typography>
              <Typography color="text.secondary">{p.providerType} — {p.generationModel}</Typography>
              <Typography variant="body2" noWrap>{p.providerUrl}</Typography>
              {p.hasApiToken && <Typography variant="caption">Has API token</Typography>}
            </CardContent>
            <CardActions>
              <IconButton onClick={() => openEdit(p)}><EditIcon /></IconButton>
              <IconButton onClick={() => deleteMut.mutate(p.id)} color="error"><DeleteIcon /></IconButton>
            </CardActions>
          </Card>
        ))}
      </Box>

      <Dialog open={open} onClose={() => setOpen(false)} fullWidth maxWidth="sm">
        <DialogTitle>{editing ? 'Edit Preset' : 'New Preset'}</DialogTitle>
        <DialogContent sx={{ display: 'flex', flexDirection: 'column', gap: 2, pt: 2 }}>
          <TextField label="Name" value={form.name} onChange={e => setForm({ ...form, name: e.target.value })} fullWidth required />
          <FormControl fullWidth>
            <InputLabel>Provider Type</InputLabel>
            <Select value={form.providerType} label="Provider Type" onChange={e => setForm({ ...form, providerType: e.target.value as ProviderType })}>
              {PROVIDER_TYPES.map(t => <MenuItem key={t} value={t}>{t}</MenuItem>)}
            </Select>
          </FormControl>
          <TextField label="Provider URL" value={form.providerUrl} onChange={e => setForm({ ...form, providerUrl: e.target.value })} fullWidth />
          <TextField label="API Token (leave blank to keep existing)" value={form.apiToken} onChange={e => setForm({ ...form, apiToken: e.target.value })} fullWidth type="password" />
          <Box sx={{ display: 'flex', gap: 1, alignItems: 'flex-end' }}>
            <Autocomplete
              freeSolo fullWidth
              options={models}
              value={form.generationModel}
              onInputChange={(_, v) => setForm({ ...form, generationModel: v })}
              renderInput={(params) => <TextField {...params} label="Generation Model" required />}
            />
            <Tooltip title="Load models from provider">
              <span>
                <IconButton onClick={loadModels} disabled={loadingModels}><RefreshIcon /></IconButton>
              </span>
            </Tooltip>
          </Box>
          {modelError && <Alert severity="warning">{modelError}</Alert>}
          <Autocomplete
            freeSolo fullWidth
            options={models}
            value={form.embeddingModel}
            onInputChange={(_, v) => setForm({ ...form, embeddingModel: v })}
            renderInput={(params) => <TextField {...params} label="Embedding Model (optional)" />}
          />
          <TextField
            label="Timeout (seconds, optional)"
            value={form.timeoutSeconds}
            onChange={e => setForm({ ...form, timeoutSeconds: e.target.value.replace(/[^0-9]/g, '') })}
            fullWidth
            type="number"
            inputProps={{ min: 1 }}
            helperText="Leave blank to use the default HTTP timeout"
          />
        </DialogContent>
        <DialogActions>
          <Button onClick={() => setOpen(false)}>Cancel</Button>
          <Button variant="contained" onClick={handleSave} disabled={createMut.isPending || updateMut.isPending}>Save</Button>
        </DialogActions>
      </Dialog>
    </Box>
  );
}
