import assert from 'node:assert/strict';
import test from 'node:test';
import { stripCitationMarkers } from '../../src/Dashboard/frontend/src/modelText.js';

test('web search citation markers are removed from answers', () => {
  assert.equal(
    stripCitationMarkers('Retail rates, not invoice prices.* \uE200cite\uE202turn0search0\uE201  \n*Retrieved*'),
    'Retail rates, not invoice prices.*  \n*Retrieved*');
  assert.equal(
    stripCitationMarkers('Launched\uE200cite\uE202turn0search0\uE202turn1view0\uE201.'),
    'Launched.');
});

test('a streaming buffer that ends inside a marker hides the partial marker', () => {
  assert.equal(stripCitationMarkers('Partial answer \uE200cite\uE202turn0'), 'Partial answer');
  assert.equal(stripCitationMarkers('Partial answer \uE200'), 'Partial answer');
});

test('ordinary text and identifiers are returned untouched', () => {
  const plain = 'Standard_D4s_v5 costs USD 0.192/hour (gpt-6.1-sol, `eastus`) — 2 × 730 h.';
  assert.equal(stripCitationMarkers(plain), plain);
  assert.equal(stripCitationMarkers(''), '');
  assert.equal(stripCitationMarkers(null), null);
});
