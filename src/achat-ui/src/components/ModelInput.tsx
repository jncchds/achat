import { useEffect, useId, useRef, useState } from 'react'

interface ModelInputProps {
  value: string
  onChange: (value: string) => void
  /** The loaded model list (from "Load models"). Pure suggestions — any text is accepted. */
  suggestions: string[]
  placeholder?: string
  disabled?: boolean
  ariaLabel?: string
}

const MAX_VISIBLE = 50

/**
 * A model-id input with suggestion autocomplete (§3 "Load models"). The field
 * stays freeform: a suggestion can be clicked / arrow-keyed into, or any model
 * id not on the list can be typed. Suggestions are a case-insensitive
 * substring filter of the loaded list, capped so long catalogues stay usable.
 */
export default function ModelInput({
  value, onChange, suggestions, placeholder, disabled, ariaLabel,
}: ModelInputProps) {
  const [open, setOpen] = useState(false)
  const [active, setActive] = useState(-1)
  const rootRef = useRef<HTMLDivElement>(null)
  const listId = useId()

  const needle = value.trim().toLowerCase()
  const matches = (
    needle === ''
      ? suggestions
      : suggestions.filter(s => s.toLowerCase().includes(needle))
  ).slice(0, MAX_VISIBLE)
  const showing = open && matches.length > 0

  // Close on outside click (the dropdown is inside rootRef, so picking an item
  // does not trip this).
  useEffect(() => {
    if (!showing) return
    const onDoc = (e: MouseEvent) => {
      if (rootRef.current && !rootRef.current.contains(e.target as Node)) {
        setOpen(false)
        setActive(-1)
      }
    }
    document.addEventListener('mousedown', onDoc)
    return () => document.removeEventListener('mousedown', onDoc)
  }, [showing])

  const close = () => { setOpen(false); setActive(-1) }

  const pick = (v: string) => {
    onChange(v)
    close()
  }

  const onKeyDown = (e: React.KeyboardEvent) => {
    if (disabled) return
    switch (e.key) {
      case 'ArrowDown':
        e.preventDefault()
        setOpen(true)
        setActive(a => (matches.length === 0 ? -1 : a < matches.length - 1 ? a + 1 : 0))
        break
      case 'ArrowUp':
        e.preventDefault()
        setActive(a => (matches.length === 0 ? -1 : a > 0 ? a - 1 : matches.length - 1))
        break
      case 'Enter':
        if (showing && active >= 0 && matches[active]) {
          e.preventDefault() // don't submit the form while picking a suggestion
          pick(matches[active])
        }
        break
      case 'Escape':
        close()
        break
      case 'Tab':
        close()
        break
    }
  }

  return (
    <div ref={rootRef} className="model-input">
      <input
        role="combobox"
        aria-expanded={showing}
        aria-controls={showing ? listId : undefined}
        aria-autocomplete="list"
        aria-activedescendant={showing && active >= 0 ? `${listId}-opt-${active}` : undefined}
        aria-label={ariaLabel}
        value={value}
        disabled={disabled}
        placeholder={placeholder}
        onChange={e => { onChange(e.target.value); setOpen(true); setActive(-1) }}
        onFocus={() => { if (suggestions.length > 0) setOpen(true) }}
        onKeyDown={onKeyDown}
        autoComplete="off"
        spellCheck={false}
      />
      {showing && (
        <ul className="model-suggest" id={listId} role="listbox" aria-label="Model suggestions">
          {matches.map((s, i) => (
            <li
              key={s}
              id={`${listId}-opt-${i}`}
              role="option"
              aria-selected={i === active}
              className={
                'model-suggest-item'
                + (i === active ? ' active' : '')
                + (s === value ? ' current' : '')
              }
              onMouseDown={e => { e.preventDefault(); pick(s) }}
              onMouseEnter={() => setActive(i)}
            >
              {s}
            </li>
          ))}
          {matches.length < suggestions.length && (
            <li className="model-suggest-more">
              …{suggestions.length - matches.length} more — keep typing to narrow
            </li>
          )}
        </ul>
      )}
    </div>
  )
}
