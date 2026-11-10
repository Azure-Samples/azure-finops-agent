import assert from 'node:assert/strict';
import test from 'node:test';
import { DECK_PROMPT, SCRIPT_PROMPT } from '../../src/frontend/src/data/answerActions.js';

// Make a deck asked every conversation, signed-out ones included, for "where we're wasting money and the fixes the
// agent will run"; four of four such decks had no waste to show (session evaluation, November 2026).
test('Make a deck builds from the conversation and proposes next steps instead of promising fixes', () => {
  assert.match(DECK_PROMPT, /from this conversation/);
  assert.match(DECK_PROMPT, /only figures your tools returned/);
  assert.match(DECK_PROMPT, /proposals for us to review/);
  assert.doesNotMatch(DECK_PROMPT, /wasting money|will run/i);
});

test('Write a script still asks before writing and says when there is nothing to script', () => {
  assert.match(SCRIPT_PROMPT, /Ask me to confirm the specific actions before generating the script/);
  assert.match(SCRIPT_PROMPT, /If there are no actionable recommendations yet, let me know/);
});
