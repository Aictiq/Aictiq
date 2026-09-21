import { onBeforeUnmount, onMounted } from 'vue'

/**
 * Keyboard shortcuts, including the sequence kind ("g then i") that keyboard-first tools
 * live on.
 *
 * Two rules make the difference between a shortcut system that helps and one that fights
 * you:
 *
 *  - **Never steal a key while someone is typing.** A binding fires only when the event
 *    target is not an input, textarea, select or contenteditable — otherwise typing "g"
 *    in a title navigates away.
 *  - **A sequence is a sequence, not two shortcuts.** "g then i" only fires if the two
 *    keys arrive within the timeout, and any other key cancels the pending prefix.
 */

/** Modifier-combination form: `mod+k` (mod = ⌘ on macOS, Ctrl elsewhere), `shift+/`. */
type Combo = string

export interface ShortcutOptions {
  /** Fire even while a text field has focus. For Escape and mod-combinations only. */
  allowInInput?: boolean
  /** Do not fire while this returns false. */
  when?: () => boolean
}

const SEQUENCE_TIMEOUT_MS = 1200

const isMac =
  typeof navigator !== 'undefined' &&
  /Mac|iPhone|iPad/.test(navigator.platform || navigator.userAgent)

/** `mod` renders as ⌘ on Apple platforms and Ctrl everywhere else. */
export function formatShortcut(binding: string): string {
  const pretty = (key: string) =>
    key
      .replace('mod', isMac ? '⌘' : 'Ctrl')
      .replace('shift', '⇧')
      .replace('alt', isMac ? '⌥' : 'Alt')
      .replace('ctrl', 'Ctrl')

  return binding
    .split(' then ')
    .map((part) =>
      part
        .split('+')
        .map(pretty)
        .join(isMac ? '' : '+'),
    )
    .join(' ')
}

function isTypingTarget(target: EventTarget | null): boolean {
  if (!(target instanceof HTMLElement)) return false
  if (target.isContentEditable) return true
  return ['INPUT', 'TEXTAREA', 'SELECT'].includes(target.tagName)
}

function matchesCombo(event: KeyboardEvent, combo: Combo): boolean {
  const parts = combo.toLowerCase().split('+')
  const key = parts[parts.length - 1] ?? ''

  const wantsMod = parts.includes('mod')
  const wantsShift = parts.includes('shift')
  const wantsAlt = parts.includes('alt')
  const wantsCtrl = parts.includes('ctrl')

  const mod = isMac ? event.metaKey : event.ctrlKey
  if (wantsMod !== mod) return false
  if (wantsShift !== event.shiftKey) return false
  if (wantsAlt !== event.altKey) return false
  // `ctrl` is only checked separately from `mod` on macOS, where they differ.
  if (isMac && wantsCtrl !== event.ctrlKey) return false

  return event.key.toLowerCase() === key
}

interface Registration {
  binding: string
  handler: (event: KeyboardEvent) => void
  options: ShortcutOptions
}

const registrations: Registration[] = []
let pendingPrefix: string | null = null
let pendingTimer: ReturnType<typeof setTimeout> | undefined
let listening = false

function clearPrefix() {
  pendingPrefix = null
  if (pendingTimer) clearTimeout(pendingTimer)
  pendingTimer = undefined
}

function onKeydown(event: KeyboardEvent) {
  // A modifier held on its own is never a shortcut; ignoring it keeps a pending
  // sequence alive while someone reaches for the next key.
  if (['Shift', 'Control', 'Alt', 'Meta'].includes(event.key)) return

  const typing = isTypingTarget(event.target)

  for (const registration of registrations) {
    const { binding, handler, options } = registration
    if (options.when && !options.when()) continue
    if (typing && !options.allowInInput) continue

    const steps = binding.split(' then ')

    if (steps.length === 1) {
      if (pendingPrefix === null && matchesCombo(event, steps[0]!)) {
        event.preventDefault()
        handler(event)
        return
      }
      continue
    }

    const [first, second] = steps as [string, string]

    if (pendingPrefix === first && matchesCombo(event, second)) {
      event.preventDefault()
      clearPrefix()
      handler(event)
      return
    }

    if (pendingPrefix === null && matchesCombo(event, first)) {
      event.preventDefault()
      pendingPrefix = first
      pendingTimer = setTimeout(clearPrefix, SEQUENCE_TIMEOUT_MS)
      return
    }
  }

  // Anything that was not the awaited second key abandons the sequence, so a stale
  // prefix cannot silently swallow a later keystroke.
  clearPrefix()
}

function ensureListening() {
  if (listening || typeof window === 'undefined') return
  window.addEventListener('keydown', onKeydown)
  listening = true
}

/**
 * Binds a shortcut for the lifetime of the calling component.
 *
 * @param binding `mod+k`, `?`, or a sequence like `g then i`.
 */
export function useShortcut(
  binding: string,
  handler: (event: KeyboardEvent) => void,
  options: ShortcutOptions = {},
) {
  const registration: Registration = { binding, handler, options }

  onMounted(() => {
    // Unshift so a component mounted later (a dialog) wins over the page beneath it.
    registrations.unshift(registration)
    ensureListening()
  })

  onBeforeUnmount(() => {
    const index = registrations.indexOf(registration)
    if (index >= 0) registrations.splice(index, 1)
  })
}

/** Test seam: the registry is module-level, so specs must be able to reset it. */
export function __resetShortcutsForTests() {
  registrations.length = 0
  clearPrefix()
}
