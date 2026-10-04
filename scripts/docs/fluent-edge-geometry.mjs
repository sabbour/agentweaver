// Pure geometry recovered from Agentweaver React renderer at a46721ae77efeba505d2fbfa58c69cdae8b4f3eb.
import { DESIGN_SYSTEM } from './fluent-tokens.mjs';
const CORNER_RADIUS = DESIGN_SYSTEM.connectors.cornerRadiusPx;
function dist(a, b) {
    return Math.hypot(b.x - a.x, b.y - a.y);
}
function segments(points) {
    const result = [];
    for (let index = 0; index < points.length - 1; index += 1) {
        const from = points[index];
        const to = points[index + 1];
        if (Math.abs(from.x - to.x) < 0.5 && Math.abs(from.y - to.y) > 0.5) {
            result.push({
                orientation: 'vertical',
                constant: from.x,
                start: Math.min(from.y, to.y),
                end: Math.max(from.y, to.y),
            });
        }
        else if (Math.abs(from.y - to.y) < 0.5 && Math.abs(from.x - to.x) > 0.5) {
            result.push({
                orientation: 'horizontal',
                constant: from.y,
                start: Math.min(from.x, to.x),
                end: Math.max(from.x, to.x),
            });
        }
    }
    return result;
}
export function findConnectorBridges(routes) {
    const ordered = routes
        .map((route) => ({ ...route, segments: segments(route.points) }))
        .sort((left, right) => left.id.localeCompare(right.id));
    const bridges = new Map();
    for (let current = 1; current < ordered.length; current += 1) {
        for (let prior = 0; prior < current; prior += 1) {
            for (const currentSegment of ordered[current].segments) {
                for (const priorSegment of ordered[prior].segments) {
                    if (currentSegment.orientation === priorSegment.orientation)
                        continue;
                    const horizontal = currentSegment.orientation === 'horizontal' ? currentSegment : priorSegment;
                    const vertical = currentSegment.orientation === 'vertical' ? currentSegment : priorSegment;
                    const x = vertical.constant;
                    const y = horizontal.constant;
                    // A rounded elbow consumes the first CORNER_RADIUS pixels of an
                    // adjoining segment. Leave that clearance plus the overpass radius
                    // so a bridge cannot collapse into a clipped three-quarter circle.
                    const inset = CORNER_RADIUS + 8;
                    if (x <= horizontal.start + inset || x >= horizontal.end - inset ||
                        y <= vertical.start + inset || y >= vertical.end - inset) {
                        continue;
                    }
                    const edgeBridges = bridges.get(ordered[current].id) ?? [];
                    if (!edgeBridges.some((bridge) => Math.abs(bridge.x - x) < 0.5 && Math.abs(bridge.y - y) < 0.5)) {
                        edgeBridges.push({ x, y, orientation: currentSegment.orientation });
                        bridges.set(ordered[current].id, edgeBridges);
                    }
                }
            }
        }
    }
    return bridges;
}
function samePoint(left, right) {
    return Math.abs(left.x - right.x) < 0.5 && Math.abs(left.y - right.y) < 0.5;
}
function sharedSourcePoint(routes) {
    const candidate = routes[0]?.points[0];
    if (!candidate)
        return undefined;
    return routes.every((route) => {
        const point = route.points[0];
        return point !== undefined && samePoint(candidate, point);
    })
        ? candidate
        : undefined;
}
function directionFrom(from, to) {
    if (Math.abs(from.y - to.y) < 0.5 && Math.abs(from.x - to.x) > 0.5) {
        return to.x > from.x ? 'right' : 'left';
    }
    if (Math.abs(from.x - to.x) < 0.5 && Math.abs(from.y - to.y) > 0.5) {
        return to.y > from.y ? 'bottom' : 'top';
    }
    return undefined;
}
function routeDirectionsAt(point, points) {
    const directions = new Set();
    for (let index = 1; index < points.length; index += 1) {
        const from = points[index - 1];
        const to = points[index];
        const forward = directionFrom(from, to);
        if (!forward)
            continue;
        const reverse = directionFrom(to, from);
        if (samePoint(point, from)) {
            directions.add(forward);
        }
        else if (samePoint(point, to)) {
            directions.add(reverse);
        }
        else if ((Math.abs(from.x - to.x) < 0.5 && Math.abs(point.x - from.x) < 0.5 &&
            point.y > Math.min(from.y, to.y) + 0.5 && point.y < Math.max(from.y, to.y) - 0.5) ||
            (Math.abs(from.y - to.y) < 0.5 && Math.abs(point.y - from.y) < 0.5 &&
                point.x > Math.min(from.x, to.x) + 0.5 && point.x < Math.max(from.x, to.x) - 0.5)) {
            directions.add(forward);
            directions.add(reverse);
        }
    }
    return directions;
}
function logicalTeePoints(routes) {
    const tees = new Map();
    for (const route of routes) {
        for (let index = 1; index < route.points.length - 1; index += 1) {
            const point = route.points[index];
            if (routes.some((candidate) => {
                const terminal = candidate.points.at(-1);
                return terminal !== undefined && samePoint(point, terminal);
            }))
                continue;
            const related = routes.filter((candidate) => routeDirectionsAt(point, candidate.points).size > 0);
            if (related.length < 2)
                continue;
            const directions = new Set(related.flatMap((candidate) => [...routeDirectionsAt(point, candidate.points)]));
            if (directions.size < 3)
                continue;
            const key = `${Math.round(point.x * 10)}:${Math.round(point.y * 10)}`;
            const existing = tees.get(key);
            if (!existing || route.id.localeCompare(existing.edgeId) < 0) {
                tees.set(key, { edgeId: route.id, point });
            }
        }
    }
    return [...tees.values()];
}
function pointLiesOnRoute(point, points) {
    for (let index = 1; index < points.length; index += 1) {
        const from = points[index - 1];
        const to = points[index];
        const vertical = Math.abs(from.x - to.x) < 0.5;
        const horizontal = Math.abs(from.y - to.y) < 0.5;
        if ((vertical && Math.abs(point.x - from.x) < 0.5 &&
            point.y >= Math.min(from.y, to.y) - 0.5 && point.y <= Math.max(from.y, to.y) + 0.5) ||
            (horizontal && Math.abs(point.y - from.y) < 0.5 &&
                point.x >= Math.min(from.x, to.x) - 0.5 && point.x <= Math.max(from.x, to.x) + 0.5)) {
            return true;
        }
    }
    return false;
}
export function findConnectorJunctions(routes) {
    const bySource = new Map();
    const byTarget = new Map();
    for (const route of routes) {
        const source = bySource.get(route.source) ?? [];
        source.push(route);
        bySource.set(route.source, source);
        if (!route.loopback) {
            const target = byTarget.get(route.target) ?? [];
            target.push(route);
            byTarget.set(route.target, target);
        }
    }
    const junctions = new Map();
    const claimed = new Set();
    const add = (edgeId, point) => {
        if (!point)
            return;
        const key = `${Math.round(point.x * 10)}:${Math.round(point.y * 10)}`;
        if (claimed.has(key))
            return;
        claimed.add(key);
        const markers = junctions.get(edgeId) ?? [];
        markers.push(point);
        junctions.set(edgeId, markers);
    };
    for (const group of bySource.values()) {
        if (group.length < 2)
            continue;
        const ordered = [...group].sort((left, right) => left.id.localeCompare(right.id));
        add(ordered[0].id, sharedSourcePoint(ordered));
        for (const tee of logicalTeePoints(group))
            add(tee.edgeId, tee.point);
    }
    for (const group of byTarget.values()) {
        if (group.length < 2)
            continue;
        for (const tee of logicalTeePoints(group))
            add(tee.edgeId, tee.point);
    }
    for (const route of routes.filter((route) => route.loopback)) {
        const join = route.points.at(-1);
        const continuation = routes.find((candidate) => !candidate.loopback &&
            candidate.source === (route.returnJoin ?? route.target) &&
            join !== undefined &&
            pointLiesOnRoute(join, candidate.points));
        if (continuation)
            add(route.id, join);
    }
    return junctions;
}
export function buildRoundedPath(points, radius = CORNER_RADIUS) {
    if (points.length === 0)
        return '';
    if (points.length === 1)
        return `M ${points[0].x},${points[0].y}`;
    if (points.length === 2) {
        return `M ${points[0].x},${points[0].y} L ${points[1].x},${points[1].y}`;
    }
    let d = `M ${points[0].x},${points[0].y}`;
    for (let i = 1; i < points.length - 1; i += 1) {
        const p0 = points[i - 1];
        const p1 = points[i];
        const p2 = points[i + 1];
        const d01 = dist(p0, p1);
        const d12 = dist(p1, p2);
        if (d01 === 0 || d12 === 0) {
            d += ` L ${p1.x},${p1.y}`;
            continue;
        }
        const r = Math.min(radius, d01 / 2, d12 / 2);
        const enter = {
            x: p1.x - ((p1.x - p0.x) / d01) * r,
            y: p1.y - ((p1.y - p0.y) / d01) * r,
        };
        const exit = {
            x: p1.x + ((p2.x - p1.x) / d12) * r,
            y: p1.y + ((p2.y - p1.y) / d12) * r,
        };
        d += ` L ${enter.x},${enter.y} Q ${p1.x},${p1.y} ${exit.x},${exit.y}`;
    }
    const last = points[points.length - 1];
    d += ` L ${last.x},${last.y}`;
    return d;
}
export function buildBridgedPath(points, bridges, radius = 7) {
    if (bridges.length === 0)
        return buildRoundedPath(points);
    if (points.length < 2)
        return buildRoundedPath(points);
    let d = `M ${points[0].x},${points[0].y}`;
    for (let index = 0; index < points.length - 1; index += 1) {
        const from = points[index];
        const to = points[index + 1];
        const horizontal = Math.abs(from.y - to.y) < 0.5;
        const vertical = Math.abs(from.x - to.x) < 0.5;
        const segmentBridges = bridges
            .filter((bridge) => (horizontal && bridge.orientation === 'horizontal' && Math.abs(bridge.y - from.y) < 0.5 &&
            bridge.x > Math.min(from.x, to.x) + radius && bridge.x < Math.max(from.x, to.x) - radius) ||
            (vertical && bridge.orientation === 'vertical' && Math.abs(bridge.x - from.x) < 0.5 &&
                bridge.y > Math.min(from.y, to.y) + radius && bridge.y < Math.max(from.y, to.y) - radius))
            .sort((left, right) => horizontal
            ? (to.x >= from.x ? left.x - right.x : right.x - left.x)
            : vertical
                ? (to.y >= from.y ? left.y - right.y : right.y - left.y)
                : 0);
        if (segmentBridges.length === 0) {
            d += ` L ${to.x},${to.y}`;
            continue;
        }
        for (const bridge of segmentBridges) {
            const forward = horizontal ? Math.sign(to.x - from.x) : Math.sign(to.y - from.y);
            const start = horizontal
                ? { x: bridge.x - radius * forward, y: bridge.y }
                : { x: bridge.x, y: bridge.y - radius * forward };
            const end = horizontal
                ? { x: bridge.x + radius * forward, y: bridge.y }
                : { x: bridge.x, y: bridge.y + radius * forward };
            const sweep = horizontal ? (forward > 0 ? 0 : 1) : (forward > 0 ? 1 : 0);
            d += ` L ${start.x},${start.y} A ${radius},${radius} 0 0 ${sweep} ${end.x},${end.y}`;
        }
        d += ` L ${to.x},${to.y}`;
    }
    return d;
}
export function alignLoopbackToContinuation(points, continuation, targetBounds) {
    if (points.length < 4 || !continuation?.length)
        return points;
    const candidate = continuation[1] ?? continuation[0];
    const clearance = 18;
    const join = targetBounds
        ? {
            // Static diagrams flow top-to-bottom. The normal connector leaves
            // here, so this is the local continuation junction rather than any
            // point on the destination card's face or top edge.
            x: continuation[0].x,
            y: targetBounds.y + targetBounds.height + clearance,
        }
        : candidate;
    const sideX = points[1].x;
    return [
        points[0],
        { x: sideX, y: points[0].y },
        { x: sideX, y: join.y },
        join,
    ];
}
