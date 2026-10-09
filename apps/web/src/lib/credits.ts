// Single source of truth for converting AI credits (AIC) to a USD estimate.
//
// Confirmed rate (gh-aw AI Credits spec): 1 AIC = $0.01, i.e. USD = AIC × 0.01.
// Change this one constant to update the conversion everywhere.
export const USD_PER_AI_CREDIT = 0.01;

/** True when a real USD conversion rate has been configured. */
export function hasUsdRate(): boolean {
  return Number.isFinite(USD_PER_AI_CREDIT) && USD_PER_AI_CREDIT > 0;
}

/** Formats an AI-credit amount as a USD string using the single shared rate. */
export function formatUsd(credits: number): string {
  const usd = (Number.isFinite(credits) ? credits : 0) * USD_PER_AI_CREDIT;
  // Guard against a rounded-to-zero display: a genuinely nonzero but sub-$0.0001 value would
  // otherwise render as "$0.0000", which reads as free. Show an explicit lower bound instead.
  if (usd > 0 && usd < 0.0001) return '< $0.0001';
  return usd.toLocaleString(undefined, {
    style: 'currency',
    currency: 'USD',
    minimumFractionDigits: 2,
    maximumFractionDigits: usd > 0 && usd < 1 ? 4 : 2,
  });
}

/** Converts nano-AIU (the raw backend unit) to whole AI credits. */
export function nanoAiuToCredits(nanoAiu: number | null | undefined): number {
  if (nanoAiu == null || !Number.isFinite(nanoAiu)) return 0;
  return nanoAiu / 1_000_000_000;
}
