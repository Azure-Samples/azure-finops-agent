import test from 'node:test';
import assert from 'node:assert/strict';
import { formatAgo, formatUntil } from '../../src/frontend/src/lib/timeFormat.js';

const now = Date.parse('2026-10-27T12:00:00Z');
const ago = (ms) => new Date(now - ms).toISOString();
const ahead = (ms) => new Date(now + ms).toISOString();
const MIN = 60000;

test('formatAgo describes past timestamps from minutes to months', () => {
  assert.equal(formatAgo(ago(20000), now), 'just now');
  assert.equal(formatAgo(ago(5 * MIN), now), '5m ago');
  assert.equal(formatAgo(ago(3 * 60 * MIN), now), '3h ago');
  assert.equal(formatAgo(ago(2 * 24 * 60 * MIN), now), '2d ago');
  assert.equal(formatAgo(ago(65 * 24 * 60 * MIN), now), '2mo ago');
});

test('formatUntil describes future timestamps', () => {
  assert.equal(formatUntil(ahead(10000), now), 'any moment');
  assert.equal(formatUntil(ahead(4 * MIN), now), 'in 4 min');
  assert.equal(formatUntil(ahead(3 * 60 * MIN), now), 'in 3 h');
  assert.equal(formatUntil(ahead(3 * 24 * 60 * MIN), now), 'in 3 d');
});

test('missing or invalid timestamps render nothing', () => {
  for (const value of [null, undefined, '', 'not a date']) {
    assert.equal(formatAgo(value, now), '');
    assert.equal(formatUntil(value, now), '');
  }
});
