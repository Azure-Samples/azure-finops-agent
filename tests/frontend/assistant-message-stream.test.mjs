import assert from 'node:assert/strict';
import test from 'node:test';
import { createAssistantMessageStream } from '../../src/Dashboard/frontend/src/assistantMessageStream.js';

test('authoritative completion replaces only its own deltas', () => {
  const stream = createAssistantMessageStream();
  stream.append('Partial cost', 'answer');
  assert.equal(stream.complete('Complete cost table', 'answer'), 'Complete cost table');
  stream.toolBoundary();
  stream.append('Follow-', 'follow-up');
  assert.equal(stream.complete('Follow-up link', 'follow-up'), 'Complete cost table\n\nFollow-up link');
});

test('empty messages, duplicate completions and late deltas cannot erase or duplicate an answer', () => {
  const stream = createAssistantMessageStream();
  stream.complete('Cost table', 'answer');
  stream.complete('Cost table', 'answer');
  stream.append('late fragment', 'answer');
  stream.complete(' \n ', 'empty-tool-message');
  assert.equal(stream.text(), 'Cost table');
});

test('legacy events remain compatible across tool boundaries', () => {
  const stream = createAssistantMessageStream();
  stream.append('Partial');
  stream.complete('Complete table');
  stream.toolBoundary();
  stream.append('Follow-up');
  assert.equal(stream.complete('Follow-up'), 'Complete table\n\nFollow-up');
});

test('a final message can attach an ID to preceding legacy deltas', () => {
  const stream = createAssistantMessageStream();
  stream.append('Partial');
  assert.equal(stream.complete('Full answer', 'message-id'), 'Full answer');
});

test('interleaved messages preserve order and exact identifiers', () => {
  const stream = createAssistantMessageStream();
  stream.append('MDE.', 'first');
  stream.append('gpt-5.', 'second');
  stream.append('Linux', 'first');
  stream.complete('gpt-5.6-luna', 'second');
  assert.equal(stream.text(), 'MDE.Linux\n\ngpt-5.6-luna');
});
