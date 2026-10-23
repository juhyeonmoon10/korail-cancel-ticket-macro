'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { JSDOM } = require('jsdom');
const asset = name => fs.readFileSync(path.join(__dirname, 'assets', name), 'utf8');
const target = () => ({ date: '2026-10-22', fromTime: '00:00', departureStation: '서울', arrivalStation: '전주', passengers: 1,
  route: '직통', seatAttribute: '일반석', roundTrip: false, adjacent: false, combined: false });
const generalHtml = `<!doctype html><html><head></head><body><main id="container">
<div class="ticketSrchWrap slick"><div class="start"><div class="select_inner"><input name="txtGoStart" readonly value="서울"></div><a class="btn_pop btn_end btn_pop-open" href="#none"><span class="blind">출발역 선택</span></a></div>
<div class="end"><div class="select_inner"><input name="txtGoEnd" readonly value="전주"></div><a class="btn_pop btn_end btn_pop-open" href="#none"><span class="blind">도착역 선택</span></a></div></div>
<section class="sectionWrap"><input type="checkbox" name="rtYn" id="shuttle01"><label for="shuttle01">왕복</label>
<div class="writeWrap day_type"><input id="startDate" readonly value="2026-10-22(목) 00:00"><a href="#none" class="btn_pop btn_d-day" title="출발일"></a></div>
<div class="total clear"><span class="label">인원</span><div class="writeWrap"><a href="#none" class="data btn_pop" title="인원">총 1명</a></div></div></section>
<div class="optionWrap"><div class="titWrap"><button class="btnToggle" aria-expanded="false">옵션</button></div><div class="selectAreaWrap" style="display:none">
<input type="radio" name="adjStnScdlOfrFlg" value="Y" id="option01"><label for="option01">포함</label><input type="radio" name="adjStnScdlOfrFlg" value="N" id="option02" checked><label for="option02">미포함</label>
<input type="radio" name="adjStnScdlOfrFlg2" value="Y" id="option05"><label for="option05">포함</label><input type="radio" name="adjStnScdlOfrFlg2" value="N" id="option06" checked><label for="option06">미포함</label></div></div>
<div class="btnWrap type_bg"><button class="btn_bn-blue">간편구매 등록</button><button class="btn_lookup">열차 조회</button></div></main></body></html>`;
function fixture(t, general = false) {
  const dom = new JSDOM(general ? generalHtml : asset('card-demo.html'), { url: 'https://www.korail.com/ticket/search/' + (general ? 'general' : 'list'), runScripts: 'outside-only', pretendToBeVisual: true });
  t.after(() => dom.window.close());
  const w = dom.window, doc = w.document;
  let hit = null, covered = null;
  w.HTMLElement.prototype.getBoundingClientRect = function () { hit = this; return this.testRect || { left: 10, top: 20, width: 120, height: 40 }; };
  w.HTMLElement.prototype.getClientRects = function () { return [{ left: 10, top: 20, width: 120, height: 40 }]; };
  doc.elementFromPoint = () => covered || hit;
  for (const method of ['click', 'scroll', 'scrollTo', 'scrollIntoView']) w.HTMLElement.prototype[method] = () => { throw new Error('Planner cannot generate input'); };
  w.fetch = w.XMLHttpRequest = () => { throw new Error('Planner cannot access network'); };
  const style = doc.createElement('style'); style.textContent = '.date_wrap,.ticketSrchWrap .btn_pop,.search-option-bar__wrap{display:block}'; doc.head.append(style);
  for (const file of ['core.js', 'korail-site.js', 'queue-query.js']) w.eval(asset(file));
  const read = (wanted = target(), context = {}) => JSON.parse(JSON.stringify(w.KorailQueueQuery.read(wanted, doc, context)));
  return { dom, w, doc, read, cover: node => { covered = node; } };
}
function noAction(plan, state) { assert.equal(plan.state, state); assert.equal(plan.action, undefined); }
function stationPicker(doc, value = '') {
  doc.body.insertAdjacentHTML('beforeend', '<div role="dialog"><div class="layerWrap type_tranin-station-pop_wrap"><h1>기차역 조회</h1><div class="sch_form"><div class="sch_box"><input name="searchTxt" title="역명을 입력해주세요"></div><div class="sch_wrap"><ul></ul></div></div></div></div>');
  const modal = doc.querySelector('.type_tranin-station-pop_wrap'); modal.querySelector('input').value = value; return modal;
}
function calendar(doc, options = {}) {
  const day = options.day || 22, selectedDay = options.selectedDay || day, hour = options.hour || 0;
  doc.body.insertAdjacentHTML('beforeend', `<div class="layerWrap type_date-pop_wrap"><h1>날짜 선택</h1><div class="type_date-pop"><div class="datepk_wrap"><button class="slick-prev">Previous</button><button class="slick-next">Next</button>
<div class="slick-active"><div class="datepicker"><p class="date">${options.month || '2026. 10.'}</p><table><tbody><tr><td><a href="#none" aria-disabled="false" title="${day === selectedDay ? '선택한 날짜' : ''}"><span class="day">${day}</span></a></td></tr></tbody></table></div></div></div>
<div class="timeSelect"><div class="slide_list"><div class="slideWrap"><button class="slick-prev">Previous</button><button class="slick-next">Next</button>${Array.from({ length: 24 }, (_, n) => `<li class="${n === hour ? 'current' : ''}"><a href="#none" tabindex="${n < 5 ? '0' : '-1'}" aria-disabled="${n < 5 ? 'false' : 'true'}">${String(n).padStart(2, '0')}시</a></li>`).join('')}</div></div></div>
<div class="btn_wrap"><button class="btn_by-blue btn_pop-close">취소</button><button class="btn_bn-blue">적용</button></div></div></div>`);
  return doc.querySelector('.type_date-pop_wrap');
}

test('list requires a newly submitted query before reporting ready, and never mutates DOM', t => {
  const f = fixture(t), before = f.doc.documentElement.outerHTML;
  let plan = f.read(); assert.equal(plan.state, 'action'); assert.equal(plan.action.kind, 'submit-query'); assert.equal(plan.action.id, 'query-submit:list');
  assert.equal(plan.resultCount, 6);
  assert.equal(plan.action.x, 70); assert.equal(plan.action.viewportWidth, 1024);
  plan = f.read(target(), { submitted: true }); noAction(plan, 'ready'); assert.equal(plan.count, 6); assert.equal(plan.query.date, target().date); assert.match(plan.fingerprint, /KTX/);
  assert.equal(f.doc.documentElement.outerHTML, before);
});

test('general submits observed conditions once but cannot claim route or seat attributes before results', t => {
  const f = fixture(t, true), plan = f.read();
  assert.equal(plan.action.kind, 'submit-query'); assert.equal(plan.action.id, 'query-submit:general'); assert.equal(plan.query.supported, false);
  assert.equal(plan.query.route, null); assert.equal(plan.query.seatAttribute, null); noAction(f.read(target(), { submitted: true }), 'waiting');
});

test('station field mismatch opens the readonly public picker on general and list layouts', t => {
  for (const general of [false, true]) for (const key of ['departureStation', 'arrivalStation']) {
    const f = fixture(t, general), wanted = { ...target(), [key]: '부산' }, plan = f.read(wanted);
    assert.equal(plan.action.id, 'station-open:' + key); assert.equal(plan.action.picker, key); assert.equal(plan.action.kind, 'click');
  }
});

test('station modal requires recorded opener, focuses input, types entire station, selects only exact unique result', t => {
  const f = fixture(t), modal = stationPicker(f.doc), wanted = { ...target(), arrivalStation: '부산' }, context = { activePicker: 'arrivalStation' };
  noAction(f.read(wanted), 'blocked');
  assert.equal(f.read(wanted, context).action.id, 'station-focus:arrivalStation');
  modal.querySelector('input').focus(); let plan = f.read(wanted, context);
  assert.equal(plan.action.kind, 'type'); assert.equal(plan.action.text, '부산');
  modal.querySelector('input').value = '부산'; noAction(f.read(wanted, context), 'waiting');
  modal.querySelector('ul').innerHTML = '<li><a>부산진</a></li><li><a><span>부산</span></a></li>';
  plan = f.read(wanted, context); assert.equal(plan.action.id, 'station-select:arrivalStation:부산'); assert.equal(plan.action.closePicker, true);
  modal.querySelector('ul').insertAdjacentHTML('beforeend', '<li><a>부산</a></li>'); noAction(f.read(wanted, context), 'blocked');
});

test('station suffix normalization does not match station substrings', t => {
  const f = fixture(t); assert.equal(f.read({ ...target(), departureStation: '서울역' }).action.kind, 'submit-query');
  const wanted = { ...target(), departureStation: '서' }; assert.equal(f.read(wanted).action.picker, 'departureStation');
});

test('date opener uses public readonly controls while minute precision is resolved through the public hour picker', t => {
  for (const general of [false, true]) {
    const f = fixture(t, general), wanted = { ...target(), date: '2026-10-23' }, plan = f.read(wanted);
    assert.match(plan.action.id, /^date-open:/); assert.equal(plan.action.picker, 'date');
    assert.equal(f.read({ ...wanted, fromTime: '13:27' }, { latestDeparture: '14:30' }).action.picker, 'date');
  }
});

test('calendar navigates active month, selects exact enabled day, and applies after date and hour match', t => {
  const f = fixture(t), modal = calendar(f.doc, { day: 23, selectedDay: 22 }), wanted = { ...target(), date: '2026-10-23' }, context = { activePicker: 'date' };
  noAction(f.read(wanted), 'blocked'); let plan = f.read(wanted, context); assert.equal(plan.action.id, 'date-day:2026-10-23');
  modal.querySelector('tbody a').title = '선택한 날짜'; plan = f.read(wanted, context); assert.match(plan.action.id, /^date-apply:/); assert.equal(plan.action.closePicker, true);
  assert.match(f.read({ ...wanted, date: '2026-11-23' }, context).action.id, /^date-month:next:/);
  assert.match(f.read({ ...wanted, date: '2026-09-23' }, context).action.id, /^date-month:prev:/);
  modal.querySelector('tbody a').setAttribute('aria-disabled', 'true'); noAction(f.read(wanted, context), 'blocked');
});

test('calendar hour chooses visible exact hour or one native slider step without mutating time', t => {
  const f = fixture(t), modal = calendar(f.doc), context = { activePicker: 'date' };
  assert.equal(f.read({ ...target(), fromTime: '03:00' }, context).action.id, 'date-hour:03:00');
  assert.equal(f.read({ ...target(), fromTime: '13:00' }, context).action.id, 'date-hours:next:0-4');
  modal.querySelector('.timeSelect .slick-next').classList.add('slick-disabled'); noAction(f.read({ ...target(), fromTime: '13:00' }, context), 'blocked');
});

test('compact calendar explains inaccessible hours when responsive arrows are absent or hidden', t => {
  for (const mode of ['removed', 'hidden']) {
    const f = fixture(t), modal = calendar(f.doc), context = { activePicker: 'date' };
    Object.defineProperty(f.w, 'innerWidth', { value: 860, configurable: true });
    for (const arrow of modal.querySelectorAll('.timeSelect .slick-prev,.timeSelect .slick-next')) {
      if (mode === 'removed') arrow.remove(); else arrow.style.display = 'none';
    }
    const before = f.doc.documentElement.outerHTML;
    const plan = f.read({ ...target(), fromTime: '13:00' }, context);
    noAction(plan, 'blocked'); assert.equal(plan.terminal, true); assert.equal(plan.retryAllowed, false);
    assert.match(plan.message, /창.*최대화.*예약 관리/);
    assert.equal(f.doc.documentElement.outerHTML, before);
    // Compact width alone must not stop a visible hour or an already selected one.
    assert.equal(f.read({ ...target(), fromTime: '03:00' }, context).action.id, 'date-hour:03:00');
    assert.equal(f.read(target(), context).action.id, 'date-apply:2026-10-22:00:00');
  }
});

test('hour arrow guidance is limited to compact layout and observable missing arrows', t => {
  const f = fixture(t), modal = calendar(f.doc), wanted = { ...target(), fromTime: '13:00' }, context = { activePicker: 'date' };
  Object.defineProperty(f.w, 'innerWidth', { value: 860, configurable: true });
  assert.equal(f.read(wanted, context).action.id, 'date-hours:next:0-4');
  modal.querySelector('.timeSelect .slick-next').remove();
  Object.defineProperty(f.w, 'innerWidth', { value: 1280, configurable: true });
  const plan = f.read(wanted, context); noAction(plan, 'unknown'); assert.doesNotMatch(plan.message, /최대화/);
});

test('calendar duplicate active months or day numbers never choose an ambiguous date', t => {
  const f = fixture(t), modal = calendar(f.doc), context = { activePicker: 'date' };
  modal.querySelector('tr').append(modal.querySelector('td').cloneNode(true)); noAction(f.read(target(), context), 'unknown');
  modal.querySelector('tr').lastChild.remove(); modal.querySelector('.datepk_wrap').append(modal.querySelector('.slick-active').cloneNode(true));
  noAction(f.read(target(), context), 'unknown');
});

test('known options are disabled through labels; collapsed general radios open first', t => {
  for (const general of [false, true]) {
    const f = fixture(t, general);
    f.doc.querySelector(general ? '#shuttle01' : '#rtYn').checked = true;
    assert.equal(f.read().action.id, 'option-off:roundTrip'); f.doc.querySelector(general ? '#shuttle01' : '#rtYn').checked = false;
    if (general) {
      f.doc.querySelector('#option01').checked = true; assert.equal(f.read().action.id, 'options-open');
      f.doc.querySelector('.optionWrap .selectAreaWrap').style.display = 'block'; assert.equal(f.read().action.id, 'option-off:adjacent');
    } else { f.doc.querySelector('#adjStnScdlOfrFlg2').checked = true; assert.equal(f.read().action.id, 'option-off:combined'); }
  }
});

test('list route and seat attributes are restored through their exact public options', t => {
  const f = fixture(t), route = f.doc.querySelector('.select02'); route.querySelector('.selected span').textContent = '환승';
  assert.equal(f.read().action.id, 'route-open'); route.querySelector('.options').style.display = 'block';
  assert.equal(f.read().action.id, 'route-select:직통'); assert.equal(f.read().action.kind, 'submit-query'); route.querySelector('.selected span').textContent = '직통'; route.querySelector('.options').style.display = 'none';
  assert.equal(f.read({ ...target(), seatAttribute: '유아동반석' }).action.id, 'seat-attribute-open');
  f.doc.querySelector('.select01 .options').style.display = 'block'; assert.equal(f.read({ ...target(), seatAttribute: '유아동반석' }).action.id, 'seat-attribute-select:유아동반석');
  assert.equal(f.read({ ...target(), seatAttribute: '유아동반석' }).action.kind, 'submit-query');
});

test('all train types are selected through a submit action before ready', t => {
  const f = fixture(t), allTab = f.doc.querySelector('#btn_tab_info1 > button'), ktx = f.doc.querySelector('#btn_tab_info2 > button');
  allTab.setAttribute('aria-pressed', 'false'); ktx.setAttribute('aria-pressed', 'true'); ktx.parentElement.classList.add('active');
  const plan = f.read(target(), { submitted: true }); assert.equal(plan.action.kind, 'submit-query'); assert.equal(plan.action.id, 'query-all-trains');
});

test('loading and empty results wait without resubmitting', t => {
  const f = fixture(t); f.doc.querySelector('#tab_tab_info1').setAttribute('aria-busy', 'true');
  noAction(f.read(), 'waiting'); assert.equal(f.read().loading, true);
  f.doc.querySelector('#tab_tab_info1').removeAttribute('aria-busy'); f.doc.querySelector('.tckWrap > ul').innerHTML = '';
  noAction(f.read(target(), { submitted: true }), 'waiting'); assert.equal(f.read().resultCount, 0);
  noAction(f.read(target(), { submitted: false }), 'waiting'); assert.equal(f.read().loading, true);
});

test('invalid result identities, duplicate rows, or wrong route cannot become ready', t => {
  for (const change of [
    f => f.doc.querySelector('.tckWrap > ul').append(f.doc.querySelector('li.tckList').cloneNode(true)),
    f => { f.doc.querySelector('li.tckList .num').textContent = 'unknown'; },
    f => { f.doc.querySelector('li.tckList h3 span').textContent = '부산'; }
  ]) { const f = fixture(t); change(f); noAction(f.read(target(), { submitted: true }), 'blocked'); }
});

test('authentication, site block, unknown dialogs, and active booking controls stop transition', t => {
  for (const html of ['<input type="password">', '<input autocomplete="one-time-code">', '<iframe src="https://example.invalid/captcha"></iframe>',
    '<div role="dialog">알 수 없는 안내</div>', '<div class="layerWrap">알 수 없는 창</div>', '<div class="ticket_reserv_wrap">예매</div>',
    '<p>다시 로그인 후 이용하세요.</p>', '<p>자동화된 요청으로 감지되어 차단되었습니다. CODE : -8002</p>']) {
    const f = fixture(t); f.doc.body.insertAdjacentHTML('beforeend', html); noAction(f.read(), 'blocked');
    if (html.includes('-8002')) assert.equal(f.read().blockCode, '-8002');
  }
});

test('hidden notices are ignored but another dialog beside a known picker is rejected', t => {
  const f = fixture(t); f.doc.body.insertAdjacentHTML('beforeend', '<p hidden>자동화된 요청으로 감지되어 차단되었습니다. CODE : -8002</p>');
  assert.equal(f.read().action.kind, 'submit-query'); stationPicker(f.doc); f.doc.body.insertAdjacentHTML('beforeend', '<div role="dialog">다른 안내</div>');
  noAction(f.read(target(), { activePicker: 'arrivalStation' }), 'blocked');
});

test('unsupported targets, passenger count, and public route boundaries never generate input', t => {
  const f = fixture(t);
  for (const wanted of [null, {}, { ...target(), date: '2026-02-30' }, { ...target(), fromTime: '24:00' }, { ...target(), passengers: 2 },
    { ...target(), route: '환승' }, { ...target(), adjacent: true }, { ...target(), seatAttribute: 'unknown' }, { ...target(), departureStation: '서울\n' }]) noAction(f.read(wanted), 'blocked');
  f.doc.querySelector('#labelple').value = '총 2명'; noAction(f.read(), 'blocked');
  for (const url of ['https://example.com/ticket/search/list', 'https://www.korail.com/login', 'http://www.korail.com/ticket/search/list', 'https://www.korail.com:123/ticket/search/list']) {
    f.dom.reconfigure({ url }); noAction(f.read(), 'blocked');
  }
});

test('duplicate and covered controls cannot generate native input; disabled query waits without clicks', t => {
  for (const change of [
    f => f.doc.querySelector('.btnWrap.btn_box').append(f.doc.querySelector('.btnWrap.btn_box button').cloneNode(true)),
    f => f.cover(f.doc.body),
    f => { f.doc.querySelector('.btnWrap.btn_box button').disabled = true; }
  ]) { const f = fixture(t); change(f); const plan = f.read(); assert.ok(['unknown', 'blocked', 'waiting'].includes(plan.state)); assert.equal(plan.action, undefined); }
});

test('offscreen coordinates are returned for bounded native scrolling, never JS scrolling', t => {
  const f = fixture(t), button = f.doc.querySelector('.btnWrap.btn_box button'); button.testRect = { left: 10, top: -200, width: 120, height: 40 };
  const plan = f.read(); assert.equal(plan.action.inViewport, false); assert.equal(plan.action.y, -180);
});

test('picker closure is reported so the host can discard temporary modal context', t => {
  const f = fixture(t), plan = f.read(target(), { activePicker: 'date' }); assert.equal(plan.pickerClosed, true); assert.equal(plan.action.kind, 'submit-query');
});

test('result revision detects identical row rerender and loading, excluding query fields and helper UI', t => {
  const f = fixture(t), baseline = f.read(); assert.equal(baseline.resultRevision, 0); assert.equal(baseline.loading, false);
  f.doc.querySelector('#labelstart').value = '부산'; f.doc.querySelector('#startDate').setAttribute('value', '2026-10-23(금) 00:00');
  f.doc.body.insertAdjacentHTML('beforeend', '<div id="korail-one-helper">helper status</div>');
  assert.equal(f.w.KorailQueueQuery.observation(f.doc).resultRevision, 0);
  const row = f.doc.querySelector('li.tckList'); row.replaceWith(row.cloneNode(true));
  const rerender = f.w.KorailQueueQuery.observation(f.doc); assert.ok(rerender.resultRevision > baseline.resultRevision);
  f.doc.querySelector('#tab_tab_info1').setAttribute('aria-busy', 'true');
  const loading = f.w.KorailQueueQuery.observation(f.doc); assert.ok(loading.resultRevision > rerender.resultRevision); assert.equal(loading.loading, true);
  f.doc.querySelector('#tab_tab_info1').removeAttribute('aria-busy'); const done = f.w.KorailQueueQuery.observation(f.doc);
  assert.ok(done.resultRevision > loading.resultRevision); assert.equal(done.loading, false);
});

test('result revision detects replacement of the complete result panel', t => {
  const f = fixture(t); f.read(); const panel = f.doc.querySelector('#tab_tab_info1'); panel.replaceWith(panel.cloneNode(true));
  assert.ok(f.read(target(), { submitted: true }).resultRevision > 0);
});

function hourWindow(modal, from, through) {
  for (const a of modal.querySelectorAll('.timeSelect li > a')) {
    const n = Number(a.textContent.slice(0, 2)), active = n >= from && n <= through;
    a.setAttribute('tabindex', active ? '0' : '-1'); a.setAttribute('aria-disabled', active ? 'false' : 'true');
  }
}
function pastHours(modal, before) {
  for (const li of modal.querySelectorAll('.timeSelect li'))
    if (Number(li.textContent.slice(0, 2)) < before) li.innerHTML = '<span class="disabled" aria-disabled="true">' + li.textContent + '</span>';
  hourWindow(modal, before, Math.min(23, before + 4));
}

test('effective query time is accepted only between stored lower bound and earliest candidate departure', t => {
  const f = fixture(t), wanted = { ...target(), fromTime: '12:15' }, context = { submitted: true, latestDeparture: '13:10' };
  f.doc.querySelector('#startDate').value = '2026-10-22(목) 12:40';
  noAction(f.read(wanted, context), 'ready'); assert.equal(f.read(wanted, context).query.fromTime, '12:40');
  f.doc.querySelector('#startDate').value = '2026-10-22(목) 12:14'; assert.equal(f.read(wanted, context).action.picker, 'date');
  f.doc.querySelector('#startDate').value = '2026-10-22(목) 13:11'; assert.equal(f.read(wanted, context).action.picker, 'date');
  noAction(f.read(wanted, { ...context, latestDeparture: '12:14' }), 'blocked');
});

test('past hours are skipped using public disabled spans and current-minute application can become ready', t => {
  const f = fixture(t), modal = calendar(f.doc, { hour: 13 }), context = { activePicker: 'date', latestDeparture: '14:05' };
  pastHours(modal, 13);
  const plan = f.read(target(), context); assert.equal(plan.action.id, 'date-apply:2026-10-22:13:00');
  modal.remove(); f.doc.querySelector('#startDate').value = '2026-10-22(목) 13:38';
  noAction(f.read(target(), { submitted: true, latestDeparture: '14:05' }), 'ready');
});

test('nonzero lower minutes can observe the current hour once, then preserve the verified effective time', t => {
  const f = fixture(t), modal = calendar(f.doc, { hour: 13 }), wanted = { ...target(), fromTime: '13:15' };
  pastHours(modal, 13);
  assert.equal(f.read(wanted, { activePicker: 'date', latestDeparture: '13:55' }).action.id, 'date-apply:2026-10-22:13:00');
  modal.remove(); f.doc.querySelector('#startDate').value = '2026-10-22(목) 13:40';
  noAction(f.read(wanted, { submitted: true, latestDeparture: '13:55' }), 'ready');
});

test('an already observed earlier hour advances to the next full hour or rejects a too-early candidate', t => {
  const f = fixture(t), modal = calendar(f.doc, { hour: 13 }), wanted = { ...target(), fromTime: '13:15' };
  f.doc.querySelector('#startDate').value = '2026-10-22(목) 13:00'; hourWindow(modal, 10, 14);
  assert.equal(f.read(wanted, { activePicker: 'date', latestDeparture: '14:30' }).action.id, 'date-hour:14:00');
  noAction(f.read(wanted, { activePicker: 'date', latestDeparture: '13:55' }), 'blocked');
});

test('an unavailable earliest hour or current minute beyond the first train cannot be applied', t => {
  const f = fixture(t), modal = calendar(f.doc, { hour: 13 }); pastHours(modal, 13);
  noAction(f.read(target(), { activePicker: 'date', latestDeparture: '12:55' }), 'blocked');
  f.doc.querySelector('#startDate').value = '2026-10-22(목) 13:40';
  noAction(f.read(target(), { activePicker: 'date', latestDeparture: '13:30' }), 'blocked');
});

test('hour slider action identity changes only after its visible hour range advances', t => {
  const f = fixture(t), modal = calendar(f.doc), wanted = { ...target(), fromTime: '13:00' }, context = { activePicker: 'date' };
  assert.equal(f.read(wanted, context).action.id, 'date-hours:next:0-4');
  hourWindow(modal, 5, 9); assert.equal(f.read(wanted, context).action.id, 'date-hours:next:5-9');
  assert.equal(f.read(wanted, context).action.id, 'date-hours:next:5-9');
});
