(function (root, factory) {
  'use strict';
  const api = factory(root);
  if (typeof module === 'object' && module.exports) module.exports = api;
  if (root) root.KorailMouseBooking = api;
})(typeof globalThis !== 'undefined' ? globalThis : this, function (root) {
  'use strict';

  // Read-only observations for the native input host. No click, scroll, input,
  // refresh, or network operation belongs here. The host re-reads after input.
  const normalize = value => String(value == null ? '' : value).replace(/\s+/g, ' ').trim();
  const escape = value => String(value).replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const clock = /^(?:[01]\d|2[0-3]):[0-5]\d$/;
  const station = value => normalize(value).replace(/역$/, '');
  const actionSelector = 'button,a,input[type=button],input[type=submit],[role=button]';
  const trainPattern = /(?:^|[^\p{L}\p{N}])(KTX(?:\s*[-–—]?\s*(?:산천|이음|청룡))?|ITX\s*[-–—]?\s*(?:새마을|마음|청춘)|새마을(?:호)?|무궁화(?:호)?|누리로|남도해양열차)\s*(\d{1,6})(?![\d:])/giu;
  const result = (state, message) => ({ state, message });

  function trainsIn(value) {
    return Array.from(normalize(value).matchAll(trainPattern), match => {
      const kind = match[1].replace(/\s+/g, '').replace(/[–—]/g, '-').toUpperCase()
        .replace(/^(KTX|ITX)-?(산천|이음|청룡|새마을|마음|청춘)$/, '$1-$2');
      return kind + ' ' + Number(match[2]);
    });
  }
  function datesIn(value) {
    return Array.from(String(value).matchAll(/(20\d{2})\s*(?:[-./]|년)\s*(\d{1,2})\s*(?:[-./]|월)\s*(\d{1,2})(?:\s*일)?/g), match =>
      match[1] + '-' + match[2].padStart(2, '0') + '-' + match[3].padStart(2, '0'));
  }
  function targetError(target) {
    if (!target || target.passengers !== 1) return '인원 1명의 여정을 선택하세요.';
    const date = /^(20\d{2})-(\d{2})-(\d{2})$/.exec(target.date || '');
    if (!date || new Date(Date.UTC(+date[1], +date[2] - 1, +date[3])).toISOString().slice(0, 10) !== target.date) return '선택 날짜가 올바르지 않습니다.';
    if (!station(target.departureStation) || !station(target.arrivalStation) || station(target.departureStation) === station(target.arrivalStation)) return '출발역과 도착역을 확인하세요.';
    if (!clock.test(target.departure || '') || !clock.test(target.arrival || '')) return '열차 출발·도착시각이 올바르지 않습니다.';
    if (!['일반실', '특실', '우등실'].includes(target.grade)) return '좌석 등급을 확인하세요.';
    const trains = trainsIn(target.train);
    if (trains.length !== 1 || normalize(target.train).replace(/\s/g, '') !== trains[0].replace(/\s/g, '')) return '정확한 열차 종류와 번호를 선택하세요.';
    return null;
  }
  function queryError(query, target) {
    if (!query || !query.supported) return query && query.reason || '조회 조건을 확인할 수 없습니다.';
    if (query.date !== target.date || station(query.departureStation) !== station(target.departureStation) || station(query.arrivalStation) !== station(target.arrivalStation) || query.passengers !== 1)
      return '화면의 날짜·구간·인원이 선택한 여정과 다릅니다.';
    return null;
  }
  function sameTrain(identity, target) {
    return !!identity && trainsIn(identity.train)[0] === trainsIn(target.train)[0] && identity.departure === target.departure && identity.arrival === target.arrival &&
      station(identity.departureStation) === station(target.departureStation) && station(identity.arrivalStation) === station(target.arrivalStation);
  }
  function classifyNotice(value) {
    const text = normalize(value).replace(/^안내(?:\s*메시지)?\s*/, '').replace(/\s/g, '');
    const match = /^([가-힣]{1,20})정차하는열차입니다\.도착시간을확인하시기바랍니다\.$/.exec(text);
    return match ? { allowed: true, station: match[1] } : { allowed: false, station: '' };
  }
  function couplingNotice(value) {
    const copy = normalize(value).replace(/\s/g, '').replace(/^이용안내/, '');
    return copy === '선택하신열차는KTX-산천2개편성을연결하여운행하는열차로서,반드시열차번호와해당호차를확인하시고승차하시기바랍니다.';
  }
  function forbiddenSeat(value) { return /예약\s*대기|입석|자유석/.test(normalize(value)); }
  function explicitFailure(value) {
    return /(?:예약|예매)(?:이|가)?\s*(?:실패|불가|불가능|완료되지|되지\s*않)|(?:예약|예매)(?:이|가)\s*취소|(?:예약|예매)\s*취소\s*(?:완료|되었습니다|상태)|매진|좌석이\s*없|예약\s*대기|입석|자유석/.test(normalize(value));
  }
  function paidOrIssued(value) {
    return /결제(?:가|를)?\s*완료(?!되지)|발권(?:이|을)?\s*완료(?!되지)|결제\s*상태\s*[:：]?\s*완료(?!되지)|발권\s*상태\s*[:：]?\s*완료(?!되지)|결제됨|발권됨/.test(normalize(value));
  }

  // The real reservation-complete DOM has NOT been captured or verified.
  // This conservative parser is provisional: a URL/title/button alone never
  // establishes success, and an unknown structure remains unconfirmed.
  function parseReceipt(value, target) {
    if (targetError(target)) return null;
    const text = normalize(value);
    if (!/(?:승차권\s*)?(?:예약|예매)(?:이|가)?\s*(?:정상적으로\s*)?완료\s*되었습니다[.!]?/.test(text) || explicitFailure(text) || paidOrIssued(text)) return null;
    const numbers = Array.from(text.matchAll(/예약\s*번호\s*[:：]?\s*([A-Z0-9]+(?:-[A-Z0-9]+)*)/gi), match => match[1]);
    if (numbers.length !== 1 || !/^(?=.{6,40}$)(?=.*\d)[A-Z0-9]+(?:-[A-Z0-9]+)*$/i.test(numbers[0])) return null;
    const trains = trainsIn(text);
    if (trains.length !== 1 || trains[0] !== trainsIn(target.train)[0]) return null;
    const dates = datesIn(text);
    if (!dates.length || dates[0] !== target.date) return null;
    for (const name of [target.departureStation, target.arrivalStation]) {
      if (!new RegExp('(?:^|[^가-힣A-Za-z])' + escape(station(name)) + '(?:역)?(?=$|[^가-힣A-Za-z])').test(text)) return null;
    }
    const route = /출발역\s*[:：]?\s*([가-힣A-Za-z·]+).*?도착역\s*[:：]?\s*([가-힣A-Za-z·]+)/.exec(text) || /([가-힣A-Za-z·]+)\s*(?:→|->)\s*([가-힣A-Za-z·]+)/.exec(text);
    if (!route || station(route[1]) !== station(target.departureStation) || station(route[2]) !== station(target.arrivalStation)) return null;
    const times = Array.from(text.matchAll(/(?<![\d:])(?:[01]\d|2[0-3]):[0-5]\d(?![\d:])/g), match => match[0]);
    if (times.length < 2 || times[0] !== target.departure || times[1] !== target.arrival) return null;
    const grades = [...new Set(text.match(/일반실|특실|우등실/g) || [])];
    if (grades.length !== 1 || grades[0] !== target.grade) return null;
    const people = Array.from(text.matchAll(/(?:총|인원|성인|어른)\s*[:：]?\s*(\d+)\s*명/g), match => +match[1]);
    if (!people.length || people.some(count => count !== 1)) return null;
    const deadline = /(?:결제|발권)\s*기한\s*[:：]?\s*((?:20\d{2}\s*(?:[-./]|년)\s*\d{1,2}\s*(?:[-./]|월)\s*\d{1,2}(?:\s*일)?\s*(?:\([월화수목금토일]\))?\s*)?(?:[01]\d|2[0-3]):[0-5]\d(?:\s*까지)?)/.exec(text);
    const unpaid = /미\s*결제|결제\s*대기|결제\s*전(?:\s|$|[.:：])|결제(?:가|를)?\s*(?:완료되지|하지\s*않)/.test(text);
    if (!unpaid) return null;
    const observedTarget = { date: dates[0], departureStation: station(route[1]), arrivalStation: station(route[2]), train: trains[0],
      departure: times[0], arrival: times[1], grade: grades[0], passengers: people[0] };
    return { reservationNumber: numbers[0], paymentDeadline: deadline ? normalize(deadline[1]) : '', evidenceText: text.slice(0, 1800), observedTarget };
  }
  // Based on the text and semantic list item observed on the official detail
  // screen, not a captured DOM. A seat-detail receipt has no invented PNR.
  function parseSeatDetail(value, target, now) {
    if (targetError(target)) return null;
    const valueText = normalize(value);
    if (explicitFailure(valueText) || paidOrIssued(valueText) || /기한\s*(?:만료|초과)|예약\s*만료/.test(valueText)) return null;
    const deadlines = Array.from(valueText.matchAll(/결제\s*기한\s*[:：]?\s*(20\d{2})\s*(?:[-./]|년)\s*(\d{1,2})\s*(?:[-./]|월)\s*(\d{1,2})(?:\s*일|\.)?\s+((?:[01]\d|2[0-3]):[0-5]\d)/g));
    if (deadlines.length !== 1) return null;
    const deadline = deadlines[0], deadlineDate = `${deadline[1]}-${deadline[2].padStart(2, '0')}-${deadline[3].padStart(2, '0')}`;
    if (new Date(Date.UTC(+deadline[1], +deadline[2] - 1, +deadline[3])).toISOString().slice(0, 10) !== deadlineDate) return null;
    const deadlineUtc = Date.parse(deadlineDate + 'T' + deadline[4] + ':00+09:00');
    const observedNow = now === undefined ? Date.now() : now;
    if (!Number.isFinite(observedNow) || !Number.isFinite(deadlineUtc) || deadlineUtc <= observedNow || deadlineUtc > Date.parse(target.date + 'T' + target.departure + ':00+09:00')) return null;
    const beforeDeadline = valueText.slice(0, deadline.index), dates = datesIn(beforeDeadline), trains = trainsIn(valueText);
    if (dates.length !== 1 || dates[0] !== target.date || trains.length !== 1 || trains[0] !== trainsIn(target.train)[0]) return null;
    const routes = Array.from(valueText.matchAll(/([가-힣A-Za-z·]+)\s*(?:→|->)\s*([가-힣A-Za-z·]+)\s*\(\s*((?:[01]\d|2[0-3]):[0-5]\d)\s*[~～–-]\s*((?:[01]\d|2[0-3]):[0-5]\d)\s*\)/g));
    if (routes.length !== 1) return null;
    const route = routes[0];
    if (station(route[1]) !== station(target.departureStation) || station(route[2]) !== station(target.arrivalStation) || route[3] !== target.departure || route[4] !== target.arrival) return null;
    const grades = [...new Set(valueText.match(/일반실|특실|우등실/g) || [])];
    if (grades.length !== 1 || grades[0] !== target.grade) return null;
    const seatMatches = Array.from(valueText.matchAll(/(일반실|특실|우등실)\s*([1-9]\d?)\s*호차\s*([1-9]\d{0,2}[A-Z])(?![A-Z\d])/g));
    const seats = [...new Set(seatMatches.map(match => `${Number(match[2])}호차 ${match[3]}`))];
    if (seats.length !== 1 || seatMatches.some(match => match[1] !== target.grade)) return null;
    // A second seat lacking its own repeated grade is still ambiguous.
    const allSeats = [...new Set(Array.from(valueText.matchAll(/([1-9]\d?)\s*호차\s*([1-9]\d{0,2}[A-Z])(?![A-Z\d])/g), match => `${Number(match[1])}호차 ${match[2]}`))];
    if (allSeats.length !== 1 || allSeats[0] !== seats[0]) return null;
    const counts = Array.from(valueText.matchAll(/(?:총|인원|성인|어른)\s*[:：]?\s*(\d+)\s*명/g), match => +match[1]);
    if (counts.some(count => count !== 1)) return null;
    return { evidenceType: 'seat-detail', reservationNumber: null, seat: seats[0], paymentDeadline: deadlineDate.replace(/-/g, '. ') + '. ' + deadline[4],
      evidenceText: valueText.slice(0, 1800), observedTarget: { date: dates[0], departureStation: station(route[1]), arrivalStation: station(route[2]),
        train: trains[0], departure: route[3], arrival: route[4], grade: grades[0], passengers: 1 } };
  }
  function officialDetail(url) {
    return url.protocol === 'https:' && ['www.korail.com', 'korail.com'].includes(url.hostname) && !url.port && url.pathname === '/ticket/reservation/detail';
  }
  function actionLabel(node) { return text(node) || normalize(node.getAttribute && node.getAttribute('aria-label')); }
  function detailReceipt(doc, target, now) {
    const actions = Array.from(doc.querySelectorAll(actionSelector)).filter(enabled);
    if (actions.filter(node => actionLabel(node) === '결제하기').length !== 1) return null;
    const cards = Array.from(doc.querySelectorAll('li,[role="listitem"]')).filter(shown).filter(node => {
      const controls = Array.from(node.querySelectorAll(actionSelector)).filter(enabled);
      const value = text(node);
      return controls.some(control => actionLabel(control) === '예약취소') && controls.some(control => actionLabel(control) === '장바구니') ||
        trainsIn(value).length > 0 && datesIn(value).length > 0 && /(?:→|->).*\d{2}:\d{2}\s*[~～–-]\s*\d{2}:\d{2}/.test(value);
    });
    const smallest = cards.filter(node => !cards.some(other => other !== node && node.contains(other)));
    if (smallest.length !== 1) return null;
    const card = smallest[0], controls = Array.from(card.querySelectorAll(actionSelector)).filter(enabled);
    if (controls.filter(node => actionLabel(node) === '예약취소').length !== 1 || controls.filter(node => actionLabel(node) === '장바구니').length !== 1) return null;
    // Reject multiple receipts even if one currently has disabled controls.
    if (Array.from(doc.querySelectorAll(actionSelector)).filter(shown).filter(node => actionLabel(node) === '예약취소').length !== 1) return null;
    const page = text(doc.body);
    if (paidOrIssued(page) || /기한\s*(?:만료|초과)|예약\s*만료/.test(page)) return null;
    if (!/승차권\s*예약/.test(page) || !/결제\s*기한\s*내\s*미결제\s*시\s*승차권이\s*자동\s*취소됩니다\.?/.test(page)) return null;
    return parseSeatDetail(text(card), target, now);
  }
  function refundNoticeAction(dialog, doc) {
    const value = text(dialog).replace(/\s/g, '');
    const required = ['환불(반환)위약금안내', '평시승차권', '광명역공항버스',
      '승차권에기재된출발일,출발시각및영수금액을기준으로환불위약금이상이하니상단탭을눌러해당환불(반환)위약금을반드시확인하시기바랍니다.',
      '출발후에는역창구에서만환불청구가능합니다', '최저위약금은400원입니다.', '위약금면제대상'];
    if (!required.every(part => value.includes(part)) || /동의|결제하기|보안문자|인증번호|로그인|자동화된요청/.test(value)) return null;
    // The observed table caption can be visually hidden for screen readers.
    // Only this exact caption inside the already recognized dialog may supply
    // the caption evidence; hidden body copy never supplies the other phrases.
    const captions = Array.from(dialog.querySelectorAll('caption'));
    const exactCaption = captions.length === 1 && normalize(captions[0].textContent).replace(/\s/g, '') === '환불(위약금)안내표';
    if (!value.includes('환불(위약금)안내표') && !exactCaption) return null;
    if (Array.from(dialog.querySelectorAll('input,textarea,select,[contenteditable="true"]')).some(shown)) return null;
    const actions = Array.from(dialog.querySelectorAll(actionSelector)).filter(enabled);
    const refundLabel = node => {
      const label = text(node), accessible = normalize(node.getAttribute('aria-label'));
      if (accessible === '레이어닫기' && ['', '레이어닫기', '×', '✕'].includes(label)) return '레이어닫기';
      return actionLabel(node);
    };
    if (actions.some(node => !['확인', '레이어닫기', '평시 승차권', '광명역 공항버스'].includes(refundLabel(node)))) return null;
    const confirms = actions.filter(node => refundLabel(node) === '확인'), closes = actions.filter(node => refundLabel(node) === '레이어닫기');
    if (confirms.length !== 1 || closes.length !== 1) return null;
    return actionFor(confirms[0], 'receipt-notice', doc);
  }
  function shown(node) {
    if (!node || node.isConnected === false || node.closest('[hidden],[aria-hidden="true"]')) return false;
    const view = node.ownerDocument && node.ownerDocument.defaultView;
    for (let current = node; current && current.nodeType === 1; current = current.parentElement) {
      const css = view && view.getComputedStyle ? view.getComputedStyle(current) : current.style;
      if (css && (css.display === 'none' || css.visibility === 'hidden' || css.visibility === 'collapse' || css.opacity === '0')) return false;
    }
    return !node.getClientRects || node.getClientRects().length > 0;
  }
  function text(node) { return normalize(node && (typeof node.innerText === 'string' ? node.innerText : node.textContent)); }
  function enabled(node) { return shown(node) && !node.disabled && !node.closest('[inert],[aria-disabled="true"],.disabled'); }
  function visibleDialogs(doc) {
    let candidates = Array.from(doc.querySelectorAll('[role="dialog"],[role="alertdialog"],[aria-modal="true"]')).filter(shown);
    if (!candidates.length) candidates = Array.from(doc.querySelectorAll('.modal.show,.modal.on,.modal[open],.popup.show,.popup.on,.modal_wrap,.modal-wrap,.popup_wrap,.pop_wrap,.popWrap')).filter(shown);
    // Nested wrappers of one dialog are one modal; distinct dialogs stay distinct.
    return candidates.filter(node => !candidates.some(other => other !== node && node.contains(other)));
  }
  function modalText(node) {
    const pieces = [];
    function visit(current) {
      if (current.nodeType === 3) { pieces.push(current.textContent); return; }
      if (current.nodeType !== 1 || !shown(current) || current.matches(actionSelector)) return;
      for (const child of current.childNodes) visit(child);
    }
    visit(node);
    return normalize(pieces.join(' '));
  }
  function actionFor(element, kind, doc) {
    if (!enabled(element)) return result('blocked', '동작할 버튼이 숨겨졌거나 비활성 상태입니다.');
    const view = doc.defaultView, rect = element.getBoundingClientRect();
    const viewportWidth = view && view.innerWidth || doc.documentElement.clientWidth;
    const viewportHeight = view && view.innerHeight || doc.documentElement.clientHeight;
    const x = rect.left + rect.width / 2, y = rect.top + rect.height / 2;
    if (![x, y, rect.width, rect.height, viewportWidth, viewportHeight].every(Number.isFinite) || rect.width <= 0 || rect.height <= 0 || viewportWidth <= 0 || viewportHeight <= 0)
      return result('blocked', '버튼의 화면 위치를 확인하지 못했습니다.');
    const inViewport = x >= 0 && y >= 0 && x < viewportWidth && y < viewportHeight;
    if (inViewport) {
      const top = doc.elementFromPoint(x, y);
      if (!top || !(top === element || element.contains(top))) return result('blocked', '다른 화면 요소가 버튼을 가리고 있습니다.');
    }
    return { state: 'ready', message: inViewport ? '화면에서 확인한 버튼입니다.' : '버튼이 화면 밖에 있습니다. 스크롤 후 다시 확인하세요.',
      action: { kind, x, y, width: rect.width, height: rect.height, viewportWidth, viewportHeight, inViewport } };
  }
  function observe(target, phase, suppliedDocument, now) {
    const invalid = targetError(target);
    if (invalid) return result('blocked', invalid);
    if (!['select', 'drawer', 'submitted'].includes(phase)) return result('blocked', '알 수 없는 예약 단계입니다.');
    const doc = suppliedDocument || root.document;
    const S = root.KorailSiteAdapter, C = root.KorailHelperCore;
    if (!doc || !doc.body || !S || !C) return result('unknown', '화면 분석기가 준비되지 않았습니다.');
    if (doc.hidden) return result('blocked', '프로그램 화면이 숨겨졌습니다.');
    let url; try { url = new URL(doc.location ? doc.location.href : doc.URL); } catch { return result('blocked', '현재 주소를 확인하지 못했습니다.'); }
    const localFixture = url.hostname === 'korail-helper.invalid' && url.pathname === '/card-demo.html';
    if (url.protocol !== 'https:' || (!localFixture && (!['www.korail.com', 'korail.com'].includes(url.hostname) || !url.pathname.startsWith('/ticket/'))))
      return result('blocked', '코레일 승차권 화면이 아닙니다.');
    const pageText = text(doc.body), dialogs = visibleDialogs(doc), blocker = C.blocker(pageText);
    if (blocker && blocker !== '안내 메시지 확인 필요') {
      const code = /CODE\s*:\s*(-\d{1,6})/i.exec(pageText);
      return { ...result('blocked', blocker), blockCode: code ? code[1] : '' };
    }
    if (Array.from(doc.querySelectorAll('iframe[src*="captcha"],iframe[src*="challenge"],input[autocomplete="one-time-code"]')).some(shown))
      return result('blocked', '인증 화면을 직접 확인하세요.');
    if (dialogs.length) {
      if (phase === 'submitted' && officialDetail(url) && dialogs.length === 1) {
        const notice = refundNoticeAction(dialogs[0], doc);
        if (notice) return notice;
      }
      if (dialogs.length !== 1) return result('blocked', '인식하지 못한 안내창입니다. 내용을 직접 확인하세요.');
      // This informational coupling notice was observed after the single reserve
      // input, while the exact selected card remains on the official list page.
      // Its only permitted action is acknowledgement, never another reservation.
      const coupled = phase === 'submitted' && !url.port && ['www.korail.com', 'korail.com'].includes(url.hostname) && url.pathname === '/ticket/search/list' &&
        /^KTX-산천 /.test(trainsIn(target.train)[0]) && couplingNotice(modalText(dialogs[0]));
      if (!coupled && (phase !== 'drawer' || !classifyNotice(modalText(dialogs[0])).allowed)) return result('blocked', '인식하지 못한 안내창입니다. 내용을 직접 확인하세요.');
      const buttons = Array.from(dialogs[0].querySelectorAll(actionSelector)).filter(enabled);
      if (coupled && Array.from(dialogs[0].querySelectorAll(actionSelector)).filter(shown).length !== 1) return result('blocked', '이용안내의 확인 버튼 이외에 다른 동작이 표시됩니다.');
      const buttonLabel = buttons.length === 1 ? text(buttons[0]) || normalize(buttons[0].getAttribute('aria-label')) : '';
      if (coupled ? buttonLabel !== '확인' : !/^(확인|닫기)$/.test(buttonLabel)) return result('blocked', '안내창의 확인 버튼을 하나로 식별하지 못했습니다.');
      if (coupled && Array.from(dialogs[0].querySelectorAll('input,textarea,select,[contenteditable="true"]')).some(shown)) return result('blocked', '입력이 필요한 안내창은 직접 확인하세요.');
      // Permitted informational notices never relax the selected query or train.
      const mismatch = queryError(S.query(doc), target);
      if (mismatch) return result('blocked', mismatch);
      const evidence = S.noticeEvidence(doc, dialogs[0], target.grade);
      if (!evidence) return result('blocked', '안내창 뒤의 조회 영역을 확인하지 못했습니다.');
      const matches = evidence.rows.filter(row => sameTrain(row.identity, target));
      if (matches.length !== 1) return result('blocked', '안내창 뒤의 선택 열차를 확인하지 못했습니다.');
      const slot = matches[0].slot;
      if (!slot || forbiddenSeat(text(slot)) || !matches[0].available) return result('blocked', '안내창 뒤의 선택 좌석 상태를 확인하지 못했습니다.');
      const active = evidence.active;
      if (active.length > 1 || active.length === 1 && active[0] !== slot) return result('blocked', '안내창 뒤의 선택 좌석이 중복되거나 변경됐습니다.');
      if (coupled) {
        const selected = Array.from(slot.querySelectorAll(':scope > .inner > a'));
        if (active.length !== 1 || selected.length !== 1 || normalize(selected[0].getAttribute('title')) !== '선택') return result('blocked', '예매 요청 뒤의 선택 열차·등급을 확인하지 못했습니다.');
      }
      return actionFor(buttons[0], coupled ? 'receipt-notice' : 'notice', doc);
    }
    if (blocker) return result('blocked', blocker);
    if (phase === 'submitted') {
      const alerts = Array.from(doc.querySelectorAll('[role="alert"]')).filter(shown);
      if (alerts.some(node => explicitFailure(text(node)) || C.blocker(text(node)))) return result('blocked', '예약 처리 안내에 오류가 표시됐습니다. 내용을 직접 확인하세요.');
      if (officialDetail(url)) {
        const receipt = detailReceipt(doc, target, now);
        return receipt ? { state: 'completed', message: '선택한 여정의 좌석·미결제 예약 상세와 유효한 결제기한을 확인했습니다.', receipt }
          : result('unknown', '선택한 여정의 단일 좌석·미결제 예약 상세를 아직 확인하지 못했습니다.');
      }
      // Submission may leave the old list visible for a while. Other trains'
      // sold-out labels and the drawer's free-seat information are not failures.
      if (S.matchesPage(doc) && S.query(doc).supported) return result('unknown', '예매 요청 후 조회 화면이 아직 표시됩니다. 결과를 기다립니다.');
      const selector = '[data-reservation-detail],.reservation-detail,.reservation_detail,.reservationDetail,.reservation_info,.reservation-info,.ticket-reservation-detail,.reserv_detail,main,[role="main"],#container';
      const regions = Array.from(doc.querySelectorAll(selector)).filter(shown);
      const smallest = regions.filter(node => !regions.some(other => other !== node && node.contains(other)));
      if (smallest.some(node => explicitFailure(text(node)) || paidOrIssued(text(node)))) return result('blocked', '예약 상세가 실패·취소 또는 결제·발권 완료 상태입니다. 결과를 직접 확인하세요.');
      const receipts = smallest.map(node => parseReceipt(text(node), target)).filter(Boolean);
      if (receipts.length === 1 && smallest.length === 1) return { state: 'completed', message: '선택한 여정의 예약 완료·미결제 상태를 확인했습니다. 결제기한을 확인하고 직접 결제하세요.', receipt: receipts[0] };
      return result('unknown', smallest.length > 1 ? '여러 예약 상세가 표시되어 결과를 확정할 수 없습니다.' : '예약 완료·미결제 상태를 아직 확인하지 못했습니다.');
    }
    if (!S.matchesPage(doc)) return result('blocked', '열차 조회 화면에서 시작하세요.');
    const mismatch = queryError(S.query(doc), target);
    if (mismatch) return result('blocked', mismatch);
    const matches = S.cards(doc).filter(row => sameTrain(S.identity(row), target));
    if (matches.length !== 1) return { ...result(matches.length ? 'blocked' : 'waiting', matches.length ? '같은 열차가 여러 개 보여 중지합니다.' : '선택한 열차가 아직 화면에 없습니다.'), refreshEligible: false };
    const row = matches[0], slot = S.slot(row, target.grade);
    if (!slot) return result('blocked', '선택한 좌석 등급을 확인하지 못했습니다.');
    if (forbiddenSeat(text(slot))) return result('blocked', '입석·자유석·예약대기는 선택한 좌석이 아닙니다.');
    const active = Array.from(doc.querySelectorAll(S.cardSelector + ' > .tck_inner > .price_box.active')).filter(shown);
    if (active.length > 1 || active.length === 1 && active[0] !== slot) return result('blocked', '선택 좌석이 중복되거나 다른 열차·등급이 선택됐습니다.');
    const seat = S.positiveAction(slot, target.grade);
    if (!seat) {
      const drawerVisible = Array.from(doc.querySelectorAll('#container > .ticket_reserv_wrap')).some(shown);
      return { ...result('waiting', '선택한 등급의 구매 가능한 가격이 표시되지 않았습니다.'),
        refreshEligible: phase === 'select' && /^매진$/.test(text(slot)) && active.length === 0 && !drawerVisible };
    }
    if (phase === 'select') return actionFor(seat, 'seat', doc);
    const reserve = S.reservationAction(doc, row, target.grade);
    if (!reserve) return result('unknown', '선택한 열차와 등급의 예매 버튼을 확인하지 못했습니다.');
    return actionFor(reserve, 'reserve', doc);
  }
  function read(target, phase, suppliedDocument, now) {
    try { return observe(target, phase, suppliedDocument, now); }
    catch { return result('unknown', '화면 구조가 바뀌어 현재 단계를 확인하지 못했습니다.'); }
  }
  function readMany(targets, suppliedDocument, now) {
    try {
      if (!Array.isArray(targets) || targets.length < 1 || targets.length > 20)
        return result('blocked', '후보 열차를 1개 이상 20개 이하로 선택하세요.');
      const first = targets[0], numbers = new Set();
      for (const target of targets) {
        const invalid = targetError(target);
        if (invalid) return result('blocked', invalid);
        if (target.date !== first.date || station(target.departureStation) !== station(first.departureStation) ||
          station(target.arrivalStation) !== station(first.arrivalStation) || target.grade !== first.grade || target.passengers !== first.passengers)
          return result('blocked', '모든 후보는 같은 날짜·구간·좌석 등급·인원이어야 합니다.');
        const number = /\d+$/.exec(trainsIn(target.train)[0])[0];
        if (numbers.has(number)) return result('blocked', '같은 열차 번호가 후보 목록에 중복됐습니다.');
        numbers.add(number);
      }
      const doc = suppliedDocument || root.document, S = root.KorailSiteAdapter;
      // One synchronous observation pass on the same document. No input,
      // navigation, timer or DOM mutation can be initiated by this reader.
      const plans = targets.map(target => observe(target, 'select', doc, now));
      const blocked = plans.find(plan => plan.state === 'blocked');
      if (blocked) return blocked;
      // A selection that predates this pass belongs to another booking phase.
      // Even a one-candidate call must not reuse its already active price box.
      if (doc && S && (Array.from(doc.querySelectorAll(S.cardSelector + ' > .tck_inner > .price_box.active')).some(shown) ||
        Array.from(doc.querySelectorAll('#container > .ticket_reserv_wrap')).some(shown)))
        return result('blocked', '이미 선택된 좌석 또는 예매 영역이 있습니다. 내용을 직접 확인하세요.');
      const uncertain = plans.find(plan => plan.state !== 'ready' && !(plan.state === 'waiting' && plan.refreshEligible === true) ||
        plan.state === 'ready' && (!plan.action || plan.action.kind !== 'seat'));
      if (uncertain) return result('unknown', uncertain.message || '후보 열차의 좌석 상태를 모두 확인하지 못했습니다.');
      const index = plans.findIndex(plan => plan.state === 'ready');
      if (index >= 0) return { ...plans[index], selectedTarget: { ...targets[index] } };
      return { ...result('waiting', '선택한 모든 후보 열차의 좌석이 매진입니다.'), refreshEligible: true };
    } catch { return result('unknown', '화면 구조가 바뀌어 후보 열차의 상태를 확인하지 못했습니다.'); }
  }
  return Object.freeze({ read, readMany, targetError, queryError, sameTrain, classifyNotice, forbiddenSeat, parseReceipt, parseSeatDetail, actionFor });
});
