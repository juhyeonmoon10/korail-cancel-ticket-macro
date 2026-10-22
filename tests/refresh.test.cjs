'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

// Exercise the production refresh state machine without loading a website.
// The test controls the clock and target availability, not the implementation.
const source = fs.readFileSync(path.join(__dirname, '../assets/content.js'), 'utf8');
const begin = source.indexOf('  async function inspect(run) {');
const end = source.indexOf('  async function start() {', begin);
assert.ok(begin >= 0 && end > begin, 'production inspect function is present');
const inspectSource = source.slice(begin, end);

function harness() {
  let time = 0, target = null, isBusy = false, guardError = null;
  const events = [];
  const row = { innerHTML: '<a>일반실 31,500원</a>' };
  const slot = { action: { name: 'selected reservation' } };
  const context = {
    running: true, generation: 7,
    pendingRefresh: { since: 0, lastChange: 0, changed: true, sawBusy: false },
    Date: { now: () => time },
    guard() { if (guardError) throw guardError; },
    busy: () => isBusy,
    findTarget() { if (target instanceof Error) throw target; if (!target) throw new Error('No target during empty-list refresh'); return target; },
    seatAction: current => current.action,
    found: async (action, run) => { events.push({ kind: 'found', action, run }); context.running = false; },
    schedule: run => { events.push({ kind: 'schedule', run }); }
  };
  vm.createContext(context);
  vm.runInContext(inspectSource + '\nglobalThis.tick = inspect;', context);
  return {
    context, events, row, slot,
    tick: async at => { time = at; await context.tick(7); },
    show: (next = { row, slot }) => { target = next; },
    empty: () => { target = null; },
    ambiguous: () => { target = new Error('Multiple matching train cards'); },
    setBusy: value => { isBusy = value; },
    deny: message => { guardError = new Error(message); }
  };
}

test('live list can remain empty beyond 700ms then complete before deadline', async () => {
  const h = harness();
  await h.tick(1000); await h.tick(1700);
  assert.equal(h.events.length, 0);
  assert.ok(h.context.pendingRefresh, 'empty list retains the pending refresh');
  h.show(); await h.tick(1800);
  await h.tick(2499); assert.equal(h.events.length, 0);
  await h.tick(2500);
  assert.equal(h.events.length, 1);
  assert.equal(h.events[0].kind, 'found');
  assert.equal(h.events[0].action, h.slot.action);
  assert.equal(h.context.pendingRefresh, null);
  await h.tick(2900); assert.equal(h.events.length, 1, 'resolved refresh cannot click again');
});

test('ambiguous cards cannot trigger a click; resolved single card must stabilize', async () => {
  const h = harness(); h.ambiguous();
  await h.tick(1200); await h.tick(2200);
  assert.equal(h.events.length, 0);
  h.show(); await h.tick(2300); await h.tick(2999);
  assert.equal(h.events.length, 0);
  await h.tick(3000); assert.equal(h.events[0].kind, 'found');
});

test('persistent missing or ambiguous target reaches the existing deadline', async () => {
  for (const ambiguous of [false, true]) {
    const h = harness(); if (ambiguous) h.ambiguous();
    await h.tick(29000);
    await assert.rejects(h.tick(30001), /30초/);
    assert.equal(h.events.length, 0);
  }
});

test('a target disappearing and reappearing restarts the stability window', async () => {
  const h = harness(); h.show(); await h.tick(1000);
  h.empty(); await h.tick(1600);
  h.show(); await h.tick(1800); await h.tick(2499);
  assert.equal(h.events.length, 0);
  await h.tick(2500); assert.equal(h.events[0].kind, 'found');
});

test('new target markup must stabilize even if the mutation observer missed it', async () => {
  const h = harness(); h.show(); await h.tick(1000);
  h.row.innerHTML = '<a>일반실 33,000원</a>';
  await h.tick(1600); await h.tick(2299);
  assert.equal(h.events.length, 0);
  await h.tick(2300); assert.equal(h.events[0].kind, 'found');
});

test('a busy signal after a complete target resets stabilization', async () => {
  const h = harness(); h.show(); await h.tick(1000);
  h.setBusy(true); await h.tick(1700);
  h.setBusy(false); await h.tick(1800); await h.tick(2499);
  assert.equal(h.events.length, 0);
  await h.tick(2500); assert.equal(h.events[0].kind, 'found');
});

test('unchanged old results without a refresh signal are never used', async () => {
  const h = harness(); h.context.pendingRefresh.changed = false;
  h.show(); await h.tick(1000); await h.tick(2000);
  assert.equal(h.events.length, 0);
  await assert.rejects(h.tick(30001), /30초/);
});

test('stable sold-out target schedules another check instead of clicking', async () => {
  const h = harness(); h.slot.action = null;
  h.show(); await h.tick(1000); await h.tick(1700);
  assert.deepEqual(h.events, [{ kind: 'schedule', run: 7 }]);
});

test('blocking or changed journey errors still stop immediately during empty results', async () => {
  for (const reason of ['조회 조건이 바뀌었습니다.', '접속 제한 확인 필요']) {
    const h = harness(); h.deny(reason);
    await assert.rejects(h.tick(1000), error => error.message === reason);
    assert.equal(h.events.length, 0);
  }
});
