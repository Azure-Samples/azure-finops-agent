// Upload social preview image to GitHub repo settings via headed Playwright.
// Run: node upload-social-preview.mjs
// Persists sign-in to .pw-profile/ so subsequent runs skip the login step.

import { chromium } from 'playwright';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { existsSync } from 'node:fs';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const PROFILE_DIR = path.join(__dirname, '.pw-profile');
const REPO_SETTINGS = 'https://github.com/Azure-Samples/azure-finops-agent/settings';
const IMAGE = path.resolve(__dirname, 'src/Dashboard/frontend/public/og-image.png');

if (!existsSync(IMAGE)) {
    console.error('Image not found:', IMAGE);
    process.exit(1);
}

const ctx = await chromium.launchPersistentContext(PROFILE_DIR, {
    headless: false,
    viewport: { width: 1400, height: 900 },
});
const page = ctx.pages()[0] ?? await ctx.newPage();

console.log('Opening', REPO_SETTINGS);
await page.goto(REPO_SETTINGS, { waitUntil: 'domcontentloaded' });

// If redirected to /login, wait for the user to sign in (up to 3 min).
if (page.url().includes('/login')) {
    console.log('\n=== Sign in to GitHub in the open browser window. ===');
    console.log('Waiting up to 3 minutes for you to land on the settings page...\n');
    await page.waitForURL(/\/Azure-Samples\/azure-finops-agent\/settings/, { timeout: 180_000 });
    console.log('Detected settings page.');
}

// Scroll to the Social preview section (bottom of /settings).
console.log('Locating Social preview section...');
const heading = page.getByRole('heading', { name: /social preview/i });
await heading.waitFor({ state: 'visible', timeout: 30_000 });
await heading.scrollIntoViewIfNeeded();

// Click "Edit" (or "Upload an image..." if no image is set yet).
const editBtn = page.getByRole('button', { name: /^edit$/i }).first();
const uploadBtn = page.getByRole('button', { name: /upload an image/i }).first();

if (await editBtn.isVisible().catch(() => false)) {
    console.log('Clicking Edit...');
    await editBtn.click();
}

// The file picker is wired to a hidden <input type="file">. Set it directly.
console.log('Uploading', IMAGE);
const fileInput = page.locator('input[type="file"]').first();
await fileInput.setInputFiles(IMAGE);

// GitHub auto-saves the upload; give it a moment, then re-read.
console.log('Waiting for save...');
await page.waitForTimeout(4000);

await page.screenshot({ path: path.join(__dirname, '.pw-social-preview-result.png'), fullPage: false });
console.log('Done. Screenshot saved to .pw-social-preview-result.png');

await ctx.close();
