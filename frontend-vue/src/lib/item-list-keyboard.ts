export interface ListKeyboardState { index: number; selected: Set<string> }
export type ListKeyboardAction = 'up' | 'down' | 'toggle'
export function reduceListKeyboard(state: ListKeyboardState, action: ListKeyboardAction, keys: string[]): ListKeyboardState {
  if (action === 'up') return { ...state, index: Math.max(0, state.index - 1) }
  if (action === 'down') return { ...state, index: Math.min(Math.max(0, keys.length - 1), state.index + 1) }
  const key = keys[state.index]; if (!key) return state
  const selected = new Set(state.selected)
  if (selected.has(key)) selected.delete(key)
  else selected.add(key)
  return { ...state, selected }
}
