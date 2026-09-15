import { test, expect } from '@playwright/test';

const sessionId = 'synthetic-conversation';
const artifactId = '11111111111111111111111111111111';
const operationId = '22222222222222222222222222222222';
const change = {
  operationId, method: 'PATCH', target: '/synthetic/resource/with-a-long-name-for-mobile-layout',
  body: '{"tags":{"Owner":"Synthetic team"}}', status: 'awaitingApproval',
  costImpact: 'Review the configuration and potential charges before approving.',
};

async function arrange(page, chatEvents, history = { messages: [] }) {
  const requests = [];
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.addInitScript(() => {
    if (window.top === window) localStorage.setItem('addons-tour-shown', '1');
  });
  await page.route('**/auth/me', route => route.fulfill({ json: { id: 101, login: 'synthetic-user', name: 'Synthetic user' } }));
  await page.route('**/auth/azure/**', route => route.fulfill({ json: { connected: false, tenants: [] } }));
  await page.route('**/api/**', async route => {
    const path = new URL(route.request().url()).pathname;
    if (path === '/api/chat') {
      requests.push(route.request().postDataJSON());
      const payload = [{ type: 'session', id: sessionId }, ...chatEvents];
      return route.fulfill({ contentType: 'text/event-stream', body: payload.map(event => `data: ${JSON.stringify(event)}\n\n`).join('') + 'data: [DONE]\n\n' });
    }
    if (path === '/api/version') return route.fulfill({ json: { sha: 'test', build: 'test', branch: 'test' } });
    if (path === '/api/config') return route.fulfill({ json: {} });
    if (path === '/api/models') return route.fulfill({ json: { models: [], defaultModel: 'synthetic' } });
    if (path === '/api/sessions/new') return route.fulfill({ json: { sessionId } });
    if (path === '/api/sessions') return route.fulfill({ json: { sessions: [], currentSessionId: sessionId } });
    if (path.endsWith('/messages')) return route.fulfill({ json: history });
    if (path.endsWith('/active')) return route.fulfill({ json: { active: false } });
    if (path === '/api/jobs') return route.fulfill({ json: { jobs: [], entraRequired: true } });
    if (path.endsWith('/approve')) return route.fulfill({ json: { result: { status: 'accepted', nextAction: 'Check the operation for terminal state.' } } });
    if (path.endsWith('/reject')) return route.fulfill({ json: { rejected: true } });
    if (path.startsWith('/api/download/')) return route.fulfill({ contentType: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet', headers: { 'content-disposition': 'attachment; filename="synthetic.xlsx"' }, body: 'synthetic-test-file' });
    return route.fulfill({ json: {} });
  });
  await page.goto('/');
  await expect(page.locator('textarea')).toBeEnabled();
  return { requests, errors };
}

async function send(page, prompt = 'make an Excel file') {
  await expect(page.locator('.action-btn--stop')).toHaveCount(0);
  await expect(page.locator('textarea')).toBeEnabled();
  await page.locator('textarea').fill(prompt);
  await page.locator('textarea').press('Enter');
  await expect(page.locator('.action-btn--stop')).toHaveCount(0);
}

test('short follow-up renders a real spreadsheet download without HTML preview', async ({ page }, testInfo) => {
  const { requests, errors } = await arrange(page, [
    { type: 'delta', content: 'The requested workbook is ready.' },
    { type: 'html_ready', fileId: artifactId, fileName: 'synthetic.xlsx', slideCount: '2 rows (XLSX)' },
  ]);
  await send(page);
  const link = page.locator('a[download="synthetic.xlsx"]').last();
  await expect(link).toBeVisible();
  await expect(link).toHaveAttribute('href', `/api/download/file/${artifactId}`);
  await expect(page.locator('.html-deck-card-btn--preview')).toHaveCount(0);
  expect(requests[0].prompt).toBe('make an Excel file');
  expect(Array.isArray(requests[0].fileIds)).toBeTruthy();
  await page.screenshot({ path: testInfo.outputPath('download.png'), animations: 'disabled' });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy();
  expect(errors).toEqual([]);
});

test('consent actions cannot navigate to tool-supplied external URLs', async ({ page }) => {
  await arrange(page, [
    { type: 'consent_required', actions: [{ label: 'ignored', href: '/auth/microsoft?tier=loganalytics' }, { label: 'unsafe', href: 'https://attacker.invalid/' }] },
    { type: 'delta', content: 'Log Analytics consent is required.' },
  ]);
  await send(page, 'List the Syslog machines');
  await expect(page.getByRole('link', { name: 'Grant Log Analytics access' })).toHaveAttribute('href', '/auth/microsoft?tier=loganalytics');
  await expect(page.locator('a[href^="https://attacker.invalid"]')).toHaveCount(0);
});

test('HTML report preview preserves contrast and stays isolated', async ({ page }, testInfo) => {
  const { errors } = await arrange(page, [
    { type: 'delta', content: 'The report is ready.' },
    { type: 'html_ready', fileId: artifactId, fileName: 'synthetic.html', slideCount: '2 rows (HTML)' },
  ]);
  await page.route('**/api/download/html/**', route => route.fulfill({ contentType: 'text/html', body: '<!doctype html><html><head><style>body{background:#f7f4ef;color:#242424;margin:24px;font:14px sans-serif}table{width:100%}</style></head><body><h1>Synthetic report</h1><label for="filter">Filter rows</label><input id="filter"><table><tbody><tr><td>Jan</td><td>30</td></tr><tr><td>Feb</td><td>30</td></tr></tbody></table><script>parent.document.body.dataset.compromised="true"</script></body></html>' }));
  await send(page, 'Make an HTML report');
  await page.locator('.html-deck-card-btn--preview').last().click();
  const iframe = page.locator('.deck-preview-frame');
  await expect(iframe).toBeVisible();
  await expect(iframe).toHaveAttribute('sandbox', '');
  const frame = page.frameLocator('.deck-preview-frame');
  await expect(frame.locator('h1')).toHaveText('Synthetic report');
  await expect(frame.locator('body')).toHaveCSS('background-color', 'rgb(247, 244, 239)');
  await expect(frame.locator('body')).toHaveCSS('color', 'rgb(36, 36, 36)');
  await expect(frame.locator('input, script')).toHaveCount(0);
  expect(await page.locator('body').getAttribute('data-compromised')).toBeNull();
  await page.screenshot({ path: testInfo.outputPath('html-preview.png'), animations: 'disabled' });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy();
  expect(errors).toEqual([]);
});

test('approval requires explicit acknowledgement and sends only the opaque identifier', async ({ page }, testInfo) => {
  const { errors } = await arrange(page, [
    { type: 'approval_required', change }, { type: 'delta', content: 'The change is awaiting your review. No write was sent.' },
  ]);
  await send(page, 'Apply this tag');
  await page.locator('.change-review summary').click();
  const approve = page.getByRole('button', { name: 'Approve change', exact: true });
  await expect(approve).toBeDisabled();
  await page.getByRole('checkbox', { name: 'I reviewed this change and its potential charges' }).check();
  await expect(approve).toBeEnabled();
  await page.screenshot({ path: testInfo.outputPath('approval.png'), animations: 'disabled' });
  const sent = page.waitForRequest(request => request.url().endsWith(`/api/changes/${operationId}/approve`));
  await approve.click();
  expect((await sent).postDataJSON()).toEqual({ acknowledgeCostImpact: true });
  await expect(page.locator('.change-review summary')).toContainText('accepted');
  await expect(page.getByRole('button', { name: 'Check operation' })).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy();
  expect(errors).toEqual([]);
});

test('new conversation does not throw or restore unrelated proposals', async ({ page }) => {
  const { errors } = await arrange(page, [{ type: 'delta', content: 'Synthetic answer.' }]);
  const removedFiles = [];
  page.on('request', request => {
    if (request.method() === 'DELETE' && request.url().includes('/api/uploads/')) removedFiles.push(request.url());
  });
  await page.route('**/api/upload', route => route.fulfill({ json: { files: [{ ok: true, fileId: '111111111111', fileName: 'synthetic.csv', kind: 'csv', sizeBytes: 7 }] } }));
  await page.locator('input[type="file"]').first().setInputFiles({ name: 'synthetic.csv', mimeType: 'text/csv', buffer: Buffer.from('cost\n1\n') });
  await expect(page.getByText('synthetic.csv', { exact: true }).first()).toBeVisible();
  await send(page, 'Synthetic question');
  await expect(page.getByText('Synthetic answer.', { exact: true })).toBeVisible();
  await page.getByTitle('Clear chat', { exact: true }).click();
  await expect(page.locator('textarea')).toBeEnabled();
  await expect(page.locator('.change-review')).toHaveCount(0);
  expect(removedFiles).toEqual([]);
  expect(errors).toEqual([]);
});