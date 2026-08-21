import { useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { login } from '../api'
import { useAuth } from '../hooks/useAuth'

export default function Login() {
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState('')
  const { refresh } = useAuth()
  const navigate = useNavigate()

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault()
    setError('')
    try {
      await login(username, password)
      await refresh()
      navigate('/bots')
    } catch (err: unknown) {
      const msg = (err as { response?: { data?: { message?: string } } })?.response?.data?.message
      setError(msg ?? 'Login failed')
    }
  }

  return (
    <div className="login-page">
      <div className="login-card card">
        <h1>AChat</h1>
        <h2>Sign In</h2>
        <form onSubmit={handleSubmit}>
          <label>
            Username
            <input value={username} onChange={e => setUsername(e.target.value)} required autoFocus autoComplete="username" />
          </label>
          <label>
            Password
            <input type="password" value={password} onChange={e => setPassword(e.target.value)} required autoComplete="current-password" />
          </label>
          {error && <p className="error-msg">{error}</p>}
          <button type="submit">Sign In</button>
        </form>
        <p className="setup-hint">First-run account is created from the bootstrap admin settings.</p>
      </div>
    </div>
  )
}
