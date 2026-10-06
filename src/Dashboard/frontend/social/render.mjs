// Renders the 1200 x 630 social sharing card (public/og-image.png) from
// social/og-image.html with the app's self-hosted font. Run: npm run og-image
// After changing the card, bump the ?v= query on og:image and twitter:image in
// index.html so LinkedIn, X and other platforms fetch the new image.
import { chromium } from "@playwright/test";
import { fileURLToPath } from "node:url";

const source = new URL("./og-image.html", import.meta.url);
const output = fileURLToPath(new URL("../public/og-image.png", import.meta.url));

const browser = await chromium.launch();
try {
  const page = await browser.newPage({
    viewport: { width: 1200, height: 630 },
    deviceScaleFactor: 1,
  });
  await page.goto(source.href);
  const fontLoaded = await page.evaluate(
    async () => (await document.fonts.load('700 84px "Google Sans Flex"')).length > 0,
  );
  if (!fontLoaded) throw new Error("Google Sans Flex did not load; check the font path in og-image.html.");
  await page.evaluate(() => Promise.all([...document.images].map((image) => image.decode())));
  await page.screenshot({ path: output });
  console.log(`Wrote ${output}`);
} finally {
  await browser.close();
}
