(function (root, factory) {
  const api = factory();
  if (typeof module === 'object' && module.exports) module.exports = api;
  if (root) root.KorailSiteAdapter = api;
})(typeof globalThis !== 'undefined' ? globalThis : this, function () {
  'use strict';

  // This adapter describes the direct, one-passenger card layout observed on 2026-10-21.
  // It never clicks, changes query fields, or treats the fare/grade selector as a reservation.
  const cardSelector = '#tab_tab_info1 > .tckWrap > ul > li.tckList';
  const clock = '(?:[01]\\d|2[0-3]):[0-5]\\d';
  const normal = text => String(text == null ? '' : text).replace(/\s+/g, ' ').trim();
  const text = node => normal(node && node.textContent);
  const one = (root, selector) => { const found = root ? root.querySelectorAll(selector) : []; return found.length === 1 ? found[0] : null; };
  function shown(node, background) {
    if (!node || node.closest('[hidden]')) return false;
    for (let hidden = node.closest('[aria-hidden="true"]'); hidden; hidden = hidden.parentElement && hidden.parentElement.closest('[aria-hidden="true"]'))
      if (!background || !background.has(hidden)) return false;
    for (let current = node; current && current.nodeType === 1; current = current.parentElement) {
      if (current.style && (current.style.display === 'none' || current.style.visibility === 'hidden')) return false;
      // A modal's accessibility isolation does not visually hide its background.
      // This read-only exception never permits genuinely hidden background copy.
      if (background) {
        const view = current.ownerDocument && current.ownerDocument.defaultView;
        const css = view && view.getComputedStyle(current);
        if (css && (css.display === 'none' || css.visibility === 'hidden' || css.visibility === 'collapse' || css.opacity === '0')) return false;
      }
    }
    const view = node.ownerDocument && node.ownerDocument.defaultView;
    if (view && node.isConnected) { const css = view.getComputedStyle(node); if (css.display === 'none' || css.visibility === 'hidden') return false; }
    return true;
  }
  function enabled(node, background) {
    if (!shown(node, background) || node.disabled || node.closest('[aria-disabled="true"],.disabled')) return false;
    for (let inert = node.closest('[inert]'); inert; inert = inert.parentElement && inert.parentElement.closest('[inert]'))
      if (!background || !background.has(inert)) return false;
    return true;
  }
  function matchesPage(doc) {
    doc = doc || (typeof document !== 'undefined' ? document : null);
    if (!doc) return false;
    let url; try { url = new URL(doc.location ? doc.location.href : doc.URL); } catch { return false; }
    if (url.protocol !== 'https:') return false;
    if (['www.korail.com', 'korail.com'].includes(url.hostname)) return url.pathname === '/ticket/search/list';
    return url.hostname === 'korail-helper.invalid' && url.pathname === '/card-demo.html' && doc.body && doc.body.getAttribute('data-korail-card-demo') === 'true';
  }
  function identity(row) {
    if (!row || !row.matches('li.tckList')) return null;
    const flags = one(row, '.tck_inner > .info_inner .info_box > .tit_box > .flag_wrap');
    const heading = one(row, '.tck_inner > .info_inner .info_box > .data_box > h3');
    if (!flags || !heading) return null;
    const numberNode = one(flags, '.num'), logo = flags.querySelectorAll('.blind');
    if (!numberNode || logo.length > 1) return null;
    let kind, number;
    if (logo.length === 1 && /^\d{1,6}$/.test(text(numberNode))) {
      kind = text(logo[0]).replace(/\s+/g, '').replace(/[–—]/g, '-').toUpperCase();
      if (!/^(?:KTX(?:-(?:산천|이음|청룡))?|ITX-(?:새마을|마음|청춘)|새마을(?:호)?|무궁화(?:호)?|누리로)$/.test(kind)) return null;
      number = String(Number(text(numberNode)));
    } else if (logo.length === 0) {
      const observedTour = /^남도해양열차\s+(\d{1,6})$/.exec(text(numberNode));
      if (!observedTour) return null;
      kind = '남도해양열차'; number = String(Number(observedTour[1]));
    } else return null;
    const spans = Array.from(heading.children);
    if (spans.length !== 3 || spans.some(node => node.tagName !== 'SPAN')) return null;
    const departureStation = text(spans[0]), arrivalStation = text(spans[1]);
    if (!departureStation || !arrivalStation || departureStation === arrivalStation || [departureStation, arrivalStation].some(value => value.length > 40 || /[<>|\d:]/.test(value))) return null;
    const times = new RegExp('^\\((' + clock + ')\\s*~\\s*(' + clock + ')\\)$').exec(text(spans[2]));
    if (!times || normal(heading.textContent).replace(text(spans[0]), '').replace(text(spans[1]), '').replace(text(spans[2]), '').trim() !== '→') return null;
    const train = kind + ' ' + number, departure = times[1], arrival = times[2];
    return { train, departure, arrival, departureStation, arrivalStation, key: [train, departureStation, arrivalStation, departure, arrival].join('|') };
  }
  function dateValid(value) {
    const match = /^(20\d{2})-(\d{2})-(\d{2})$/.exec(value || '');
    if (!match) return false;
    const d = new Date(Date.UTC(+match[1], +match[2] - 1, +match[3]));
    return d.getUTCFullYear() === +match[1] && d.getUTCMonth() === +match[2] - 1 && d.getUTCDate() === +match[3];
  }
  function query(doc) {
    const result = { supported: false, reason: '', departureStation: '', arrivalStation: '', date: '', fromTime: '', passengers: 0, route: '', roundTrip: false, adjacent: false, combined: false, seatAttribute: '' };
    const start = one(doc, '#labelstart'), end = one(doc, '#labelend'), date = one(doc, '#startDate'), people = one(doc, '#labelple');
    const route = one(doc, '.ticketSrchWrap .select.select02 > .selected');
    const seat = one(doc, '.ticketSrchWrap .select.select01 > .selected');
    const round = one(doc, '#rtYn'), adjacent = one(doc, '#adjStnScdlOfrFlg'), combined = one(doc, '#adjStnScdlOfrFlg2');
    if (![start, end, date, people, route, seat, round, adjacent, combined].every(Boolean)) { result.reason = '현재 조회 조건의 구조를 확인하지 못했습니다.'; return result; }
    result.departureStation = normal(start.value); result.arrivalStation = normal(end.value);
    result.route = text(route); result.seatAttribute = text(seat);
    result.roundTrip = round.checked === true; result.adjacent = adjacent.checked === true; result.combined = combined.checked === true;
    const dateMatch = new RegExp('^(20\\d{2}-\\d{2}-\\d{2})(?:\\([월화수목금토일]\\))?\\s+(' + clock + ')$').exec(normal(date.value));
    if (dateMatch) { result.date = dateMatch[1]; result.fromTime = dateMatch[2]; }
    result.passengers = /^총\s*1\s*명$/.test(normal(people.value)) ? 1 : 0;
    if (!result.departureStation || !result.arrivalStation || result.departureStation === result.arrivalStation || !dateValid(result.date)) result.reason = '출발역·도착역·날짜를 확인하세요.';
    else if (result.route !== '직통') result.reason = '이 화면에서는 직통 열차만 지원합니다.';
    else if (result.passengers !== 1) result.reason = '인원 총 1명으로 조회하세요.';
    else if (result.roundTrip || result.adjacent || result.combined) result.reason = '왕복·인접역 보기·수서 함께 보기를 끈 뒤 다시 조회하세요.';
    else result.supported = true;
    return result;
  }
  function cards(doc, background) {
    const conditions = query(doc);
    if (!conditions.supported) return [];
    const panel = one(doc, '#tab_tab_info1');
    if (!panel || !shown(panel, background)) return [];
    return Array.from(doc.querySelectorAll(cardSelector)).filter(row => {
      const id = identity(row);
      return shown(row, background) && id && id.departureStation === conditions.departureStation && id.arrivalStation === conditions.arrivalStation;
    });
  }
  function slots(row) {
    const inner = one(row, ':scope > .tck_inner');
    return inner ? Array.from(inner.children).filter(node => node.classList.contains('price_box')) : [];
  }
  function explicitGrade(box) {
    const label = one(box, ':scope > .inner > a > .txt_ch');
    const match = /^(일반실|특실|우등실)(?:\(매진임박\))?$/.exec(text(label));
    return match ? match[1] : null;
  }
  function slot(row, grade, background) {
    if (!identity(row) || !['일반실', '특실', '우등실'].includes(grade)) return null;
    const boxes = slots(row);
    if (boxes.length !== 2) return null;
    if (grade === '우등실') { const matches = boxes.filter(box => explicitGrade(box) === grade); return matches.length === 1 ? matches[0] : null; }
    const index = grade === '일반실' ? 0 : 1, box = boxes[index], explicit = explicitGrade(box);
    if (explicit && explicit !== grade) return null;
    if (grade === '일반실' && box.classList.contains('spe') || grade === '특실' && box.classList.contains('gen')) return null;
    return shown(box, background) ? box : null;
  }
  function positiveAction(box, grade, background) {
    if (!box || !enabled(box, background) || !box.classList.contains('price_box')) return null;
    if (box.classList.contains('sold_out') || box.classList.contains('yms') || /예약대기|입석|자유석/.test(text(box))) return null;
    if (explicitGrade(box) !== grade) return null;
    if (grade === '일반실' && !box.classList.contains('gen') || grade === '특실' && !box.classList.contains('spe')) return null;
    const anchor = one(box, ':scope > .inner > a'), fare = anchor && one(anchor, '.txt_price');
    if (!enabled(anchor, background) || !/^(?:\d{1,3}(?:,\d{3})*|\d+)원$/.test(text(fare))) return null;
    return anchor;
  }
  function noticeEvidence(doc, dialog, grade) {
    const container = one(doc, '#container');
    if (!container || !dialog || dialog.ownerDocument !== doc || !dialog.isConnected || !shown(dialog) || container.contains(dialog) || !matchesPage(doc)) return null;
    // Only whole-page ancestors outside this recognized dialog may be isolated.
    // A hidden/inert card, panel, or seat remains excluded. No DOM is changed and
    // these observations are never used as coordinates for a background click.
    const background = new Set();
    for (let node = container; node && !node.contains(dialog); node = node.parentElement) background.add(node);
    const rows = cards(doc, background).map(row => {
      const box = slot(row, grade, background);
      return { identity: identity(row), slot: box, available: !!positiveAction(box, grade, background) };
    });
    const active = Array.from(doc.querySelectorAll(cardSelector + ' > .tck_inner > .price_box.active')).filter(node => shown(node, background));
    return { rows, active };
  }
  function reservationAction(doc, row, grade) {
    const conditions = query(doc), id = identity(row);
    if (!conditions.supported || !id || id.departureStation !== conditions.departureStation || id.arrivalStation !== conditions.arrivalStation) return null;
    const wanted = slot(row, grade), selection = positiveAction(wanted, grade);
    if (!selection) return null;
    const active = Array.from(doc.querySelectorAll(cardSelector + ' > .tck_inner > .price_box.active')).filter(node => shown(node));
    if (active.length !== 1 || active[0] !== wanted || normal(selection.getAttribute('title')) !== '선택') return null;
    const drawer = one(doc, '#container > .ticket_reserv_wrap');
    if (!drawer || !shown(drawer)) return null;
    const rows = drawer.querySelectorAll('.ticket_reserv'), gradeList = one(drawer, '.reserv_first');
    if (rows.length !== 1 || !gradeList || gradeList.children.length < 1 || text(gradeList.children[0]) !== grade) return null;
    const button = one(drawer, '.reservbtnWrap > button.reservbtn');
    return enabled(button) && text(button) === '예매' ? button : null;
  }
  function activeRefresh(doc) {
    if (!query(doc).supported) return null;
    const candidates = Array.from(doc.querySelectorAll('#btn_tab_info1 > button,#btn_tab_info2 > button,#btn_tab_info3 > button,#btn_tab_info4 > button,#btn_tab_info5 > button'))
      .filter(button => button.getAttribute('aria-pressed') === 'true' && button.getAttribute('aria-controls') === 'tab_tab_info1' && shown(button));
    if (candidates.length !== 1) return null;
    const element = candidates[0], label = text(element);
    if (!label) return null;
    return { element, selector: '#' + element.parentElement.id + ' > button', label };
  }
  return Object.freeze({ matchesPage, identity, cards, slot, positiveAction, noticeEvidence, reservationAction, query, activeRefresh, cardSelector });
});
