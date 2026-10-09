import assert from 'node:assert/strict';
import test from 'node:test';
import { groupChats, withQuestionTitle } from '../../src/Dashboard/frontend/src/chatGroups.js';

const now = new Date(2026, 9, 8, 15, 0, 0);
const at = (days, hours = 0) =>
  new Date(now.getTime() - days * 86400000 - hours * 3600000).toISOString();

const sessions = [
  { id: 'old', summary: 'Reservation review', modified: at(40) },
  { id: 'today-late', summary: 'Why did my VM costs rise?', modified: at(0, 1) },
  { id: 'yesterday', summary: 'Cheapest region for a D4s v5', modified: at(1) },
  { id: 'week', summary: 'Budget guard setup', modified: at(4) },
  { id: 'today-early', summary: null, modified: at(0, 5) },
];

test('chats are grouped by last activity, newest first', () => {
  const groups = groupChats(sessions, '', now);
  assert.deepEqual(
    groups.map((group) => [group.label, group.sessions.map((s) => s.id)]),
    [
      ['Today', ['today-late', 'today-early']],
      ['Yesterday', ['yesterday']],
      ['Previous 7 days', ['week']],
      ['Older', ['old']],
    ],
  );
});

test('search matches titles case-insensitively and drops empty groups', () => {
  const groups = groupChats(sessions, '  VM ', now);
  assert.deepEqual(
    groups.map((group) => [group.label, group.sessions.map((s) => s.id)]),
    [['Today', ['today-late']]],
  );
  assert.deepEqual(
    groupChats(sessions, 'untitled', now).flatMap((g) => g.sessions.map((s) => s.id)),
    ['today-early'],
  );
  assert.deepEqual(groupChats(sessions, 'nothing like this', now), []);
  assert.deepEqual(groupChats(undefined, '', now), []);
});

test('a running conversation shows its question until its title arrives', () => {
  const question = '\n  Where are my biggest Azure savings opportunities? Score my Crawl maturity.\nUse all subscriptions.';
  const added = withQuestionTitle(sessions, 'new', question, now);
  assert.deepEqual(added[0], {
    id: 'new',
    summary: 'Where are my biggest Azure savings opportunities? Score my Crawl maturity.',
    modified: now.toISOString(),
  });
  assert.equal(added.length, sessions.length + 1);
  for (const summary of [null, "", "   "]) {
    assert.equal(
      withQuestionTitle([{ id: 'blank', summary }], 'blank', 'My question', now)[0].summary,
      'My question',
    );
  }

  const renamed = withQuestionTitle(
    [{ id: 'fresh', summary: 'Untitled conversation', started: at(0) }],
    'fresh',
    'x'.repeat(90),
    now,
  );
  assert.deepEqual(renamed, [
    { id: 'fresh', summary: `${'x'.repeat(80)}…`, started: at(0), modified: now.toISOString() },
  ]);

  // A named conversation keeps its title, and an empty question changes nothing.
  assert.equal(withQuestionTitle(sessions, 'today-late', 'Another question', now), null);
  assert.equal(withQuestionTitle(sessions, 'new', '   ', now), null);
  assert.equal(withQuestionTitle(undefined, '', 'Question', now), null);
});
