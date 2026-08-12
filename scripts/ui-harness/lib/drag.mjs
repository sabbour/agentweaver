const DEFAULT_STEPS = 12;
const MAX_STEPS = 100;
const DEFAULT_TIMEOUT = 10_000;
const MAX_TIMEOUT = 120_000;

function boundedInteger(value, name, fallback, max) {
  if (value === undefined) return fallback;
  if (typeof value === 'boolean' || value === '') throw new Error(`--${name} requires a numeric value`);
  const parsed = Number(value);
  if (!Number.isInteger(parsed) || parsed < 1 || parsed > max) {
    throw new Error(`--${name} must be an integer from 1 to ${max}`);
  }
  return parsed;
}

function optionalCoordinate(value, name) {
  if (value === undefined) return undefined;
  if (typeof value === 'boolean' || value === '') throw new Error(`--${name} requires a numeric value`);
  const parsed = Number(value);
  if (!Number.isFinite(parsed) || parsed < 0) {
    throw new Error(`--${name} must be a non-negative finite number`);
  }
  return parsed;
}

export function dragOptionsFromArgs(args) {
  return {
    steps: boundedInteger(args.steps, 'steps', DEFAULT_STEPS, MAX_STEPS),
    timeout: boundedInteger(args.timeout, 'timeout', DEFAULT_TIMEOUT, MAX_TIMEOUT),
    sourceOffset: {
      x: optionalCoordinate(args['from-x'], 'from-x'),
      y: optionalCoordinate(args['from-y'], 'from-y'),
    },
    targetOffset: {
      x: optionalCoordinate(args['to-x'], 'to-x'),
      y: optionalCoordinate(args['to-y'], 'to-y'),
    },
  };
}

function pointInside(box, offset, label) {
  if (!box || ![box.x, box.y, box.width, box.height].every(Number.isFinite) || box.width <= 0 || box.height <= 0) {
    throw new Error(`drag ${label} did not resolve to a visible element with a usable bounding box`);
  }
  const x = offset.x ?? box.width / 2;
  const y = offset.y ?? box.height / 2;
  if (![x, y].every(Number.isFinite) || x < 0 || y < 0 || x >= box.width || y >= box.height) {
    throw new Error(`drag ${label} coordinates must stay inside the selected element`);
  }
  return { x: box.x + x, y: box.y + y };
}

/**
 * Drive the pointer sequence React Flow requires. Both element boxes and all
 * offsets are validated before pointerdown, and a pressed pointer is released
 * if any move/up operation fails.
 */
export async function performPointerDrag({
  page,
  source,
  target,
  sourceOffset = {},
  targetOffset = {},
  steps = DEFAULT_STEPS,
  timeout = DEFAULT_TIMEOUT,
}) {
  if (!source || !target) throw new Error('drag requires source and target locators');
  const safeSteps = boundedInteger(steps, 'steps', DEFAULT_STEPS, MAX_STEPS);
  const safeTimeout = boundedInteger(timeout, 'timeout', DEFAULT_TIMEOUT, MAX_TIMEOUT);

  await Promise.all([
    source.waitFor({ state: 'visible', timeout: safeTimeout }),
    target.waitFor({ state: 'visible', timeout: safeTimeout }),
  ]);
  const [sourceBox, targetBox] = await Promise.all([source.boundingBox(), target.boundingBox()]);
  const from = pointInside(sourceBox, sourceOffset, 'source');
  const to = pointInside(targetBox, targetOffset, 'target');

  let pointerDown = false;
  try {
    await page.mouse.move(from.x, from.y);
    await page.mouse.down({ button: 'left' });
    pointerDown = true;
    await page.mouse.move(to.x, to.y, { steps: safeSteps });
    await page.mouse.up({ button: 'left' });
    pointerDown = false;
  } finally {
    if (pointerDown) {
      try {
        await page.mouse.up({ button: 'left' });
      } catch {
        // Preserve the original drag failure while making a best-effort release.
      }
    }
  }

  return { from, to, steps: safeSteps };
}
