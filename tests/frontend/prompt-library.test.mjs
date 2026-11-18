import assert from 'node:assert/strict';
import test from 'node:test';
import { maturityCategories, pricingSections } from '../../src/frontend/src/data/sidebarCategories.js';

const modelVersion =
  /\bgpt[- ]?\d|\b\d+(\.\d+)?-(sol|luna|astra|terra)\b|\b4o\b|\bgrok[- ]\d|\bdeepseek[- ]v?\d|\bllama[- ]\d|\bmistral[- ]\w+[- ]\d|\b(sol|luna|astra|terra)\b/i;

test('the start page leads with AI governance, then AI spending, then Azure prices', () => {
  assert.deepEqual(
    pricingSections.map((section) => section.label),
    ['Govern AI agents', 'Control AI spending', 'Compare Azure prices'],
  );
});

test('every public section offers its five most important short questions', () => {
  for (const section of pricingSections) {
    assert.equal(
      section.prompts.length,
      5,
      `${section.label} has ${section.prompts.length} questions`,
    );
    // Signed in, the connected cards fill the same navigation rows.
    for (const { label } of [...section.prompts, ...section.connectedPrompts])
      assert.ok(label.length <= 36, `"${label}" does not fit one sidebar line`);
  }
});

test('public prompts name model brands, never a model version or one model service', () => {
  for (const section of pricingSections)
    for (const { label, prompt } of [...section.prompts, ...section.connectedPrompts]) {
      assert.doesNotMatch(label, modelVersion, label);
      assert.doesNotMatch(prompt, modelVersion, label);
      assert.doesNotMatch(`${label} ${prompt}`, /azure openai/i, label);
    }
});

// Users of the agent-inventory card could not follow its steps, and a system-prompt rule alone did not get the links
// written (three quality reports, November 2026), so the card itself asks for them.
test('the agent inventory card asks for each step\'s Microsoft Learn link', () => {
  const card = pricingSections.flatMap((section) => section.prompts)
    .find(({ label }) => label === 'How do I find all our AI agents?');
  assert.match(card.prompt, /Microsoft tool \(linked to its Microsoft Learn page\)/);
});

// A best-value answer named its pick with only the token mix, not the score-per-USD measure behind it, which the
// session evaluation had found users could not check (November 2026).
test('the best-value card asks for the measure its pick is ranked by', () => {
  const card = pricingSections.flatMap((section) => section.prompts)
    .find(({ label }) => label === 'Which AI model is best value?');
  assert.match(card.prompt, /name the best-value pick and the measure it is ranked by\.$/);
});

// Pricing the user's own configuration was the most common typed request in the November 2026 audit and no card offered
// it; the card gathers every input in one message so the next reply is the priced table.
test('the own-setup card asks for every input in one message before it looks up any price', () => {
  const card = pricingSections.flatMap((section) => section.prompts)
    .find(({ label }) => label === 'Price my own setup');
  assert.match(card.prompt, /^Price my own Azure setup like the Azure pricing calculator\. Before looking up any price, ask me in one short message for everything you need/);
  assert.match(card.prompt, /say which default you will use for anything I leave out/);
});

// Signed in, the third section serves the user's own estate: its title says so, and its savings card sends the Crawl
// prompt word for word, so the deployment gate keeps evaluating it as one case.
test('signed in, the price section is titled for the user\'s estate and leads with the gate-tested savings prompt', () => {
  const section = pricingSections.find((candidate) => candidate.key === 'infrastructure-pricing');
  assert.equal(section.connectedLabel, 'Optimize my Azure costs');
  assert.deepEqual(section.connectedPrompts.slice(0, 2).map(({ label }) => label), ['Where can I save money?', 'FinOps assessment report']);
  const crawl = maturityCategories.find((category) => category.key === 'crawl').prompts
    .find(({ label }) => label === 'Score Crawl maturity');
  assert.equal(section.connectedPrompts[0].prompt, crawl.prompt);
});
