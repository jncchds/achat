import { useState } from 'react';
import {
  Box, Button, Typography, Table, TableHead, TableRow, TableCell,
  TableBody, Paper, TableContainer, IconButton, Dialog, DialogTitle,
  DialogContent, DialogActions, TextField, MenuItem, Select,
  InputLabel, FormControl, Switch, FormControlLabel, Chip, InputAdornment,
} from '@mui/material';
import DeleteIcon from '@mui/icons-material/Delete';
import EditIcon from '@mui/icons-material/Edit';
import ClearIcon from '@mui/icons-material/Clear';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { adminApi, type UserDto, type CreateUserRequest, type UpdateUserRequest } from '../api/admin';

const emptyCreate = (): CreateUserRequest => ({ username: '', email: '', password: '', role: 'User' });

export default function AdminUsersPage() {
  const qc = useQueryClient();
  const { data: users = [] } = useQuery({ queryKey: ['admin-users'], queryFn: adminApi.getUsers });
  const [openCreate, setOpenCreate] = useState(false);
  const [editing, setEditing] = useState<UserDto | null>(null);
  const [createForm, setCreateForm] = useState<CreateUserRequest>(emptyCreate());
  const [editForm, setEditForm] = useState<UpdateUserRequest>({});
  const [editTgId, setEditTgId] = useState('');

  const createMut = useMutation({ mutationFn: adminApi.createUser, onSuccess: () => { qc.invalidateQueries({ queryKey: ['admin-users'] }); setOpenCreate(false); } });
  const updateMut = useMutation({ mutationFn: ({ id, d }: { id: string; d: UpdateUserRequest }) => adminApi.updateUser(id, d), onSuccess: () => { qc.invalidateQueries({ queryKey: ['admin-users'] }); setEditing(null); } });
  const deleteMut = useMutation({ mutationFn: adminApi.deleteUser, onSuccess: () => qc.invalidateQueries({ queryKey: ['admin-users'] }) });

  const openEdit = (u: UserDto) => {
    setEditing(u);
    setEditForm({ username: u.username, email: u.email, role: u.role, isActive: u.isActive });
    setEditTgId(u.telegramId != null ? String(u.telegramId) : '');
  };

  return (
    <Box sx={{ p: 3 }}>
      <Box sx={{ display: 'flex', justifyContent: 'space-between', mb: 2 }}>
        <Typography variant="h5" sx={{ fontWeight: 700 }}>User Management</Typography>
        <Button variant="contained" onClick={() => { setCreateForm(emptyCreate()); setOpenCreate(true); }}>Add User</Button>
      </Box>

      <TableContainer component={Paper}>
        <Table size="small">
          <TableHead>
            <TableRow>
              <TableCell>Username</TableCell>
              <TableCell>Email</TableCell>
              <TableCell>Role</TableCell>
              <TableCell>Telegram ID</TableCell>
              <TableCell>Status</TableCell>
              <TableCell>Created</TableCell>
              <TableCell>Actions</TableCell>
            </TableRow>
          </TableHead>
          <TableBody>
            {users.map(u => (
              <TableRow key={u.id}>
                <TableCell>{u.username}</TableCell>
                <TableCell>{u.email}</TableCell>
                <TableCell><Chip label={u.role} size="small" color={u.role === 'Admin' ? 'primary' : 'default'} /></TableCell>
                <TableCell>{u.telegramId ?? '—'}</TableCell>
                <TableCell><Chip label={u.isActive ? 'Active' : 'Inactive'} size="small" color={u.isActive ? 'success' : 'error'} /></TableCell>
                <TableCell>{new Date(u.createdAt).toLocaleDateString()}</TableCell>
                <TableCell>
                  <IconButton size="small" onClick={() => openEdit(u)}><EditIcon /></IconButton>
                  <IconButton size="small" color="error" onClick={() => deleteMut.mutate(u.id)}><DeleteIcon /></IconButton>
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </TableContainer>

      {/* Create dialog */}
      <Dialog open={openCreate} onClose={() => setOpenCreate(false)} fullWidth maxWidth="sm">
        <DialogTitle>Create User</DialogTitle>
        <DialogContent sx={{ display: 'flex', flexDirection: 'column', gap: 2, pt: 2 }}>
          <TextField label="Username" value={createForm.username} onChange={e => setCreateForm({ ...createForm, username: e.target.value })} fullWidth required />
          <TextField label="Email" type="email" value={createForm.email} onChange={e => setCreateForm({ ...createForm, email: e.target.value })} fullWidth />
          <TextField label="Password" type="password" value={createForm.password} onChange={e => setCreateForm({ ...createForm, password: e.target.value })} fullWidth required />
          <FormControl fullWidth>
            <InputLabel>Role</InputLabel>
            <Select value={createForm.role} label="Role" onChange={e => setCreateForm({ ...createForm, role: e.target.value })}>
              <MenuItem value="User">User</MenuItem>
              <MenuItem value="Admin">Admin</MenuItem>
            </Select>
          </FormControl>
        </DialogContent>
        <DialogActions>
          <Button onClick={() => setOpenCreate(false)}>Cancel</Button>
          <Button variant="contained" onClick={() => createMut.mutate(createForm)} disabled={createMut.isPending}>Create</Button>
        </DialogActions>
      </Dialog>

      {/* Edit dialog */}
      <Dialog open={!!editing} onClose={() => setEditing(null)} fullWidth maxWidth="sm">
        <DialogTitle>Edit User: {editing?.username}</DialogTitle>
        <DialogContent sx={{ display: 'flex', flexDirection: 'column', gap: 2, pt: 2 }}>
          <TextField label="Username" value={editForm.username ?? ''} onChange={e => setEditForm({ ...editForm, username: e.target.value })} fullWidth />
          <TextField label="Email" type="email" value={editForm.email ?? ''} onChange={e => setEditForm({ ...editForm, email: e.target.value })} fullWidth />
          <TextField label="New Password (blank = no change)" type="password" value={editForm.password ?? ''} onChange={e => setEditForm({ ...editForm, password: e.target.value })} fullWidth />
          <FormControl fullWidth>
            <InputLabel>Role</InputLabel>
            <Select value={editForm.role ?? 'User'} label="Role" onChange={e => setEditForm({ ...editForm, role: e.target.value })}>
              <MenuItem value="User">User</MenuItem>
              <MenuItem value="Admin">Admin</MenuItem>
            </Select>
          </FormControl>
          <FormControlLabel control={<Switch checked={editForm.isActive ?? true} onChange={e => setEditForm({ ...editForm, isActive: e.target.checked })} />} label="Active" />
          <TextField
            label="Telegram ID"
            value={editTgId}
            onChange={e => setEditTgId(e.target.value.replace(/\D/g, ''))}
            fullWidth
            placeholder="Leave blank to clear"
            slotProps={{
              input: {
                endAdornment: editTgId ? (
                  <InputAdornment position="end">
                    <IconButton size="small" onClick={() => setEditTgId('')}><ClearIcon fontSize="small" /></IconButton>
                  </InputAdornment>
                ) : undefined,
              },
            }}
          />
        </DialogContent>
        <DialogActions>
          <Button onClick={() => setEditing(null)}>Cancel</Button>
          <Button variant="contained" onClick={() => {
            const tgPayload: UpdateUserRequest = editTgId
              ? { ...editForm, telegramId: parseInt(editTgId, 10) }
              : editing?.telegramId != null
                ? { ...editForm, clearTelegramId: true }
                : editForm;
            updateMut.mutate({ id: editing!.id, d: tgPayload });
          }} disabled={updateMut.isPending}>Save</Button>
        </DialogActions>
      </Dialog>
    </Box>
  );
}
