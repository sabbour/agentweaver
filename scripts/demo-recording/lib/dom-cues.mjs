export function browserDomCueBootstrapSource() {
  return `
    (() => {
      if (window.__demoConfigureDomCueWatchers) return;

      let observer = null;
      let definitions = [];
      let scheduled = false;
      const fired = new Set();
      const matchingSince = new Map();
      const armedAt = new Map();
      const normalizeText = (value) => String(value ?? '').replace(/\\s+/g, ' ').trim();
      const visible = (element) => {
        if (!(element instanceof Element)) return false;
        const rect = element.getBoundingClientRect();
        if (rect.width <= 0 || rect.height <= 0) return false;
        const style = getComputedStyle(element);
        if (style.display === 'none' || style.visibility === 'hidden' || style.opacity === '0') return false;
        return rect.bottom > 0 && rect.right > 0 && rect.top < innerHeight && rect.left < innerWidth;
      };
      const query = (selector) => {
        try { return Array.from(document.querySelectorAll(selector)); } catch (e) { return []; }
      };
      const attributeValue = (element, attribute) => element?.getAttribute?.(attribute);
      const textMatches = (text, source) => {
        const normalized = normalizeText(text);
        if (source.equals !== undefined) return normalized === normalizeText(source.equals);
        if (source.includes !== undefined) return normalized.includes(normalizeText(source.includes));
        if (source.pattern !== undefined) {
          try { return new RegExp(source.pattern, source.flags ?? '').test(normalized); } catch (e) { return false; }
        }
        return false;
      };
      const unionRect = (elements) => {
        const rects = elements.filter(visible).map((element) => element.getBoundingClientRect());
        if (!rects.length) return null;
        const left = Math.max(0, Math.min(...rects.map((rect) => rect.left)));
        const top = Math.max(0, Math.min(...rects.map((rect) => rect.top)));
        const right = Math.min(innerWidth, Math.max(...rects.map((rect) => rect.right)));
        const bottom = Math.min(innerHeight, Math.max(...rects.map((rect) => rect.bottom)));
        return { x: left, y: top, width: Math.max(0, right - left), height: Math.max(0, bottom - top) };
      };
      const elementRect = (element) => {
        if (!visible(element)) return null;
        const rect = element.getBoundingClientRect();
        return {
          x: Math.max(0, rect.left),
          y: Math.max(0, rect.top),
          width: Math.max(0, Math.min(innerWidth, rect.right) - Math.max(0, rect.left)),
          height: Math.max(0, Math.min(innerHeight, rect.bottom) - Math.max(0, rect.top)),
        };
      };
      const resolveRect = (definition, matchedElements) => {
        const rect = definition.rect ?? { mode: 'matched-element' };
        if (rect.mode === 'none') return { rect: null, rectStatus: 'not-requested' };
        let value = null;
        if (rect.mode === 'viewport') value = { x: 0, y: 0, width: innerWidth, height: innerHeight };
        if (rect.mode === 'matched-element') value = elementRect(matchedElements[0]);
        if (rect.mode === 'element' || rect.mode === 'first-matching') value = elementRect(query(rect.selector)[0]);
        if (rect.mode === 'union') value = unionRect(query(rect.selector));
        return value
          ? { rect: value, rectStatus: 'captured' }
          : { rect: null, rectStatus: 'missing-or-not-visible' };
      };
      const evaluateSource = (source) => {
        const candidates = query(source.selector ?? 'body');
        if (source.kind === 'selector') {
          const matched = source.state === 'attached' ? candidates : candidates.filter(visible);
          return { matched, observed: { matchCount: matched.length } };
        }
        if (source.kind === 'attribute') {
          const matched = candidates.filter((element) => {
            const value = attributeValue(element, source.attribute);
            if (source.equals !== undefined) return value === String(source.equals);
            if (source.values) return source.values.map(String).includes(value);
            return value !== null;
          });
          return {
            matched,
            observed: {
              matchCount: matched.length,
              attribute: source.attribute,
              value: matched.length ? attributeValue(matched[0], source.attribute) : null,
            },
          };
        }
        if (source.kind === 'text') {
          const matched = candidates.filter((element) => textMatches(element.textContent, source));
          return {
            matched,
            observed: { matchCount: matched.length, text: matched.length ? normalizeText(matched[0].textContent) : null },
          };
        }
        if (source.kind !== 'predicate') return { matched: [], observed: { matchCount: 0 } };

        const values = candidates.map((element) => attributeValue(element, source.attribute));
        const allowed = (source.values ?? []).map(String);
        let pass = false;
        if (source.operator === 'exists') pass = candidates.some(visible);
        if (source.operator === 'count-gte') pass = candidates.length >= Number(source.value);
        if (source.operator === 'count-eq') pass = candidates.length === Number(source.value);
        if (source.operator === 'any-attribute-in') pass = values.some((value) => allowed.includes(value));
        if (source.operator === 'all-attribute-in') {
          pass = candidates.length >= Number(source.minCount ?? 1) && values.every((value) => allowed.includes(value));
        }
        if (source.operator === 'text-includes') {
          pass = candidates.some((element) => normalizeText(element.textContent).includes(normalizeText(source.value)));
        }
        if (source.operator === 'text-matches') {
          try {
            const regex = new RegExp(source.pattern, source.flags ?? '');
            pass = candidates.some((element) => regex.test(normalizeText(element.textContent)));
          } catch (e) {
            pass = false;
          }
        }
        return {
          matched: pass ? candidates : [],
          observed: {
            matchCount: candidates.length,
            attribute: source.attribute ?? null,
            values: source.attribute ? values : undefined,
          },
        };
      };
      const report = (definition, result) => {
        const name = definition.name;
        if (!name || fired.has(name)) return false;
        const { rect, rectStatus } = resolveRect(definition, result.matched);
        const viewportWidth = innerWidth;
        const viewportHeight = innerHeight;
        const rectNormalized = rect ? {
          x: rect.x / viewportWidth,
          y: rect.y / viewportHeight,
          width: rect.width / viewportWidth,
          height: rect.height / viewportHeight,
        } : null;
        fired.add(name);
        matchingSince.delete(name);
        const payload = {
          name,
          pageObservedTimeMs: performance.now(),
          url: location.href,
          source: definition.source ?? { kind: 'selector' },
          observed: result.observed ?? {},
          coordinateSpace: {
            kind: 'browser-viewport-css-pixels',
            viewportWidth,
            viewportHeight,
            devicePixelRatio,
          },
          rectCssPx: rect,
          rectNormalized,
          rectStatus,
        };
        try { globalThis.__demoReportCue?.(payload); } catch (e) {}
        window.__demoActivityMark?.('cue', { name });
        return true;
      };
      const evaluateDefinition = (definition) => {
        if (!definition?.name || fired.has(definition.name)) return;
        const now = performance.now();
        const armed = armedAt.get(definition.name) ?? now;
        if (definition.deadlineMs && (now - armed) > Number(definition.deadlineMs)) return;
        const result = evaluateSource(definition.source ?? {});
        const matched = result.matched.length > 0;
        if (!matched) {
          matchingSince.delete(definition.name);
          return;
        }
        const stableForMs = Math.max(0, Number(definition.stableForMs ?? 0));
        if (!stableForMs) {
          report(definition, result);
          return;
        }
        const since = matchingSince.get(definition.name) ?? now;
        matchingSince.set(definition.name, since);
        const remaining = stableForMs - (now - since);
        if (remaining <= 0) {
          report(definition, result);
        } else {
          setTimeout(scheduleEvaluation, Math.ceil(remaining));
        }
      };
      const evaluateAll = () => definitions.forEach(evaluateDefinition);
      function scheduleEvaluation() {
        if (scheduled) return;
        scheduled = true;
        queueMicrotask(() => {
          scheduled = false;
          evaluateAll();
        });
      }
      const startObserver = () => {
        observer?.disconnect();
        const attributes = new Set(['class', 'style', 'hidden', 'aria-hidden']);
        definitions.forEach((definition) => {
          if (definition?.source?.attribute) attributes.add(definition.source.attribute);
        });
        observer = new MutationObserver(scheduleEvaluation);
        observer.observe(document.documentElement, {
          subtree: true,
          childList: true,
          characterData: true,
          attributes: true,
          attributeFilter: Array.from(attributes),
        });
      };

      window.__demoEmitDomCue = (definition, element) => {
        if (!definition?.name || fired.has(definition.name)) return false;
        const matched = element instanceof Element ? [element] : [];
        return report(definition, {
          matched,
          observed: {
            matchCount: matched.length,
            text: matched.length ? normalizeText(matched[0].textContent) : null,
          },
        });
      };
      window.__demoConfigureDomCueWatchers = (nextDefinitions = []) => {
        definitions = Array.isArray(nextDefinitions) ? nextDefinitions.filter(Boolean) : [];
        matchingSince.clear();
        const now = performance.now();
        definitions.forEach((definition) => armedAt.set(definition.name, now));
        startObserver();
        evaluateAll();
      };
      window.__demoStopDomCueWatchers = () => {
        observer?.disconnect();
        observer = null;
        definitions = [];
        matchingSince.clear();
        armedAt.clear();
      };
    })();
  `;
}
