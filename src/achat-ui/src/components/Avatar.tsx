interface AvatarProps {
  name: string
  url?: string | null
  size?: 'sm' | 'md' | 'lg'
}

/** Colored initial avatar (or image) for a bot / user. */
export default function Avatar({ name, url, size = 'md' }: AvatarProps) {
  const cls = size === 'sm' ? 'avatar sm' : size === 'lg' ? 'avatar lg' : 'avatar'
  if (url) {
    return <span className={cls}><img src={url} alt={name} /></span>
  }
  const initial = name.trim().charAt(0).toUpperCase() || '?'
  return <span className={cls}>{initial}</span>
}
