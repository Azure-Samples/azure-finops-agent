import assert from 'node:assert/strict';
import test from 'node:test';
import { pricingSections } from '../../src/Dashboard/frontend/src/data/sidebarCategories.js';

const modelVersion =
  /\bgpt[- ]?\d|\b\d+(\.\d+)?-(sol|luna|astra|terra)\b|\b4o\b|\bgrok[- ]\d|\bdeepseek[- ]v?\d|\bllama[- ]\d|\bmistral[- ]\w+[- ]\d|\b(sol|luna|astra|terra)\b/i;

test('signed-out library leads with AI governance and security, then AI pricing', () => {
  assert.deepEqual(
    pricingSections.map((section) => [section.label, section.defaultOpen]),
    [
      ['AI governance & security', true],
      ['AI & LLM pricing', true],
      ['Infrastructure pricing', false],
    ],
  );
});

test('every public section shows three or four short questions', () => {
  for (const section of pricingSections) {
    assert.ok(
      section.prompts.length >= 3 && section.prompts.length <= 4,
      `${section.label} has ${section.prompts.length} questions`,
    );
    for (const { label } of section.prompts)
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
