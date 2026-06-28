import DefaultTheme from 'vitepress/theme'
import './custom.css'

// Extend the VitePress default theme. The custom.css file carries the
// Mermaid legibility (label clipping) fixes and the dark-mode chrome
// overrides; no runtime enhanceApp logic is required.
export default {
  extends: DefaultTheme,
}
