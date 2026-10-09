const MUTE_KEY = 'aw.notifications.muted';

export function getNotificationsMuted(): boolean {
  try {
    return localStorage.getItem(MUTE_KEY) === '1';
  } catch {
    return false;
  }
}

export function setNotificationsMuted(muted: boolean): void {
  try {
    localStorage.setItem(MUTE_KEY, muted ? '1' : '0');
  } catch {
    /* localStorage unavailable — mute preference just won't persist */
  }
}

// Lazily created so we never touch AudioContext until a chime is actually needed.
let audioContext: AudioContext | null = null;

function getAudioContext(): AudioContext | null {
  if (typeof window === 'undefined') return null;
  const Ctor = window.AudioContext
    ?? (window as unknown as { webkitAudioContext?: typeof AudioContext }).webkitAudioContext;
  if (!Ctor) return null;
  if (!audioContext) audioContext = new Ctor();
  return audioContext;
}

/**
 * Browsers block audio (including WebAudio oscillators) from starting before a user gesture
 * (autoplay policy). Call once at app root: the first pointerdown/keydown resumes a suspended
 * AudioContext so a LATER notification chime (arriving on its own, with no direct gesture) is
 * allowed to play. Idempotent and safe to call from multiple mounted components; returns a
 * disarm function for cleanup.
 */
export function armAudioUnlock(): () => void {
  const unlock = () => {
    const ctx = getAudioContext();
    if (ctx && ctx.state === 'suspended') {
      void ctx.resume().catch(() => {});
    }
  };
  window.addEventListener('pointerdown', unlock, { once: true });
  window.addEventListener('keydown', unlock, { once: true });
  return () => {
    window.removeEventListener('pointerdown', unlock);
    window.removeEventListener('keydown', unlock);
  };
}

/**
 * Plays a short two-tone chime for a newly-arrived notification. No-ops silently when muted, when
 * the AudioContext hasn't been unlocked by a user gesture yet (respecting autoplay policy), or
 * when WebAudio isn't available — this must never throw or block the toast/badge from showing.
 */
export function playNotificationChime(): void {
  if (getNotificationsMuted()) return;
  try {
    const ctx = getAudioContext();
    if (!ctx || ctx.state === 'suspended') return;
    const now = ctx.currentTime;
    [880, 1320].forEach((freq, i) => {
      const osc = ctx.createOscillator();
      const gain = ctx.createGain();
      osc.type = 'sine';
      osc.frequency.value = freq;
      const start = now + i * 0.12;
      gain.gain.setValueAtTime(0.0001, start);
      gain.gain.exponentialRampToValueAtTime(0.15, start + 0.02);
      gain.gain.exponentialRampToValueAtTime(0.0001, start + 0.16);
      osc.connect(gain).connect(ctx.destination);
      osc.start(start);
      osc.stop(start + 0.18);
    });
  } catch {
    // Never let a sound glitch break the notification pipeline.
  }
}
