(function (root, factory) {
  'use strict';
  const api = factory(root);
  if (typeof module === 'object' && module.exports) module.exports = api;
  if (root) root.KorailMouseMore = api;
})(typeof globalThis !== 'undefined' ? globalThis : this, function (root) {
  'use strict';
  // Read-only observations for native mouse input. Never clicks, scrolls,
  // changes fields, calls site handlers, or makes network requests.
  const panelSelector = '#tab_tab_info1';
  const listSelector = panelSelector + ' > .tckWrap > ul';
  // Public Korail bundle 1.0.221 (2026-10-23): pagination is a direct
  // panel child; the next-day control is a separate button in this list.
  const moreSelector = panelSelector + ' > ul.btnList > li > div > a.page_group[href="#none"]';
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
  function blocked(message, data) {
    return { ...data, state: 'blocked', message, terminal: true, retryAllowed: false, blockCode: '', more: false };
  }
  function one(doc, selector) { const nodes = doc.querySelectorAll(selector); return nodes.length === 1 ? nodes[0] : null; }
  function validKeys(keys) {
    return Array.isArray(keys) && keys.length > 0 && keys.every(key => typeof key === 'string' && key.length > 0) && new Set(keys).size === keys.length;
  }
  function progress(previousKeys, currentKeys) {
    if (!validKeys(previousKeys) || !validKeys(currentKeys) || currentKeys.length < previousKeys.length ||
        previousKeys.some((key, index) => key !== currentKeys[index])) return 'invalid';
    return currentKeys.length === previousKeys.length ? 'unchanged' : 'appended';
  }
  function targetKey(target) {
    if (!target || typeof target !== 'object' || Array.isArray(target)) return null;
    const names = ['train', 'departureStation', 'arrivalStation', 'departure', 'arrival'];
    if (names.some(name => typeof target[name] !== 'string')) return null;
    const train = normal(target.train), from = station(target.departureStation), to = station(target.arrivalStation);
    if (!/^(?:KTX(?:-(?:산천|이음|청룡))?|ITX-(?:새마을|마음|청춘)|새마을(?:호)?|무궁화(?:호)?|누리로|남도해양열차) (?:0|[1-9]\d{0,5})$/.test(train) ||
        !from || !to || from === to || [from, to].some(name => name.length > 40 || /[<>|\d:]/.test(name)) ||
        !/^(?:[01]\d|2[0-3]):[0-5]\d$/.test(target.departure) || !/^(?:[01]\d|2[0-3]):[0-5]\d$/.test(target.arrival)) return null;
    return [train, from, to, target.departure, target.arrival].join('|');
  }
  function coordinates(element, selector, doc, data) {
    const rect = element.getBoundingClientRect(), view = doc.defaultView;
    const viewportWidth = view && view.innerWidth || doc.documentElement.clientWidth;
    const viewportHeight = view && view.innerHeight || doc.documentElement.clientHeight;
    const x = rect.left + rect.width / 2, y = rect.top + rect.height / 2;
    if (![x, y, rect.width, rect.height, viewportWidth, viewportHeight].every(Number.isFinite) || rect.width <= 0 || rect.height <= 0 || viewportWidth <= 0 || viewportHeight <= 0)
      return blocked('더보기 버튼의 위치를 확인하지 못했습니다.', data);
    const inViewport = x >= 0 && y >= 0 && x < viewportWidth && y < viewportHeight;
    if (inViewport && !inside(element, doc.elementFromPoint(x, y))) return blocked('다른 화면 요소가 더보기 버튼을 가리고 있습니다.', data);
    return { ...data, state: 'ready', more: true,
      message: inViewport ? '현재 조회 결과의 더보기 버튼을 확인했습니다.' : '더보기 버튼이 화면 밖에 있습니다. 스크롤 후 다시 읽으세요.',
      action: { kind: 'more', id: selector + '|더보기', selector, label: '더보기',
        x, y, width: rect.width, height: rect.height, viewportWidth, viewportHeight, inViewport } };
  }
  function observe(expectedQueryFingerprint, targets, suppliedDocument) {
    const doc = suppliedDocument || root.document, S = root.KorailSiteAdapter, R = root.KorailMouseRefresh;
    const initial = { queryFingerprint: '', keys: [], count: 0, allTargetsPresent: false, more: false };
    if (!doc || !doc.body || !S || !R || typeof R.inspect !== 'function')
      return { ...initial, state: 'unknown', message: '더보기 화면 분석기가 준비되지 않았습니다.' };
    if (expectedQueryFingerprint != null && (typeof expectedQueryFingerprint !== 'string' || !expectedQueryFingerprint))
      return blocked('저장한 조회 조건이 올바르지 않습니다.', initial);
    if (!Array.isArray(targets) || targets.length > 20) return blocked('선택 열차 목록을 확인하지 못했습니다.', initial);
    const targetKeys = targets.map(targetKey);
    if (targetKeys.some(key => !key) || new Set(targetKeys).size !== targetKeys.length)
      return blocked('선택 열차 식별이 중복되거나 불명확합니다.', initial);
    const snapshot = R.inspect(expectedQueryFingerprint == null ? null : { queryFingerprint: expectedQueryFingerprint }, doc);
    const data = { ...initial, queryFingerprint: snapshot.queryFingerprint || '', count: snapshot.count || 0 };
    if (!['ready', 'loading'].includes(snapshot.state)) return { ...data, ...snapshot, more: false };
    const panel = one(doc, panelSelector), list = one(doc, listSelector);
    if (!panel || !list) return blocked('더보기와 연결된 열차 목록을 하나로 확인하지 못했습니다.', data);
    const rows = Array.from(doc.querySelectorAll(S.cardSelector)).filter(shown), ids = rows.map(row => S.identity(row));
    if (ids.some(id => !id) || new Set(ids.map(id => id && id.key)).size !== ids.length)
      return blocked('조회 결과의 열차 식별이 중복되거나 불명확합니다.', data);
    if (ids.some(id => station(id.departureStation) !== station(snapshot.query.departureStation) || station(id.arrivalStation) !== station(snapshot.query.arrivalStation)))
      return blocked('조회 결과에 다른 구간의 열차가 포함되어 있습니다.', data);
    const observedKeys = ids.map(id => targetKey(id));
    if (observedKeys.some(key => !key) || new Set(observedKeys).size !== observedKeys.length)
      return blocked('조회 결과의 열차 식별이 중복되거나 불명확합니다.', data);
    data.keys = ids.map(id => id.key); data.count = ids.length;
    data.allTargetsPresent = targetKeys.length > 0 && targetKeys.every(key => observedKeys.includes(key));
    if (targets.some(target => station(target.departureStation) !== station(snapshot.query.departureStation) || station(target.arrivalStation) !== station(snapshot.query.arrivalStation)))
      return blocked('선택 열차와 현재 조회 구간이 다릅니다.', data);
    if (snapshot.state === 'loading') return { ...data, state: 'loading', message: snapshot.message };
    const candidates = Array.from(panel.querySelectorAll('button,a,[role="button"]')).filter(node => !list.contains(node) && shown(node) && text(node) === '더보기');
    if (candidates.length > 1) return blocked('더보기 버튼이 여러 개여서 중지합니다.', data);
    if (!candidates.length) {
      if (Array.from(doc.querySelectorAll(moreSelector)).some(shown))
        return { ...data, state: 'unknown', message: '더보기 버튼의 표시 문구를 확인하지 못했습니다.' };
      const unexpected = Array.from(doc.querySelectorAll('button,a,[role="button"]')).some(node => shown(node) && text(node) === '더보기');
      if (unexpected) return { ...data, state: 'unknown', message: '더보기 버튼과 현재 열차 목록의 연결을 확인하지 못했습니다.' };
      return { ...data, state: 'ready', message: data.allTargetsPresent ? '선택한 열차를 모두 읽었습니다.' : '현재 조회 결과에 더보기 버튼이 없습니다.' };
    }
    const element = candidates[0];
    if (one(doc, moreSelector) !== element)
      return { ...data, state: 'unknown', message: '확인한 조회 결과 더보기 버튼 구조가 아닙니다.' };
    if (!(list.compareDocumentPosition(element) & 4)) return blocked('더보기 버튼이 열차 목록 뒤에 있지 않습니다.', data);
    if (data.allTargetsPresent) return { ...data, more: true, state: 'ready', message: '선택한 열차를 모두 읽었습니다.' };
    if (element.disabled || element.getAttribute('aria-disabled') === 'true')
      return { ...data, more: true, state: 'loading', message: '더보기 버튼의 갱신이 끝나기를 기다립니다.' };
    if (element.closest('[inert],.disabled')) return blocked('더보기 버튼이 비활성 상태입니다.', data);
    return coordinates(element, moreSelector, doc, data);
  }
  function read(expectedQueryFingerprint, targets, suppliedDocument) {
    try { return observe(expectedQueryFingerprint, targets, suppliedDocument); }
    catch { return { state: 'unknown', queryFingerprint: '', keys: [], count: 0, allTargetsPresent: false, more: false,
      message: '화면 구조가 바뀌어 더보기 상태를 확인하지 못했습니다.' }; }
  }
  return Object.freeze({ read, progress });
});
