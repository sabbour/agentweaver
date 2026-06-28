import DefaultTheme from 'vitepress/theme'
import { onMounted, onUnmounted, watch, nextTick } from 'vue'
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
  wrapper.className = 'mermaid mermaid-lightbox-content'

  const clone = svg.cloneNode(true) as SVGElement
  // The inline `style="max-width:NNNpx"` (from useMaxWidth:true) and the
  // width/height attributes would otherwise pin the clone small. Strip them
  // so the CSS can scale it up to fill the viewport, preserving aspect ratio.
  clone.removeAttribute('style')
  clone.removeAttribute('width')
  clone.removeAttribute('height')
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
    if (!svg) return // mermaid hasn't rendered yet; the observer will retry
    el.setAttribute(BOUND_ATTR, 'true')
    el.addEventListener('click', () => {
      const current = el.querySelector('svg')
      if (current) openOverlay(current)
    })
  })
}

export default {
  extends: DefaultTheme,
  setup() {
    if (import.meta.env.SSR || typeof window === 'undefined') return

    const route = useRoute()
    let observer: MutationObserver | null = null
    let throttled = false

    // Mermaid renders ~60 diagrams asynchronously and out of order; fixed
    // timeouts miss late ones. A MutationObserver on the content tree binds
    // every diagram the moment its <svg> appears.
    const runBind = () => {
      if (throttled) return
      throttled = true
      requestAnimationFrame(() => {
        throttled = false
        bindLightbox()
      })
    }

    const startObserver = () => {
      const target = document.querySelector('.vp-doc') || document.body
      observer?.disconnect()
      observer = new MutationObserver(() => runBind())
      observer.observe(target, { childList: true, subtree: true })
    }

    onMounted(() => {
      bindLightbox()
      nextTick(() => {
        bindLightbox()
        startObserver()
      })
    })

    // On client-side navigation the content root is replaced and SVGs
    // re-render: close any open overlay and re-attach the observer.
    watch(
      () => route.path,
      () => {
        closeOverlay()
        nextTick(() => {
          bindLightbox()
          startObserver()
        })
      }
    )

    onUnmounted(() => {
      observer?.disconnect()
      observer = null
      closeOverlay()
    })
  },
}
