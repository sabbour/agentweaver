<script setup lang="ts">
import { ref } from 'vue'

// No `text` prop: the sibling `<pre><code>` inside the same
// `.aw-hero-quickstart-card` stays the single source of truth for the
// command text, so nothing can drift out of sync between what's shown and
// what's copied.
const root = ref<HTMLElement | null>(null)
const state = ref<'idle' | 'copied' | 'error'>('idle')
let resetTimer: ReturnType<typeof setTimeout> | undefined

function getCommandText(): string {
  const card = root.value?.closest('.aw-hero-quickstart-card')
  const code = card?.querySelector('code')
  return code?.innerText ?? ''
}

async function handleClick() {
  const text = getCommandText()
  if (!text) return

  // Clipboard access only happens here, inside the click handler — never at
  // module or render scope — so this stays SSR-safe.
  const clipboard = typeof navigator !== 'undefined' ? navigator.clipboard : undefined

  try {
    if (!clipboard) throw new Error('Clipboard API unavailable')
    await clipboard.writeText(text)
    state.value = 'copied'
  } catch {
    // Fall back gracefully: don't throw, just prompt the user to copy manually.
    state.value = 'error'
  }

  clearTimeout(resetTimer)
  resetTimer = setTimeout(() => {
    state.value = 'idle'
  }, 1500)
}
</script>

<template>
  <button
    ref="root"
    type="button"
    class="aw-copy-button"
    :class="{ 'aw-copy-button-copied': state === 'copied', 'aw-copy-button-error': state === 'error' }"
    :aria-label="state === 'copied' ? 'Copied to clipboard' : 'Copy command to clipboard'"
    @click="handleClick"
  >
    <span aria-hidden="true">{{ state === 'copied' ? 'Copied!' : state === 'error' ? 'Ctrl+C' : 'Copy' }}</span>
    <span class="aw-copy-button-live" aria-live="polite">{{ state === 'copied' ? 'Copied to clipboard' : '' }}</span>
  </button>
</template>
