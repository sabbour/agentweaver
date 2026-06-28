import DefaultTheme from 'vitepress/theme'
import { onMounted, watch, nextTick } from 'vue'
import { useRoute } from 'vitepress'
import './custom.css'

const BOUND_ATTR = 'data-lightbox-bound'

function closeOverlay() {
  const existing = document.querySelector('.mermaid-lightbox-overlay')
  if (existing) existing.remove()
  document.removeEventListener('keydown', onKeydown)
}

function onKeydown(e: KeyboardEvent) {
  if (e.key === 'Escape') closeOverlay()
}

function openOverlay(svg: SVGElement) {
  closeOverlay()

  const overlay = document.createElement('div')
  overlay.className = 'mermaid-lightbox-overlay'

  // Wrap the cloned SVG in a `.mermaid` container so the dark-mode chrome
  // overrides in custom.css (scoped under `.mermaid`) apply in the lightbox.
  const wrapper = document.createElement('div')
  wrapper.className = 'mermaid'

  const clone = svg.cloneNode(true) as SVGElement
  clone.removeAttribute('style')
  wrapper.appendChild(clone)
  overlay.appendChild(wrapper)

  overlay.addEventListener('click', () => closeOverlay())
  document.addEventListener('keydown', onKeydown)

  document.body.appendChild(overlay)
}

function bindLightbox() {
  if (import.meta.env.SSR || typeof window === 'undefined') return

  const diagrams = document.querySelectorAll<HTMLElement>('.vp-doc .mermaid')
  diagrams.forEach((el) => {
    if (el.getAttribute(BOUND_ATTR) === 'true') return
    const svg = el.querySelector('svg')
    if (!svg) return // mermaid hasn't rendered yet; a later pass will bind it
    el.setAttribute(BOUND_ATTR, 'true')
    el.addEventListener('click', () => {
      const current = el.querySelector('svg')
      if (current) openOverlay(current)
    })
  })
}

// Mermaid renders client-side and asynchronously, so re-query a few times
// after navigation to catch diagrams that render after the initial pass.
function scheduleBind() {
  if (import.meta.env.SSR || typeof window === 'undefined') return
  ;[0, 300, 800, 1500].forEach((delay) => setTimeout(bindLightbox, delay))
}

export default {
  extends: DefaultTheme,
  setup() {
    if (import.meta.env.SSR || typeof window === 'undefined') return
    const route = useRoute()
    onMounted(() => scheduleBind())
    // Re-bind after client-side route changes (SVGs are re-rendered).
    watch(
      () => route.path,
      () => {
        closeOverlay()
        nextTick(() => scheduleBind())
      }
    )
  },
}
