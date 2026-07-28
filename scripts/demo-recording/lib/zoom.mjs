export function classifyZoom(beat) {
  const haystack = `${beat.title} ${beat.narrationSource ?? ''} ${beat.markdown ?? ''}`.toLowerCase();
  if (/\bpreview|narrow|repair|bug|diagnosis|test\b/.test(haystack)) {
    return { scale: 1.65, holdMs: 900, semantic: 'detail' };
  }
  if (/\bsettings|webhook|memory|dashboard|observability|workflow|graph\b/.test(haystack)) {
    return { scale: 1.55, holdMs: 760, semantic: 'read' };
  }
  if (/\bcreate|choose|confirm|ship|approve|click|button\b/.test(haystack)) {
    return { scale: 1.45, holdMs: 520, semantic: 'action' };
  }
  return { scale: 1.5, holdMs: 620, semantic: 'balanced' };
}

export function cornerBadgeHtml(label, title) {
  return `
    <div style="position:fixed;top:16px;left:16px;z-index:2147483644;padding:8px 12px;
      background:rgba(15,23,42,.92);color:white;border-radius:999px;font:600 13px/1.2 Segoe UI,Arial,sans-serif;
      box-shadow:0 10px 24px rgba(15,23,42,.28);letter-spacing:.01em">
      <span style="opacity:.78">${label}</span>
      <span style="margin:0 6px;opacity:.45">•</span>
      <span>${title}</span>
    </div>`;
}

export function browserZoomBootstrapSource() {
  return `
    (() => {
      const existing = document.getElementById('demo-cursor');
      // IMPORTANT: transform document.body itself, not '#root'. Fluent UI v9
      // (and most portal-based dialog libraries) render Dialog/DialogSurface
      // content into a portal appended as a *sibling* of '#root' directly
      // under <body> — not inside it. Scaling '#root' alone leaves those
      // portal-rendered modals unscaled and stuck in place while the page
      // behind them zooms/pans, which reads as "zoom hits the wrong layer".
      // Transforming 'body' keeps '#root' and any portal content in lockstep.
      const root = document.body;
      if (!root || existing) return;
      document.documentElement.style.overflow = 'hidden';
      document.body.style.overflow = 'hidden';
      root.style.transformOrigin = '0 0';
      root.style.transition = 'transform 460ms cubic-bezier(0.22, 1, 0.36, 1)';
      root.style.willChange = 'transform';
      const style = document.createElement('style');
      style.id = 'demo-cursor-style';
      style.textContent = \`
        #demo-cursor{position:fixed;left:0;top:0;width:24px;height:24px;pointer-events:none;z-index:2147483647;
          background-image:url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='24' height='24' viewBox='0 0 24 24'%3E%3Cpath d='M3 2 L3 19 L7.8 14.2 L10.9 21 L14.1 19.7 L11 13.1 L17.9 13.1 Z' fill='white' stroke='%230f172a' stroke-width='1.5' stroke-linejoin='round'/%3E%3C/svg%3E");
          background-size:24px 24px;background-repeat:no-repeat;filter:drop-shadow(0 1px 2px rgba(15,23,42,.45));
          transition:left 80ms linear,top 80ms linear,transform 120ms ease;transform-origin:2px 2px;}
        #demo-cursor.demo-click{transform:scale(.94);}
        #demo-cursor-ripple{position:fixed;left:0;top:0;width:20px;height:20px;border-radius:9999px;border:2px solid rgba(59,130,246,.82);
          pointer-events:none;opacity:0;z-index:2147483646;transform:translate(-6px,-6px) scale(.2);}
        #demo-cursor-ripple.demo-ripple{animation:demoCursorRipple .38s ease-out;}
        @keyframes demoCursorRipple{0%{opacity:.95;transform:translate(-6px,-6px) scale(.25);}100%{opacity:0;transform:translate(-6px,-6px) scale(2.5);}}
      \`;
      document.documentElement.appendChild(style);
      const cursor = document.createElement('div');
      cursor.id = 'demo-cursor';
      const ripple = document.createElement('div');
      ripple.id = 'demo-cursor-ripple';
      document.documentElement.appendChild(ripple);
      document.documentElement.appendChild(cursor);
      const clamp = (v, min, max) => Math.max(min, Math.min(max, v));
      window.__demoCursorMove = (x, y) => {
        cursor.style.left = x + 'px';
        cursor.style.top = y + 'px';
        ripple.style.left = (x + 2) + 'px';
        ripple.style.top = (y + 2) + 'px';
      };
      window.__demoCursorClick = () => {
        cursor.classList.add('demo-click');
        ripple.classList.remove('demo-ripple');
        void ripple.offsetWidth;
        ripple.classList.add('demo-ripple');
        setTimeout(() => cursor.classList.remove('demo-click'), 180);
      };
      window.__demoZoomFocus = (x, y, scale = 1.5, targetXPct = 0.5, targetYPct = 0.46) => {
        const w = window.innerWidth;
        const h = window.innerHeight;
        const txRaw = w * targetXPct - x * scale;
        const tyRaw = h * targetYPct - y * scale;
        const minX = w - w * scale;
        const minY = h - h * scale;
        const tx = clamp(txRaw, minX, 0);
        const ty = clamp(tyRaw, minY, 0);
        root.style.transform = \`translate(\${tx}px, \${ty}px) scale(\${scale})\`;
      };
      window.__demoZoomReset = () => { root.style.transform = 'translate(0px, 0px) scale(1)'; };
      const startedAt = performance.now();
      const activity = [];
      const pushActivity = (kind, detail = {}) => {
        const t = Math.max(0, Math.round(performance.now() - startedAt));
        const previous = activity[activity.length - 1];
        if (kind === 'mutation' && previous?.kind === 'mutation' && (t - previous.t) < 300) return;
        activity.push({ kind, t, ...detail });
      };
      pushActivity('capture-ready');
      const observer = new MutationObserver(() => pushActivity('mutation'));
      observer.observe(document.documentElement, { subtree: true, childList: true, characterData: true, attributes: true });
      window.__demoActivityMark = (kind, detail = {}) => pushActivity(kind, detail);
      window.__demoGetActivityLog = () => activity.slice();
      window.__demoStopActivity = () => {
        observer.disconnect();
        pushActivity('capture-stop');
        return activity.slice();
      };
    })();
  `;
}
