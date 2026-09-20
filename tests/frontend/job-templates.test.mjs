import assert from 'node:assert/strict';
import test from 'node:test';
import { JOB_TEMPLATES } from '../../src/Dashboard/frontend/src/data/jobTemplates.js';

test('capacity monitoring preserves unknowns and does not promise allocation', () => {
  const prompt = JOB_TEMPLATES.find(t => t.label === 'Check capacity of X').prompt;
  assert.match(prompt, /not a capacity guarantee/);
  assert.match(prompt, /unknown/);
});

test('reservation template requires configured inputs and explicit application approval', () => {
  const prompt = JOB_TEMPLATES.find(t => t.label === 'Reserve X when available').prompt;
  assert.match(prompt, /explicit approval in the application/);
  assert.match(prompt, /Do not purchase or reserve unattended/);
  assert.doesNotMatch(prompt, /finops-capacity|secure it immediately/);
});

test('one-minute probe cannot repeatedly query billing', () => {
  const template = JOB_TEMPLATES.find(t => t.label === '1-min test');
  assert.equal(template.interval, 1);
  assert.match(template.prompt, /Do not list subscriptions again or call Cost Management query\/forecast/);
  assert.doesNotMatch(template.prompt, /consumption in USD/);
});
