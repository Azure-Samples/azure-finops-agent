import assert from 'node:assert/strict';
import test from 'node:test';
import { describeTurnFailure, terminalRecoveryState, userFacingStreamError } from '../../src/Dashboard/frontend/src/turnRecovery.js';

const failed = {
  startedUtc: '2026-01-01T00:00:00Z',
  completedUtc: '2026-01-01T00:00:05Z',
  status: 'error',
};

test('a completed failed turn is not still generating', () => {
  const state = terminalRecoveryState(false, [failed], 1);
  assert.equal(state.status, 'error');
  assert.match(state.text, /no longer generating/);
  assert.equal(state.failure.title, "Couldn't complete this answer");
});

test('active turns and incomplete outcome coverage remain recoverable', () => {
  assert.equal(terminalRecoveryState(true, [failed], 1), null);
  assert.equal(terminalRecoveryState(false, [failed], 2), null);
  assert.equal(terminalRecoveryState(false, [], 1), null);
  assert.equal(terminalRecoveryState(false, [{ ...failed, completedUtc: null }], 1), null);
});

test('successful and partially completed turns are not guessed to be empty', () => {
  assert.equal(terminalRecoveryState(false, [{ ...failed, status: 'completed' }], 1), null);
  assert.equal(terminalRecoveryState(false, [{ ...failed, status: 'partial' }], 1), null);
});

test('explicit Stop retains distinct wording', () => {
  const state = terminalRecoveryState(false, [{ ...failed, status: 'stopped' }], 1);
  assert.match(state.text, /You stopped/);
  assert.equal(state.failure, null);
});

test('model authorization is distinguished from tenant sign-in without suggesting secrets', () => {
  const failure = describeTurnFailure('Authentication failed with provider at https://example.invalid/openai/v1/ (HTTP 401). Check your COPILOT_PROVIDER_API_KEY.');
  assert.equal(failure.title, 'AI model access is blocked');
  assert.match(failure.text, /HTTP 401/);
  assert.match(failure.text, /tenant sign-in is separate/);
  assert.match(failure.hint, /inference permissions/);
  assert.doesNotMatch(failure.text, /example.invalid|COPILOT_PROVIDER_API_KEY/);
  assert.equal(userFacingStreamError('Synthetic transport error'), 'Synthetic transport error');
});

test('provider authorization failures are not confused with downstream tool access', () => {
  const forbidden = describeTurnFailure('Authentication failed with provider (HTTP 403)');
  assert.equal(forbidden.title, 'AI model access is blocked');
  assert.match(forbidden.text, /HTTP 403/);
  const downstream = describeTurnFailure('QueryAzure returned HTTP 403 for a resource.');
  assert.notEqual(downstream.title, forbidden.title);
  assert.equal(downstream.text, 'QueryAzure returned HTTP 403 for a resource.');
});

test('missing error details remain honest and actionable', () => {
  const failure = describeTurnFailure('');
  assert.match(failure.text, /turn has ended/);
  assert.match(failure.hint, /pending changes/);
  assert.doesNotMatch(failure.text, /HTTP 401|still generating|underlying problem/);
});
