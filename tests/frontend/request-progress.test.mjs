import assert from 'node:assert/strict';
import test from 'node:test';
import { createRequestProgress, describeRequestProgress, toolResultSucceeded } from '../../src/Dashboard/frontend/src/requestProgress.js';

const now = Date.parse('2026-01-01T00:00:00Z');

test('countdown uses the server deadline, not a frozen original wait', () => {
  const state = createRequestProgress({
    status: 429, waitSeconds: 37, willRetry: true, retryAtUtc: '2026-01-01T00:00:37Z',
  }, now, 'Cost Management');
  assert.equal(describeRequestProgress(state, now).remaining, 37);
  assert.equal(describeRequestProgress(state, now + 12_000).remaining, 25);
  assert.equal(describeRequestProgress(state, now + 40_000).remaining, 0);
  assert.match(describeRequestProgress(state, now + 40_000).title, /waiting for the retry response/);
  assert.equal(describeRequestProgress(state, now).phase, 'cooldown');
  assert.equal(describeRequestProgress(state, now).percent, 0);
  assert.equal(describeRequestProgress(state, now + 12_000).percent, 32);
  assert.equal(describeRequestProgress(state, now + 40_000).phase, 'retry-wait');
  assert.equal(describeRequestProgress(state, now + 40_000).percent, null);
  assert.equal(describeRequestProgress(state, now + 40_000).sidebarLabel, 'Waiting');
});

test('expired terminal cooldown does not promise an automatic retry', () => {
  const state = createRequestProgress({ status: 429, waitSeconds: 37, willRetry: false }, now);
  const view = describeRequestProgress(state, now + 60_000);
  assert.match(view.title, /automatic retry stopped/);
  assert.match(view.detail, /new request/);
  assert.doesNotMatch(view.detail, /retrying automatically/);
  assert.equal(view.phase, 'stopped');
  assert.equal(view.percent, null);
  assert.equal(view.sidebarLabel, 'Stopped');
});

test('slow heartbeat and transient server failures are not labelled as throttling', () => {
  const slow = describeRequestProgress(createRequestProgress({ status: 0, waitSeconds: 6 }, now), now);
  assert.equal(slow.badge, 'Waiting');
  assert.equal(slow.remaining, null);
  assert.equal(slow.phase, 'slow');
  assert.equal(slow.percent, null);
  assert.doesNotMatch(slow.title, /rate limit|cooling|HTTP 0/i);
  const unavailable = describeRequestProgress(createRequestProgress({ status: 503, waitSeconds: 5 }, now), now);
  assert.match(unavailable.detail, /temporarily unavailable/);
  assert.equal(unavailable.phase, 'cooldown');
  assert.equal(unavailable.badge, 'HTTP 503');
  assert.doesNotMatch(unavailable.heading, /billing|rate limit/i);
});

test('background time and extended retry deadlines are reflected immediately', () => {
  const initial = createRequestProgress({ status: 429, waitSeconds: 37 }, now);
  assert.equal(describeRequestProgress(initial, now + 30_000).remaining, 7);
  const extended = createRequestProgress({ status: 429, waitSeconds: 60, retryAtUtc: '2026-01-01T00:01:30Z' }, now + 30_000);
  assert.equal(describeRequestProgress(extended, now + 35_000).remaining, 55);
  assert.equal(describeRequestProgress(extended, now + 35_000).percent, 8);
});

test('the exact supplied deadline wins over a shorter wait and keeps its original offset', () => {
  const retryAtUtc = '2026-01-01T01:10:00.125+01:00';
  const state = createRequestProgress({ status: 429, waitSeconds: 1, retryAtUtc }, now);
  assert.equal(state.retryAtUtc, retryAtUtc);
  assert.equal(state.deadline, Date.parse(retryAtUtc));
  assert.equal(state.startedAt, now);
  const view = describeRequestProgress(state, now);
  assert.equal(view.retryAtUtc, retryAtUtc);
  assert.equal(view.remaining, 601);
  assert.equal(view.countdown, '10m 01s');
  assert.equal(view.percent, 0);
  assert.equal(describeRequestProgress(state, state.deadline - 1).remaining, 1);
  assert.equal(describeRequestProgress(state, state.deadline - 1).percent, 99);
  assert.equal(describeRequestProgress(state, state.deadline).phase, 'retry-wait');
});

test('billing throttling explains the shared service without implying changes or savings', () => {
  const state = createRequestProgress({
    status: 429, waitSeconds: 37, url: '/providers/Microsoft.CostManagement/query',
  }, now, 'Azure');
  const view = describeRequestProgress(state, now);
  assert.match(view.heading, /billing API is catching its breath/);
  assert.match(view.explanation, /Azure rate-limits its shared billing service/);
  assert.match(view.explanation, /honor its retry deadline/);
  assert.match(view.resourceNote, /does not stop your Azure resources or reduce their charges/);
  assert.equal(describeRequestProgress(state, now + 12_000).heading, view.heading);
  assert.equal(describeRequestProgress(state, now + 12_000).guidance, view.guidance);
  assert.equal(view.sidebarLabel, '37s');
});

test('generic service throttling does not claim to be the Azure billing API', () => {
  const view = describeRequestProgress(createRequestProgress({
    status: 429, waitSeconds: 20, tool: 'graph', url: '/v1.0/users',
  }, now, 'Microsoft Graph'), now);
  assert.match(view.explanation, /Microsoft Graph is rate limited/);
  assert.doesNotMatch(view.heading + view.detail, /billing|Azure/);
  assert.equal(view.resourceNote, '');
});

test('terminal failures never show retry progress before or after their deadline', () => {
  for (const status of [429, 503]) {
    const state = createRequestProgress({ status, waitSeconds: 37, willRetry: false }, now);
    for (const offset of [0, 12_000, 37_000, 300_000]) {
      const view = describeRequestProgress(state, now + offset);
      assert.equal(view.phase, 'stopped');
      assert.equal(view.percent, null);
      assert.equal(view.heading, 'Automatic retry has stopped');
      assert.equal(view.sidebarLabel, 'Stopped');
      assert.doesNotMatch(view.guidance, /retrying automatically|waiting for the.*result/i);
    }
  }
});

test('an explicit terminal stop takes precedence even without an HTTP status', () => {
  const view = describeRequestProgress(createRequestProgress({
    status: 0, waitSeconds: 6, willRetry: false,
  }, now), now);
  assert.equal(view.phase, 'stopped');
  assert.equal(view.badge, 'Stopped');
  assert.equal(view.remaining, null);
  assert.equal(view.percent, null);
  assert.doesNotMatch(view.detail, /HTTP 0|still running/);
});

test('missing deadlines fall back to the full wait and progress never becomes negative', () => {
  const state = createRequestProgress({ status: 429, waitSeconds: 90, retryAtUtc: 'invalid' }, now);
  assert.equal(state.deadline, now + 90_000);
  assert.equal(state.retryAtUtc, '2026-01-01T00:01:30.000Z');
  assert.equal(describeRequestProgress(state, now).countdown, '1m 30s');
  assert.equal(describeRequestProgress(state, now - 5000).percent, 0);
});

test('invalid wait values do not invent a countdown or throw when formatting the deadline', () => {
  for (const waitSeconds of [undefined, 'invalid', Infinity, -1]) {
    const state = createRequestProgress({ status: 429, waitSeconds }, now);
    assert.equal(state.deadline, now);
    assert.equal(describeRequestProgress(state, now).phase, 'retry-wait');
  }
});

test('SDK success cannot turn an HTTP failure into a green success indicator', () => {
  assert.equal(toolResultSucceeded(true, 'HTTP 400 BadRequest\n{}'), false);
  assert.equal(toolResultSucceeded(true, 'HTTP 429 TooManyRequests\n{}'), false);
  assert.equal(toolResultSucceeded(true, 'HTTP 503 ServiceUnavailable\n{}'), false);
  assert.equal(toolResultSucceeded(false, 'HTTP 200 OK\n{}'), false);
  assert.equal(toolResultSucceeded(true, 'HTTP 200 OK\n{}'), true);
});

function bulkResult(overrides = {}) {
  return {
    total: 2, succeeded: 2, failed: 0, pending: 0, unattempted: 0, cancelled: 0,
    stopped: false, complete: true,
    results: [
      { index: 0, status: 200, outcome: 'succeeded', partial: false, body: { cost: 12 } },
      { index: 1, status: 200, outcome: 'succeeded', partial: false, body: { cost: 34 } },
    ],
    ...overrides,
  };
}

test('SDK success does not hide failed or cancelled bulk requests', () => {
  for (const counter of ['failed', 'cancelled']) {
    const result = bulkResult({ succeeded: 1, [counter]: 1, complete: false });
    assert.equal(toolResultSucceeded(true, JSON.stringify(result)), false, counter);
    assert.equal(toolResultSucceeded(true, result), false, counter);
    assert.equal(toolResultSucceeded(true, { ...result, complete: true }), false, counter);
  }
});

test('pending, unattempted, stopped, and incomplete bulk requests are not successful', () => {
  for (const overrides of [
    { succeeded: 1, pending: 1 },
    { succeeded: 1, unattempted: 1 },
    { stopped: true },
    { complete: false },
    { complete: undefined },
  ]) {
    assert.equal(toolResultSucceeded(true, JSON.stringify(bulkResult(overrides))), false, JSON.stringify(overrides));
  }
});

test('bulk completion requires accounted results and consistent counters', () => {
  for (const overrides of [
    { succeeded: 1 },
    { succeeded: 3 },
    { succeeded: '2' },
    { failed: -1 },
    { cancelled: '1' },
    { pending: null },
    { total: 3 },
    { results: [] },
  ]) {
    assert.equal(toolResultSucceeded(true, JSON.stringify(bulkResult(overrides))), false, JSON.stringify(overrides));
  }
});

test('bulk row evidence prevents pending operations and partial data from looking complete', () => {
  for (const row of [
    { status: 202, outcome: 'accepted', partial: false },
    { status: 409, outcome: 'awaitingApproval', partial: false },
    { status: 200, outcome: 'inProgress', partial: false },
    { status: 200, outcome: 'unknown', partial: false },
    { status: 429, outcome: 'failed', partial: false },
    { status: 0, outcome: 'cancelled', partial: false },
    { status: 200, outcome: 'succeeded', partial: true },
    { status: 202, outcome: 'succeeded', partial: false },
    null,
  ]) {
    const result = bulkResult();
    result.results[1] = row;
    assert.equal(toolResultSucceeded(true, JSON.stringify(result)), false, JSON.stringify(row));
  }
});

test('fully completed bulk responses remain successful only when the SDK also succeeded', () => {
  const result = bulkResult();
  assert.equal(toolResultSucceeded(true, result), true);
  assert.equal(toolResultSucceeded(true, JSON.stringify(result)), true);
  assert.equal(toolResultSucceeded(false, result), false);
  assert.equal(toolResultSucceeded(false, JSON.stringify(result)), false);
});

test('HTTP-wrapped bulk results retain both transport and batch completion checks', () => {
  const complete = JSON.stringify(bulkResult());
  const incomplete = JSON.stringify(bulkResult({ failed: 1, complete: false }));
  assert.equal(toolResultSucceeded(true, `HTTP 200 OK\n${complete}`), true);
  assert.equal(toolResultSucceeded(true, `HTTP 200 OK\n${incomplete}`), false);
  assert.equal(toolResultSucceeded(true, `HTTP 202 Accepted\n${complete}`), false);
  assert.equal(toolResultSucceeded(true, `HTTP 400 BadRequest\n${complete}`), false);
  assert.equal(toolResultSucceeded(true, `HTTP 429 TooManyRequests\n${complete}`), false);
});

test('ordinary financial JSON is not mistaken for a bulk envelope by its total', () => {
  for (const result of [
    { total: 200 },
    { total: 200, failed: 3, cancelled: 1 },
    { total: 200, complete: false },
    { total: 2, results: [{ cost: 12 }, { cost: 34 }] },
    { total: 12.34, complete: false, results: [{ cost: 12.34 }] },
    { total: 2, failed: 1, results: { cost: 12 } },
    { properties: { total: 2, failed: 1, results: [] } },
    [bulkResult({ failed: 1, complete: false })],
    null,
    200,
  ]) {
    assert.equal(toolResultSucceeded(true, result), true, JSON.stringify(result));
    assert.equal(toolResultSucceeded(true, JSON.stringify(result)), true, JSON.stringify(result));
    assert.equal(toolResultSucceeded(false, result), false, JSON.stringify(result));
  }
  assert.equal(toolResultSucceeded(true, 'A normal tool response.'), true);
});
