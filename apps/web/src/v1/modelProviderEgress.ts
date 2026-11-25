import type { ModelProviderEgressStatus } from './contracts';

export function hostRequestStatusLabel(egress?: ModelProviderEgressStatus | null): string {
  if (!egress) return 'Status unavailable';
  if (egress.deliveryStatus === 'pending' && egress.destinationState === null)
    return 'Waiting to send';
  if (egress.deliveryStatus === 'delivered' && egress.destinationState === 'requested')
    return 'Received by Environment';
  if (egress.deliveryStatus === 'failed' && egress.destinationState === null)
    return 'Delivery failed';
  return 'Status unavailable';
}

export function hostRequestHostText(egress?: ModelProviderEgressStatus | null): string {
  const host = typeof egress?.host === 'string' ? egress.host.trim() : '';
  const labels = host.split('.');
  const safe = host.length <= 253 &&
    labels.length >= 2 &&
    labels.every((label) => /^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$/.test(label)) &&
    !/^\d+$/.test(labels.at(-1) ?? '');
  return safe ? host : 'Unavailable';
}

export function hostRequestPortText(egress?: ModelProviderEgressStatus | null): string {
  return egress && Number.isSafeInteger(egress.port) && egress.port > 0
    ? String(egress.port)
    : 'Unavailable';
}
