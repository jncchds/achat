import { createContext, useContext, useEffect, useState, type ReactNode, type Dispatch, type SetStateAction } from 'react'
import type { User } from '../api'
import { getStoredUser, me, logout, setStoredUser } from '../api'

interface AuthCtx {
  user: User | null
  loading: boolean
  setUser: Dispatch<SetStateAction<User | null>>
  refresh: () => Promise<void>
  signOut: () => Promise<void>
}

const AuthContext = createContext<AuthCtx>({
  user: null, loading: true, setUser: () => {}, refresh: async () => {}, signOut: async () => {},
})

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUserState] = useState<User | null>(getStoredUser())
  const [loading, setLoading] = useState(true)

  const refresh = async () => {
    try {
      const u = await me()
      setStoredUser(u)
      setUserState(u)
    } catch {
      setStoredUser(null)
      setUserState(null)
    } finally {
      setLoading(false)
    }
  }

  const signOut = async () => {
    await logout()
    location.href = '/login'
  }

  // Validate the stored token once on mount.
  useEffect(() => {
    refresh()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  return (
    <AuthContext.Provider value={{ user, loading, setUser: setUserState, refresh, signOut }}>
      {children}
    </AuthContext.Provider>
  )
}

export const useAuth = () => useContext(AuthContext)
