const fs = require('node:fs');
const path = require('node:path');
const { createRequire } = require('node:module');
const root = path.resolve(__dirname, '..', '..', '..', '..', '..');
const appRequire = createRequire(path.join(root, 'apps', 'web', 'package.json'));
const { chromium } = appRequire('playwright');
async function main() {
  const browser = await chromium.launch({ headless: true });
  try {
    const page = await browser.newPage();
    const labels = ['revise', 'safety-failed', 'no-changes', 'review', 'request-changes',
      'declined', 'approved', 'blocked', 'merged'];
    const metrics = await page.evaluate(labels => {
      const context = document.createElement('canvas').getContext('2d');
      context.font = '600 18px "Segoe UI"';
      return Object.fromEntries(labels.map(label => [label, context.measureText(label).width]));
    }, labels);
    fs.writeFileSync(path.join(__dirname, 'label-metrics.json'), JSON.stringify(metrics, null, 2));
    fs.writeFileSync(path.join(__dirname, 'font-check.json'), JSON.stringify({
      browser: await browser.version(),
      semiboldInstalled: fs.existsSync('C:\\Windows\\Fonts\\seguisb.ttf'),
      regularInstalled: fs.existsSync('C:\\Windows\\Fonts\\segoeui.ttf'),
      font: 'Segoe UI', title: '600 28px', label: '600 18px', metrics,
    }, null, 2));
    console.log(metrics);
  } finally {
    await browser.close();
  }
}
main().catch(error => { console.error(error); process.exitCode = 1; });
