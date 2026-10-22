'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { JSDOM } = require('jsdom');
const fs = require('node:fs');
const path = require('node:path');
const assets = fs.existsSync(path.join(__dirname, 'assets/site-block.js')) ? path.join(__dirname, 'assets') : path.join(__dirname, '../assets');
const B = require(path.join(assets, 'site-block.js'));
const phrase = '자동화된 요청으로 감지되어 차단되었습니다.';
const notice = `<div role="dialog"><h2>안내 메시지</h2><p>${phrase}</p><p>CODE : -8002</p><button>확인</button></div>`;
const delay = () => new Promise(resolve => setTimeout(resolve, 220));

function fixture(body = '', url = 'https://www.korail.com/ticket/search/list') {
  const dom = new JSDOM(`<!doctype html><html><body>${body}</body></html>`, { url, pretendToBeVisual: true, runScripts: 'outside-only' });
  // jsdom has no layout. Styles/hidden ancestors are still checked by the reader.
  dom.window.Element.prototype.getClientRects = function () { return [{ width: 300, height: 80 }]; };
  return { dom, doc: dom.window.document, view: dom.window, close: () => dom.window.close() };
}

test('recognizes displayed official HTML dialog across sibling nodes and whitespace', () => {
  const f = fixture(notice.replace('요청으로', '요청으로\n').replace('CODE :', 'CODE :\t'));
  try { assert.deepEqual(B.read(f.doc), { code: '-8002' }); } finally { f.close(); }
});

test('requires both the explicit wording and the exact known code', () => {
  for (const html of ['CODE : -8002', phrase, '일반 오류 CODE : -1234', `${phrase} CODE : -1234`, `${phrase} CODE : -80020`]) {
    const f = fixture(html);
    try { assert.equal(B.read(f.doc), null, html); } finally { f.close(); }
  }
  const f = fixture(`${phrase} CODE : -1234${notice}`);
  try { assert.deepEqual(B.read(f.doc), { code: '-8002' }); } finally { f.close(); }
});

test('ignores hidden ancestors, invisible styles and nodes without a rendered box', () => {
  for (const attr of ['hidden', 'aria-hidden="true"', 'style="display:none"', 'style="visibility:hidden"', 'style="opacity:0"']) {
    const f = fixture(`<section ${attr}>${notice}</section>`);
    try { assert.equal(B.read(f.doc), null, attr); } finally { f.close(); }
  }
  const f = fixture(notice);
  try { f.doc.querySelector('[role=dialog]').getClientRects = () => []; assert.equal(B.read(f.doc), null); } finally { f.close(); }
});

test('ignores helper content, input text, editable text and scripts', () => {
  for (const html of [`<div id="korail-one-helper">${notice}</div>`, `<input value="${phrase} CODE : -8002">`,
    `<textarea>${phrase} CODE : -8002</textarea>`, `<div contenteditable="true">${notice}</div>`, `<script>${phrase} CODE : -8002</script>`]) {
    const f = fixture(html);
    try { assert.equal(B.read(f.doc), null); } finally { f.close(); }
  }
});

test('installs only on official HTTPS ticket pages, excluding demo and lookalike origins', () => {
  for (const url of ['https://korail-helper.invalid/card-demo.html', 'https://www.korail.com.evil.example/ticket/main',
    'http://www.korail.com/ticket/main', 'https://www.korail.com:8443/ticket/main', 'https://www.korail.com/login']) {
    const f = fixture(notice, url);
    try { assert.equal(B.install(f.doc, () => assert.fail('must not report')), null); assert.equal(B.read(f.doc), null); } finally { f.close(); }
  }
});

test('reports a notice already displayed once, preserving controls and document HTML', () => {
  const f = fixture(notice + '<input value="untouched">'), calls = [];
  try {
    const before = f.doc.body.innerHTML;
    f.view.HTMLElement.prototype.click = () => assert.fail('must not click');
    f.view.fetch = () => assert.fail('must not fetch');
    f.view.XMLHttpRequest = () => assert.fail('must not request');
    const first = B.install(f.doc, code => calls.push(code));
    assert.equal(B.install(f.doc, () => assert.fail('duplicate observer')), first);
    assert.deepEqual(calls, ['-8002']);
    assert.equal(f.doc.body.innerHTML, before);
  } finally { f.close(); }
});

test('observes a later modal without any automation running and stops before its own report changes UI', async () => {
  const f = fixture('<main>조회 화면</main>'), calls = [];
  try {
    B.install(f.doc, code => { calls.push(code); f.doc.body.append(f.doc.createElement('aside')); });
    f.doc.body.insertAdjacentHTML('beforeend', notice);
    await delay();
    f.doc.body.insertAdjacentHTML('beforeend', notice);
    await delay();
    assert.deepEqual(calls, ['-8002']);
  } finally { f.close(); }
});

test('observes an existing modal becoming visible through an ancestor class change', async () => {
  const f = fixture(`<style>.closed{display:none}</style><section class="closed">${notice}</section>`), calls = [];
  try {
    B.install(f.doc, code => calls.push(code));
    assert.deepEqual(calls, []);
    f.doc.querySelector('section').className = '';
    await delay();
    assert.deepEqual(calls, ['-8002']);
  } finally { f.close(); }
});

test('continuous page mutations do not postpone a displayed block indefinitely', async () => {
  const f = fixture('<aside id="clock"></aside>'), calls = [];
  let pulse;
  try {
    B.install(f.doc, code => calls.push(code));
    f.doc.body.insertAdjacentHTML('beforeend', notice);
    pulse = setInterval(() => { f.doc.getElementById('clock').textContent = String(Date.now()); }, 25);
    await new Promise(resolve => setTimeout(resolve, 500));
    assert.deepEqual(calls, ['-8002']);
  } finally { clearInterval(pulse); f.close(); }
});

test('hidden tab is not reported until its displayed notice becomes observable', async () => {
  const f = fixture(notice), calls = [];
  try {
    Object.defineProperty(f.doc, 'hidden', { configurable: true, value: true });
    B.install(f.doc, code => calls.push(code));
    assert.deepEqual(calls, []);
    Object.defineProperty(f.doc, 'hidden', { configurable: true, value: false });
    f.doc.dispatchEvent(new f.view.Event('visibilitychange'));
    await delay();
    assert.deepEqual(calls, ['-8002']);
  } finally { f.close(); }
});

test('disconnect cancels a pending report; report exceptions do not trigger retries', async () => {
  const f = fixture(''), second = fixture(notice);
  try {
    const controller = B.install(f.doc, () => assert.fail('disconnected monitor'));
    f.doc.body.insertAdjacentHTML('beforeend', notice);
    controller.disconnect();
    let calls = 0;
    B.install(second.doc, () => { calls++; throw new Error('bridge unavailable'); });
    second.doc.body.insertAdjacentHTML('beforeend', notice);
    await delay();
    assert.equal(calls, 1);
  } finally { f.close(); second.close(); }
});

test('production content integration reports the scoped message without starting the legacy loop', () => {
  const f = fixture(notice), calls = [];
  try {
    f.view.desktopNonce = 'test-page-nonce'; f.view.desktopJourney = null;
    f.view.chrome = { webview: { postMessage: message => calls.push(message), addEventListener() {} } };
    for (const name of ['core.js', 'korail-site.js', 'site-block.js', 'content.js']) f.view.eval(fs.readFileSync(path.join(assets, name), 'utf8'));
    assert.deepEqual(JSON.parse(JSON.stringify(calls.filter(message => message.kind === 'siteBlocked'))),
      [{ kind: 'siteBlocked', code: '-8002', nonce: 'test-page-nonce' }]);
    assert.equal(f.view.KorailDesktopHelper.snapshot().running, false);
    assert.equal(f.view.KorailDesktopHelper.snapshot().count, 0);
  } finally { f.close(); }
});
