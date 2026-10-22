(function (root, factory) {
  'use strict';
  const api = factory(root);
  if (typeof module === 'object' && module.exports) module.exports = api;
  if (root) root.KorailMouseRefresh = api;
})(typeof globalThis !== 'undefined' ? globalThis : this, function (root) {
  'use strict';
  // Read-only support for the native host. There are no clicks, field changes,
  // timers, retries, or network requests here. The observed card DOM is from
  // 2026-10-21; unrecognized future layouts remain unconfirmed.
  const listSelector = '#tab_tab_info1 > .tckWrap > ul';
  const panelSelector = '#tab_tab_info1';
  const loadingSelector = '[role="progressbar"],[aria-busy="true"]';
  const watchers = new WeakMap();
  const normal = value => String(value == null ? '' : value).replace(/\s+/g, ' ').trim();
  const station = value => normal(value).replace(/역$/, '');
  const text = node => normal(node && (typeof node.innerText === 'string' ? node.innerText : node.textContent));
  const inside = (parent, child) => !!parent && !!child && (parent === child || parent.contains(child));
  function shown(node) {
    if (!node || node.isConnected === false || node.closest('[hidden],[aria-hidden="true"]')) return false;
    const view = node.ownerDocument && node.ownerDocument.defaultView;
    for (let current = node; current && current.nodeType === 1; current = current.parentElement) {
      const css = view && view.getComputedStyle ? view.getComputedStyle(current) : current.style;
      if (css && (css.display === 'none' || css.visibility === 'hidden' || css.visibility === 'collapse' || css.opacity === '0')) return false;
    }
    return !node.getClientRects || node.getClientRects().length > 0;
  }
  function one(doc, selector) { const found = doc.querySelectorAll(selector); return found.length === 1 ? found[0] : null; }
  function blocked(message, sourceText) {
    const code = /CODE\s*:\s*(-\d{1,6})/i.exec(normal(sourceText));
    return { state: 'blocked', message, terminal: true, retryAllowed: false, blockCode: code ? code[1] : '' };
  }
  function queryFingerprint(query, refresh) {
    return JSON.stringify({ date: query.date, departureStation: query.departureStation, arrivalStation: query.arrivalStation,
      passengers: query.passengers, fromTime: query.fromTime, route: query.route, seatAttribute: query.seatAttribute,
      roundTrip: query.roundTrip, adjacent: query.adjacent, combined: query.combined, filter: refresh.selector, filterLabel: refresh.label });
  }
  function sameQuery(expected, query, fingerprint) {
    if (!expected) return true;
    if (expected.queryFingerprint && expected.queryFingerprint !== fingerprint) return false;
    for (const key of ['date', 'passengers', 'fromTime', 'route', 'seatAttribute', 'roundTrip', 'adjacent', 'combined'])
      if (Object.prototype.hasOwnProperty.call(expected, key) && expected[key] !== query[key]) return false;
    for (const key of ['departureStation', 'arrivalStation'])
      if (Object.prototype.hasOwnProperty.call(expected, key) && station(expected[key]) !== station(query[key])) return false;
    return true;
  }
  function fingerprint(rows, adapter) {
    return JSON.stringify(rows.map(row => ({ identity: adapter.identity(row), seats: ['일반실', '특실', '우등실'].map(grade => {
      const slot = adapter.slot(row, grade);
      return { grade, text: text(slot), available: !!adapter.positiveAction(slot, grade) };
    }) })));
  }
  function relevantMutation(record, previous, current) {
    const roots = [previous.list, current.list].filter(Boolean);
    if (record.type === 'childList' || record.type === 'characterData') {
      if (roots.some(node => inside(node, record.target))) return true;
      if (record.type === 'childList') {
        const changes = [...record.addedNodes, ...record.removedNodes];
        if (changes.some(node => roots.some(list => inside(node, list)))) return true;
        if ([previous.panel, current.panel].some(panel => inside(panel, record.target)) && changes.some(node => node.nodeType === 1 &&
          (node.matches(loadingSelector) || node.querySelectorAll(loadingSelector).length))) return true;
      }
    }
    if (record.type === 'attributes') {
      if (['disabled', 'aria-disabled'].includes(record.attributeName) && [previous.button, current.button].includes(record.target)) return true;
      if (record.attributeName === 'aria-busy' && [previous.panel, current.panel].some(panel => inside(panel, record.target))) return true;
    }
    return false;
  }
  function watch(doc, refresh) {
    const locate = () => {
      const active = root.KorailSiteAdapter.activeRefresh(doc);
      return { list: one(doc, listSelector), panel: one(doc, panelSelector), button: active ? active.element : refresh.element };
    };
    let state = watchers.get(doc);
    if (!state) {
      state = { revision: 0, previous: locate(), observer: null };
      const Observer = doc.defaultView && doc.defaultView.MutationObserver || root.MutationObserver;
      if (Observer) {
        const consume = records => {
          const current = locate();
          if (records.some(record => relevantMutation(record, state.previous, current))) state.revision++;
          state.previous = current;
        };
        state.consume = consume;
        state.observer = new Observer(consume);
        state.observer.observe(doc.body, { subtree: true, childList: true, characterData: true, attributes: true,
          attributeFilter: ['disabled', 'aria-disabled', 'aria-busy'] });
      }
      watchers.set(doc, state);
    } else if (state.observer) state.consume(state.observer.takeRecords());
    return state;
  }
  function coordinates(element, refresh, doc) {
    const rect = element.getBoundingClientRect(), view = doc.defaultView;
    const viewportWidth = view && view.innerWidth || doc.documentElement.clientWidth;
    const viewportHeight = view && view.innerHeight || doc.documentElement.clientHeight;
    const x = rect.left + rect.width / 2, y = rect.top + rect.height / 2;
    if (![x, y, rect.width, rect.height, viewportWidth, viewportHeight].every(Number.isFinite) || rect.width <= 0 || rect.height <= 0 || viewportWidth <= 0 || viewportHeight <= 0)
      return blocked('조회 버튼의 위치를 확인하지 못했습니다.');
    const inViewport = x >= 0 && y >= 0 && x < viewportWidth && y < viewportHeight;
    if (inViewport && !inside(element, doc.elementFromPoint(x, y))) return blocked('다른 화면 요소가 조회 버튼을 가리고 있습니다.');
    return { state: 'ready', message: inViewport ? '현재 열차 분류의 조회 버튼을 확인했습니다.' : '조회 버튼이 화면 밖에 있습니다. 스크롤 후 다시 읽으세요.',
      action: { kind: 'refresh', id: refresh.selector + '|' + refresh.label, selector: refresh.selector, label: refresh.label,
        x, y, width: rect.width, height: rect.height, viewportWidth, viewportHeight, inViewport } };
  }
  function observe(expected, suppliedDocument) {
    const doc = suppliedDocument || root.document, S = root.KorailSiteAdapter, C = root.KorailHelperCore;
    if (!doc || !doc.body || !S || !C) return { state: 'unknown', message: '조회 화면 분석기가 준비되지 않았습니다.' };
    if (doc.hidden) return blocked('프로그램 화면이 숨겨져 중지합니다.');
    let url; try { url = new URL(doc.location ? doc.location.href : doc.URL); } catch { return blocked('조회 화면 주소를 확인하지 못했습니다.'); }
    const fixture = url.hostname === 'korail-helper.invalid' && url.pathname === '/card-demo.html';
    if (url.protocol !== 'https:' || url.port || (!fixture && !['www.korail.com', 'korail.com'].includes(url.hostname)) || !S.matchesPage(doc))
      return blocked('확인한 코레일 열차 조회 화면이 아닙니다.');
    const pageText = text(doc.body), reason = C.blocker(pageText);
    if (reason) return blocked(reason, pageText);
    if (Array.from(doc.querySelectorAll('[role="dialog"],[role="alertdialog"],[aria-modal="true"],.modal.show,.modal.on,.modal[open],.popup.show,.popup.on,.modal_wrap,.modal-wrap,.popup_wrap,.pop_wrap,.popWrap')).some(shown))
      return blocked('안내창이 열려 재조회를 중지합니다. 내용을 직접 확인하세요.');
    if (Array.from(doc.querySelectorAll('iframe[src*="captcha"],iframe[src*="challenge"],input[autocomplete="one-time-code"]')).some(shown))
      return blocked('인증 화면을 직접 확인하세요.');
    const query = S.query(doc);
    if (!query.supported) return blocked(query.reason || '조회 조건을 확인하지 못했습니다.');
    const refresh = S.activeRefresh(doc);
    if (!refresh || !shown(refresh.element)) return { state: 'unknown', message: '현재 열차 분류의 조회 버튼을 하나로 식별하지 못했습니다.', query };
    const signature = queryFingerprint(query, refresh);
    if (!sameQuery(expected, query, signature)) return blocked('날짜·구간·인원 또는 열차 분류가 변경되어 중지합니다.');
    const observed = watch(doc, refresh), rows = S.cards(doc), rawRows = Array.from(doc.querySelectorAll(S.cardSelector)).filter(shown);
    const ids = rows.map(row => S.identity(row));
    if (ids.some(id => !id) || new Set(ids.map(id => id.key)).size !== ids.length) return blocked('조회 결과의 열차 식별이 중복되거나 불명확합니다.');
    const data = { query, queryFingerprint: signature, fingerprint: fingerprint(rows, S), count: rows.length, revision: observed.revision, observesMutations: !!observed.observer };
    const panel = one(doc, panelSelector);
    const loading = !!refresh.element.disabled || refresh.element.getAttribute('aria-disabled') === 'true' ||
      !!panel && (panel.matches(loadingSelector) && shown(panel) || Array.from(panel.querySelectorAll(loadingSelector)).some(shown));
    if (loading || !rows.length || rows.length !== rawRows.length) return { ...data, state: 'loading', message: '조회 결과가 비었거나 갱신 중입니다.', busy: true };
    if (refresh.element.closest('[inert],.disabled')) return { ...data, ...blocked('조회 버튼이 비활성 상태입니다.'), busy: false };
    if (Array.from(doc.querySelectorAll(S.cardSelector + ' > .tck_inner > .price_box.active,#container > .ticket_reserv_wrap')).some(shown))
      return { ...data, ...blocked('선택 좌석 또는 예매창이 열려 재조회를 중지합니다.'), busy: false };
    return { ...data, ...coordinates(refresh.element, refresh, doc), busy: false };
  }
  function read(expected, suppliedDocument) {
    try { return observe(expected, suppliedDocument); }
    catch { return { state: 'unknown', message: '화면 구조가 바뀌어 조회 상태를 확인하지 못했습니다.' }; }
  }
  return Object.freeze({ read, sameQuery, queryFingerprint, fingerprint, relevantMutation });
});
