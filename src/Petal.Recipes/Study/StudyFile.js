(function () {
  'use strict';
  var doc = document, root = doc.documentElement, body = doc.body;
  var data;
  try { data = JSON.parse(doc.getElementById('data').textContent); } catch (e) { return; }
  var W = data.w, rtl = root.getAttribute('dir') === 'rtl';
  body.classList.remove('nojs');
  body.classList.add('js');
  if ('scrollRestoration' in history) history.scrollRestoration = 'manual';

  // ───── what is kept in this browser ─────
  var memory = {};
  function load(key) {
    try { var v = window.localStorage.getItem(key); return v ? JSON.parse(v) : null; } catch (e) { return memory[key] || null; }
  }
  function save(key, value) {
    memory[key] = value;
    try { window.localStorage.setItem(key, JSON.stringify(value)); } catch (e) { /* a page that may keep nothing still works */ }
  }
  var KEY = 'dropwheel-study:' + body.getAttribute('data-id'), UI = 'dropwheel-study:ui';
  function fresh() { return { read: {}, q: {}, c: {}, best: {}, last: 0 }; }
  var state = load(KEY) || fresh();
  ['read', 'q', 'c', 'best'].forEach(function (k) { if (!state[k] || typeof state[k] !== 'object') state[k] = {}; });
  var ui = load(UI) || {};
  if (ui.answers === undefined) ui.answers = true;
  function keep() { save(KEY, state); }

  // ───── little helpers ─────
  function $(s, el) { return (el || doc).querySelector(s); }
  function $$(s, el) { return Array.prototype.slice.call((el || doc).querySelectorAll(s)); }
  function h(tag, props, kids) {
    var el = doc.createElement(tag);
    if (props) Object.keys(props).forEach(function (k) {
      var v = props[k];
      if (v === null || v === undefined || v === false) return;
      if (k === 'text') el.textContent = v;
      else if (k === 'class') el.className = v;
      else if (k.slice(0, 2) === 'on') el.addEventListener(k.slice(2), v);
      else el.setAttribute(k, v === true ? '' : v);
    });
    (kids || []).forEach(function (c) { if (c !== null && c !== undefined) el.appendChild(typeof c === 'string' ? doc.createTextNode(c) : c); });
    return el;
  }
  function fmt(s) {
    var args = arguments;
    return String(s).replace(/\{(\d+)\}/g, function (m, i) { return args[+i + 1] === undefined ? m : args[+i + 1]; });
  }
  // Text as compared: lower case, without accents or Arabic vowel marks, each Arabic letter in one form.
  function fold(s) {
    return String(s || '').normalize('NFD').replace(/[̀-ًͯ-ٰٟـ‌]/g, '').toLowerCase()
      .replace(/ٱ/g, 'ا').replace(/ة/g, 'ه').replace(/[ىی]/g, 'ي').replace(/ک/g, 'ك').replace(/ı/g, 'i');
  }
  function shuffle(list) {
    for (var i = list.length - 1; i > 0; i--) { var j = Math.floor(Math.random() * (i + 1)); var t = list[i]; list[i] = list[j]; list[j] = t; }
    return list;
  }
  var letters = String(W.letters || 'A B C D').split(' ').filter(Boolean);
  function letter(i) { return letters[i] || String.fromCharCode(65 + i); }
  var say = $('#say');
  function announce(text) { say.textContent = ''; setTimeout(function () { say.textContent = text; }, 40); }
  function flash(el) { el.classList.remove('flash'); void el.offsetWidth; el.classList.add('flash'); }
  var partsByN = {};
  data.parts.forEach(function (p) { partsByN[p.n] = p; });
  function chapterOf(n) { var p = partsByN[n]; return p && p.c ? data.chapters[p.c - 1] : null; }
  function inScope(n, scope) {
    if (!scope || scope === 'all') return true;
    if (scope.charAt(0) === 'c') { var ch = data.chapters[+scope.slice(1) - 1]; return !!ch && ch.p.indexOf(n) >= 0; }
    if (scope.charAt(0) === 'p') return n === +scope.slice(1);
    return true;
  }
  function scopeChoice(selected, onchange) {
    var select = h('select', { onchange: function () { onchange(select.value); } }, [h('option', { value: 'all', text: W.wholeFile })]);
    data.chapters.forEach(function (c) { select.appendChild(h('option', { value: 'c' + c.n, text: c.t })); });
    select.value = selected && $('option[value="' + selected + '"]', select) ? selected : 'all';
    return h('label', null, [W.from + ' ', select]);
  }

  // ───── text size and light or dark ─────
  function applyPrefs() {
    root.style.setProperty('--size', ui.size || 1);
    if (ui.theme) root.setAttribute('data-theme', ui.theme); else root.removeAttribute('data-theme');
    body.classList.toggle('noanswers', !ui.answers);
  }
  applyPrefs();
  $$('.size').forEach(function (b) {
    b.addEventListener('click', function () {
      ui.size = Math.round(Math.min(1.45, Math.max(0.85, (ui.size || 1) + 0.1 * +b.getAttribute('data-d'))) * 100) / 100;
      save(UI, ui); applyPrefs();
    });
  });
  var theme = $('.theme');
  if (theme) theme.addEventListener('click', function () {
    // The other look from the one shown, whatever the system's is.
    var dark = ui.theme ? ui.theme === 'dark' : !!(window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches);
    ui.theme = dark ? 'light' : 'dark';
    save(UI, ui); applyPrefs();
  });

  // ───── the contents on a narrow screen ─────
  var menu = $('.menu');
  function closeMenu() { body.classList.remove('menu-open'); if (menu) menu.setAttribute('aria-expanded', 'false'); }
  if (menu) menu.addEventListener('click', function (e) {
    e.stopPropagation();
    var open = body.classList.toggle('menu-open');
    menu.setAttribute('aria-expanded', String(open));
  });
  doc.addEventListener('click', function (e) {
    if (body.classList.contains('menu-open') && !e.target.closest('#toc') && !e.target.closest('.menu')) closeMenu();
  });

  // ───── views ─────
  var views = {};
  $$('.view').forEach(function (v) { views[v.id] = v; });
  var current = null;
  function route() {
    var hash = decodeURIComponent(location.hash.slice(1)) || 'home', name = hash, anchor = null, scope = null;
    if (/^test-/.test(hash)) { name = 'test'; scope = hash.slice(5); }
    else if (/^t\d+$/.test(hash)) { name = 'terms'; anchor = hash; }
    else if (/^p\d+-\d+$/.test(hash)) { name = hash.split('-')[0]; anchor = hash; }
    if (!views[name]) name = 'home';
    show(name, anchor, scope);
  }
  function show(name, anchor, scope) {
    if (current) current.classList.remove('on');
    current = views[name];
    current.classList.add('on');
    closeMenu(); hidePop(); closeHits();
    $$('#toc a').forEach(function (a) { a.removeAttribute('aria-current'); });
    var link = $('#toc a[href="#' + name + '"]');
    if (link) link.setAttribute('aria-current', 'page');
    if (name === 'cards') Trainer.render();
    if (name === 'test') Exam.render(scope);
    if (name === 'progress') Stand.render();
    if (name === 'home') resume();
    if (/^p\d+$/.test(name)) { state.last = +name.slice(1); keep(); }
    var target = anchor && doc.getElementById(anchor);
    if (target) { target.scrollIntoView({ block: 'start' }); flash(target); }
    else {
      window.scrollTo(0, 0);
      // A browser that has just opened the page at its address's part may scroll there again once it is laid out.
      if (window.requestAnimationFrame) window.requestAnimationFrame(function () { window.scrollTo(0, 0); });
    }
    var title = $('h1', current);
    if (title && !target) { try { title.focus({ preventScroll: true }); } catch (e) { title.focus(); } }
    // The part's line in the contents in sight, without moving the page.
    var toc = $('#toc');
    if (link && toc && toc.scrollHeight > toc.clientHeight && (link.offsetTop < toc.scrollTop || link.offsetTop > toc.scrollTop + toc.clientHeight - 48))
      toc.scrollTop = Math.max(0, link.offsetTop - toc.clientHeight / 3);
  }
  window.addEventListener('hashchange', route);
  function resume() {
    var begin = $('.begin a');
    var last = partsByN[state.last];
    if (!begin || !last) return;
    begin.setAttribute('href', '#p' + last.n);
    begin.firstChild.nodeValue = fmt(W.resume, last.t);
  }

  // ───── parts studied ─────
  function refreshMarks() {
    var total = data.parts.length, done = data.parts.filter(function (p) { return state.read[p.n]; }).length;
    $$('a[data-p]').forEach(function (a) { a.classList.toggle('done', !!state.read[a.getAttribute('data-p')]); });
    $$('.mark').forEach(function (b) {
      var on = !!state.read[b.getAttribute('data-p')];
      b.setAttribute('aria-pressed', String(on));
      $('span', b).textContent = on ? W.studied : W.markStudied;
    });
    $$('.part').forEach(function (v) { var s = $('.state', v); if (s) s.textContent = state.read[v.getAttribute('data-p')] ? '✓ ' + W.studied : ''; });
    $$('.tile[data-c]').forEach(function (t) {
      var ch = data.chapters[+t.getAttribute('data-c') - 1];
      var d = ch.p.filter(function (n) { return state.read[n]; }).length;
      $('.bar span', t).style.width = (100 * d / ch.p.length) + '%';
    });
    $$('.tile[data-p]').forEach(function (t) { $('.bar span', t).style.width = state.read[t.getAttribute('data-p')] ? '100%' : '0'; });
    var meter = $('.meter');
    if (meter) {
      var pct = total ? Math.round(100 * done / total) : 0;
      meter.style.setProperty('--done', pct);
      $('.num', meter).textContent = pct + '%';
      meter.setAttribute('title', W.partsStudied + ': ' + done + '/' + total);
    }
  }

  // ───── questions in the parts ─────
  function answer(q, choice, record) {
    var right = +q.getAttribute('data-a');
    $$('.opt', q).forEach(function (b, i) {
      b.disabled = true;
      b.classList.toggle('right', i === right);
      b.classList.toggle('wrong', i === choice && i !== right);
      b.setAttribute('aria-pressed', String(i === choice));
    });
    q.classList.add('answered');
    var verdict = $('.verdict', q);
    verdict.textContent = choice === right ? W.right : W.wrong;
    verdict.className = 'verdict ' + (choice === right ? 'good' : 'bad');
    $('.key', q).open = true;
    if (record) {
      state.q[q.getAttribute('data-q')] = { c: choice, r: choice === right ? 1 : 0 };
      keep();
      announce(verdict.textContent);
    }
  }
  function unanswer(q) {
    $$('.opt', q).forEach(function (b) { b.disabled = false; b.classList.remove('right', 'wrong'); b.removeAttribute('aria-pressed'); });
    q.classList.remove('answered');
    $('.key', q).open = false;
    delete state.q[q.getAttribute('data-q')];
    keep();
    var first = $('.opt', q);
    if (first) first.focus();
  }

  // ───── what a click does ─────
  doc.addEventListener('click', function (e) {
    var t = e.target;
    var opt = t.closest('.part .q .opt');
    if (opt && !opt.disabled) { answer(opt.closest('.q'), +opt.getAttribute('data-i'), true); return; }
    var redo = t.closest('.part .q .redo');
    if (redo) { e.preventDefault(); unanswer(redo.closest('.q')); return; }
    var card = t.closest('.part .card');
    if (card) { card.classList.toggle('flipped'); return; }
    var mark = t.closest('.mark');
    if (mark) {
      var n = mark.getAttribute('data-p');
      if (state.read[n]) delete state.read[n]; else state.read[n] = Date.now();
      keep(); refreshMarks();
      announce(state.read[n] ? W.studied : W.markStudied);
      return;
    }
    var term = t.closest('.term');
    if (term) { e.preventDefault(); if (popFor === term) hidePop(); else showPop(term); return; }
    if (!t.closest('#pop')) hidePop();
  });

  // ───── terms ─────
  var pop = $('#pop'), terms = {}, popFor = null, popTimer = 0;
  data.terms.forEach(function (t) { terms[t.id] = t; });
  $$('.term').forEach(function (el) { el.removeAttribute('title'); el.setAttribute('role', 'button'); el.setAttribute('aria-describedby', 'pop'); });
  function showPop(el) {
    var t = terms[el.getAttribute('data-t')];
    if (!t) return;
    clearTimeout(popTimer);
    pop.textContent = '';
    pop.appendChild(h('strong', { text: t.t }));
    pop.appendChild(h('span', { text: t.m }));
    pop.appendChild(h('a', { class: 'src', href: '#' + t.id, text: W.terms + ' · ' + t.w }));
    pop.hidden = false;
    var r = el.getBoundingClientRect(), pw = pop.offsetWidth, ph = pop.offsetHeight;
    var x = rtl ? r.right - pw : r.left;
    x = Math.max(8, Math.min(x, window.innerWidth - pw - 8));
    var y = r.bottom + 8;
    if (y + ph > window.innerHeight - 8 && r.top - ph - 8 > 0) y = r.top - ph - 8;
    pop.style.left = (x + window.scrollX) + 'px';
    pop.style.top = (y + window.scrollY) + 'px';
    popFor = el;
  }
  function hidePop() { if (pop) pop.hidden = true; popFor = null; }
  var hover = window.matchMedia && window.matchMedia('(hover: hover)').matches;
  if (hover) {
    doc.addEventListener('mouseover', function (e) {
      var term = e.target.closest && e.target.closest('.term');
      if (term && term !== popFor) { clearTimeout(popTimer); popTimer = setTimeout(function () { showPop(term); }, 150); }
      else if (!term && !e.target.closest('#pop') && popFor) { clearTimeout(popTimer); popTimer = setTimeout(hidePop, 250); }
    });
  }
  doc.addEventListener('focusin', function (e) { if (e.target.classList && e.target.classList.contains('term')) showPop(e.target); });
  doc.addEventListener('keydown', function (e) {
    if (e.key === 'Escape') { hidePop(); closeHits(); closeMenu(); }
    if ((e.key === 'Enter' || e.key === ' ') && e.target.classList && e.target.classList.contains('term')) { e.preventDefault(); if (popFor === e.target) hidePop(); else showPop(e.target); }
  });
  window.addEventListener('resize', hidePop);

  // ───── the glossary ─────
  var filter = $('.filter');
  if (filter) filter.addEventListener('input', function () {
    var f = fold(filter.value.trim());
    $$('.g').forEach(function (g) { g.hidden = !!f && fold(g.textContent).indexOf(f) < 0; });
  });

  // ───── finding anything ─────
  var q = $('#q'), hits = $('#hits'), index = null, chosen = -1;
  function buildIndex() {
    index = [];
    data.parts.forEach(function (p) {
      var v = views['p' + p.n];
      if (!v) return;
      // Block by block, so the words of one paragraph don't run into the next.
      var raw = $$('h1, h2, h3, h4, p, li, dt, dd, th, td, blockquote, .card .face', v).map(function (e) { return e.textContent; }).join(' ').replace(/\s+/g, ' ');
      index.push({ href: '#p' + p.n, title: p.t, raw: raw, text: fold(raw), titleText: fold(p.t) });
    });
    data.terms.forEach(function (t) { index.push({ href: '#' + t.id, title: t.t, raw: t.m, text: fold(t.t + ' ' + t.m), titleText: fold(t.t) }); });
  }
  // The sentence of a text a word is in, with the word marked: compared folded, shown as written.
  function snippet(raw, word) {
    var sentences = raw.replace(/([.!?؟۔])\s+/g, '$1\u0001').split('\u0001');
    for (var i = 0; i < sentences.length; i++) {
      var s = sentences[i], chars = Array.from(s), folded = '', map = [];
      chars.forEach(function (ch, k) { var f = fold(ch); for (var j = 0; j < f.length; j++) { folded += f[j]; map.push(k); } });
      var at = folded.indexOf(word);
      if (at < 0) continue;
      var from = map[at], to = map[at + word.length - 1] + 1;
      var start = Math.max(0, from - 60), end = Math.min(chars.length, to + 80);
      return h('span', null, [(start > 0 ? '…' : '') + chars.slice(start, from).join(''), h('mark', { text: chars.slice(from, to).join('') }), chars.slice(to, end).join('') + (end < chars.length ? '…' : '')]);
    }
    return h('span', { text: raw.slice(0, 120) });
  }
  function closeHits() { if (hits) { hits.hidden = true; chosen = -1; } }
  function findNow() {
    var words = fold(q.value.trim()).split(/\s+/).filter(function (w) { return w.length > 1; });
    hits.textContent = '';
    if (!words.length) { closeHits(); return; }
    if (!index) buildIndex();
    var found = index.filter(function (e) { return words.every(function (w) { return e.text.indexOf(w) >= 0; }); })
      .sort(function (a, b) { return (words.every(function (w) { return b.titleText.indexOf(w) >= 0; }) ? 1 : 0) - (words.every(function (w) { return a.titleText.indexOf(w) >= 0; }) ? 1 : 0); })
      .slice(0, 12);
    if (!found.length) hits.appendChild(h('p', { text: W.noResults }));
    found.forEach(function (e) {
      hits.appendChild(h('a', { href: e.href, role: 'option', 'data-word': words[0] }, [h('strong', { text: e.title }), snippet(e.raw, words[0])]));
    });
    hits.hidden = false;
    chosen = -1;
  }
  var findTimer = 0;
  if (q) {
    q.addEventListener('input', function () { clearTimeout(findTimer); findTimer = setTimeout(findNow, 140); });
    q.addEventListener('keydown', function (e) {
      var links = $$('a', hits);
      if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
        e.preventDefault();
        if (!links.length) return;
        chosen = (chosen + (e.key === 'ArrowDown' ? 1 : -1) + links.length) % links.length;
        links.forEach(function (l, i) { l.classList.toggle('on', i === chosen); });
      } else if (e.key === 'Enter' && links.length) {
        e.preventDefault();
        go(links[Math.max(0, chosen)]);
      }
    });
    q.addEventListener('focus', function () { if (q.value.trim()) findNow(); });
  }
  if (hits) hits.addEventListener('click', function (e) { var a = e.target.closest('a'); if (a) { e.preventDefault(); go(a); } });
  doc.addEventListener('click', function (e) { if (hits && !e.target.closest('.find')) closeHits(); });
  // To what was found, and the first line of it that says the word lit up.
  function go(a) {
    var word = a.getAttribute('data-word');
    closeHits();
    q.blur();
    if (location.hash === a.getAttribute('href')) route(); else location.hash = a.getAttribute('href');
    setTimeout(function () {
      var place = $$('p, li, dd, dt, .ask, h3, h4', current).filter(function (el) { return fold(el.textContent).indexOf(word) >= 0; })[0];
      if (place) { place.scrollIntoView({ block: 'center' }); flash(place); }
    }, 60);
  }
  doc.addEventListener('keydown', function (e) {
    var typing = e.target.closest && e.target.closest('input, textarea, select');
    if (e.key === '/' && !typing && q) { e.preventDefault(); q.focus(); }
  });

  // ───── cards one by one ─────
  var Trainer = {
    scope: 'all', queue: null, at: null,
    render: function () {
      var slot = $('#cards .slot');
      if (!slot) return;
      var self = this;
      if (!this.queue) this.fill(false);
      this.face = null;
      slot.textContent = '';
      var all = data.cards.filter(function (c) { return inScope(c.p, self.scope); });
      var known = all.filter(function (c) { return state.c[c.id]; }).length;
      var tools = h('div', { class: 'tools' }, [
        scopeChoice(this.scope, function (v) { self.scope = v; self.fill(false); self.render(); }),
        h('span', { class: 'grow' }),
        h('button', { type: 'button', class: 'btn quiet', text: W.shuffle, onclick: function () { self.fill(true); self.render(); } }),
        h('button', { type: 'button', class: 'btn quiet', text: W.startOver, onclick: function () { all.forEach(function (c) { delete state.c[c.id]; }); keep(); self.fill(false); self.render(); } }),
      ]);
      slot.appendChild(tools);
      slot.appendChild(h('div', { class: 'label', text: fmt(W.known, known, all.length) }));
      var bar = h('div', { class: 'meterbar' }, [h('span')]);
      bar.firstChild.style.width = (all.length ? 100 * known / all.length : 0) + '%';
      slot.appendChild(bar);
      var card = this.queue[0];
      if (!card) { slot.appendChild(h('p', { class: 'score', text: W.allKnown })); return; }
      var face = h('button', { type: 'button', class: 'card', 'aria-label': W.turn }, [
        h('span', { class: 'face front' }, [h('span', { class: 't', text: card.f })]),
        h('span', { class: 'face back' }, [h('span', { class: 't', text: card.b }), h('span', { class: 'src', text: card.w })]),
      ]);
      face.addEventListener('click', function () { face.classList.toggle('flipped'); });
      slot.appendChild(h('div', { class: 'stage' }, [face]));
      slot.appendChild(h('div', { class: 'grade' }, [
        h('button', { type: 'button', class: 'btn again', text: W.notYet + ' (1)', onclick: function () { self.grade(false); } }),
        h('button', { type: 'button', class: 'btn knew', text: W.knewIt + ' (2)', onclick: function () { self.grade(true); } }),
      ]));
      slot.appendChild(h('p', { class: 'hint', text: W.cardsHint }));
      this.face = face;
    },
    fill: function (mixed) {
      var self = this;
      var left = data.cards.filter(function (c) { return inScope(c.p, self.scope) && !state.c[c.id]; });
      this.queue = mixed ? shuffle(left) : left;
    },
    grade: function (knew) {
      var card = this.queue.shift();
      if (!card) return;
      if (knew) state.c[card.id] = 1;
      else { delete state.c[card.id]; this.queue.splice(Math.min(3, this.queue.length), 0, card); }
      keep();
      this.render();
      if (this.face) this.face.focus();
    },
  };
  doc.addEventListener('keydown', function (e) {
    if (!current || current.id !== 'cards' || !Trainer.face || (e.target.closest && e.target.closest('input, select, textarea'))) return;
    if (e.key === ' ' || e.key === 'Enter') { if (e.target === Trainer.face) return; e.preventDefault(); Trainer.face.classList.toggle('flipped'); }
    else if (e.key === '1') Trainer.grade(false);
    else if (e.key === '2') Trainer.grade(true);
  });

  // ───── testing yourself ─────
  var Exam = {
    scope: 'all', count: 10, list: null, at: 0, right: 0, wrong: [],
    render: function (scope) {
      if (scope) { this.scope = scope; this.list = null; }
      if (this.list) return this.question();
      this.setup();
    },
    pool: function () { var self = this; return data.quiz.filter(function (x) { return inScope(x.p, self.scope); }); },
    setup: function () {
      var self = this, slot = $('#test .slot');
      slot.textContent = '';
      var count = h('select', { onchange: function () { self.count = +count.value; } }, [10, 20, 0].map(function (n) { return h('option', { value: n, text: n ? String(n) : W.all }); }));
      count.value = String(this.count);
      slot.appendChild(h('div', { class: 'tools' }, [
        scopeChoice(this.scope, function (v) { self.scope = v; self.setup(); }),
        h('label', null, [W.howMany + ' ', count]),
      ]));
      var pool = this.pool();
      if (!pool.length) { slot.appendChild(h('p', { class: 'empty', text: W.noQuestions })); return; }
      if (state.best[this.scope] !== undefined) slot.appendChild(h('p', { class: 'label', text: fmt(W.best, state.best[this.scope]) }));
      slot.appendChild(h('p', null, [h('button', { type: 'button', class: 'btn primary', text: W.start, onclick: function () { self.begin(); } })]));
    },
    begin: function () {
      var pool = shuffle(this.pool().slice());
      this.list = this.count ? pool.slice(0, this.count) : pool;
      this.at = 0; this.right = 0; this.wrong = [];
      this.question();
    },
    question: function () {
      var self = this, slot = $('#test .slot'), x = this.list[this.at];
      slot.textContent = '';
      if (!x) return this.result();
      slot.appendChild(h('p', { class: 'label', text: fmt(W.questionOf, this.at + 1, this.list.length) }));
      var bar = h('div', { class: 'meterbar' }, [h('span')]);
      bar.firstChild.style.width = (100 * this.at / this.list.length) + '%';
      slot.appendChild(bar);
      var box = h('div', { class: 'q' }, [h('p', { class: 'ask', text: x.q })]);
      var opts = h('ol', { class: 'opts' });
      var after = h('div', { class: 'after', hidden: true });
      x.o.forEach(function (o, i) {
        var b = h('button', { type: 'button', class: 'opt' }, [h('span', { class: 'l', text: letter(i) }), h('span', { class: 't', text: o })]);
        b.addEventListener('click', function () { self.choose(x, i, opts, after); });
        opts.appendChild(h('li', null, [b]));
      });
      box.appendChild(opts);
      box.appendChild(after);
      slot.appendChild(box);
      var first = $('.opt', box);
      if (first) first.focus();
    },
    choose: function (x, i, opts, after) {
      var self = this, good = i === x.a;
      $$('.opt', opts).forEach(function (b, k) { b.disabled = true; b.classList.toggle('right', k === x.a); b.classList.toggle('wrong', k === i && !good); });
      if (good) this.right++; else this.wrong.push(x);
      state.q[x.id] = { c: i, r: good ? 1 : 0 };
      keep();
      after.hidden = false;
      after.appendChild(h('p', { class: 'verdict ' + (good ? 'good' : 'bad'), text: good ? W.right : W.wrong }));
      after.appendChild(h('p', null, [h('strong', { text: letter(x.a) + ') ' + x.o[x.a] }), x.y ? ' — ' + x.y : '']));
      after.appendChild(h('p', null, [h('a', { class: 'src', href: '#p' + x.p, text: x.w + ' · ' + W.readPart })]));
      if (x.x) after.appendChild(h('blockquote', { class: 'from', dir: 'auto' }, [h('span', { class: 'label', text: W.fromOriginal }), ' ', h('q', { text: x.x })]));
      var last = this.at + 1 >= this.list.length;
      var next = h('button', { type: 'button', class: 'btn primary', text: last ? W.finish : W.next, onclick: function () { self.at++; self.question(); } });
      after.appendChild(h('p', null, [next]));
      announce(good ? W.right : W.wrong);
      next.focus();
    },
    result: function () {
      var self = this, slot = $('#test .slot'), pct = Math.round(100 * this.right / this.list.length);
      if (state.best[this.scope] === undefined || pct > state.best[this.scope]) { state.best[this.scope] = pct; keep(); }
      slot.appendChild(h('div', { class: 'stat score' }, [h('div', { class: 'big', text: pct + '%' }), h('div', { text: fmt(W.score, this.right, this.list.length) })]));
      if (this.wrong.length) {
        slot.appendChild(h('h2', { text: W.mistakes }));
        this.wrong.forEach(function (x) {
          slot.appendChild(h('div', { class: 'q' }, [
            h('p', { class: 'ask', text: x.q }),
            h('p', null, [h('strong', { text: letter(x.a) + ') ' + x.o[x.a] }), x.y ? ' — ' + x.y : '']),
            h('a', { class: 'src', href: '#p' + x.p, text: x.w + ' · ' + W.readPart }),
          ]));
        });
      }
      slot.appendChild(h('p', { class: 'row' }, [
        h('button', { type: 'button', class: 'btn primary', text: W.tryAgain, onclick: function () { self.begin(); } }),
        h('button', { type: 'button', class: 'btn quiet', text: W.startOver, onclick: function () { self.list = null; self.setup(); } }),
      ]));
      announce(fmt(W.score, this.right, this.list.length));
      this.list = null;
    },
  };

  // ───── how far you are ─────
  var Stand = {
    render: function () {
      var slot = $('#progress .slot');
      slot.textContent = '';
      var parts = data.parts.length, read = data.parts.filter(function (p) { return state.read[p.n]; }).length;
      var asked = data.quiz.length, right = data.quiz.filter(function (x) { return state.q[x.id] && state.q[x.id].r; }).length;
      var cards = data.cards.length, known = data.cards.filter(function (c) { return state.c[c.id]; }).length;
      function stat(label, a, b) {
        var bar = h('div', { class: 'meterbar' }, [h('span')]);
        bar.firstChild.style.width = (b ? 100 * a / b : 0) + '%';
        return h('div', { class: 'stat' }, [h('div', { class: 'label', text: label }), h('div', { class: 'val', text: a + '/' + b }), bar]);
      }
      var grid = h('div', { class: 'statgrid' }, [stat(W.partsStudied, read, parts)]);
      if (asked) grid.appendChild(stat(W.questionsRight, right, asked));
      if (cards) grid.appendChild(stat(W.cardsKnown, known, cards));
      slot.appendChild(grid);
      if (data.chapters.length) {
        var table = h('table', { class: 'chaps' }, [h('tr', null, [h('th', { text: W.chapter }), h('th', { text: W.partsStudied }), asked ? h('th', { text: W.questionsRight }) : null])]);
        data.chapters.forEach(function (c) {
          var d = c.p.filter(function (n) { return state.read[n]; }).length;
          var qs = data.quiz.filter(function (x) { return c.p.indexOf(x.p) >= 0; });
          var r = qs.filter(function (x) { return state.q[x.id] && state.q[x.id].r; }).length;
          table.appendChild(h('tr', null, [h('td', null, [h('a', { href: '#c' + c.n, text: c.t })]), h('td', { text: d + '/' + c.p.length }), asked ? h('td', { text: qs.length ? r + '/' + qs.length : '–' }) : null]));
        });
        slot.appendChild(table);
      }
      var answers = h('input', { type: 'checkbox' });
      answers.checked = !!ui.answers;
      answers.addEventListener('change', function () { ui.answers = answers.checked; save(UI, ui); applyPrefs(); });
      slot.appendChild(h('p', { class: 'row' }, [
        h('button', { type: 'button', class: 'btn', text: W.print, onclick: function () { window.print(); } }),
        h('label', null, [answers, ' ' + W.withAnswers]),
      ]));
      slot.appendChild(h('p', { class: 'note', text: W.kept }));
      slot.appendChild(h('p', null, [h('button', { type: 'button', class: 'link', text: W.reset, onclick: function () {
        if (!window.confirm(W.resetSure)) return;
        state = fresh(); keep(); Trainer.queue = null; restore(); refreshMarks(); Stand.render();
      } })]));
    },
  };

  // ───── printing: the answers open when they are wanted ─────
  var opened = [];
  window.addEventListener('beforeprint', function () {
    opened = $$('details.key').filter(function (d) { return !d.open; });
    if (ui.answers) opened.forEach(function (d) { d.open = true; });
  });
  window.addEventListener('afterprint', function () { opened.forEach(function (d) { d.open = false; }); opened = []; });

  // ───── as it was left ─────
  function restore() {
    $$('.part .q').forEach(function (x) {
      var s = state.q[x.getAttribute('data-q')];
      if (s && typeof s.c === 'number') answer(x, s.c, false);
      else if (x.classList.contains('answered')) { $$('.opt', x).forEach(function (b) { b.disabled = false; b.classList.remove('right', 'wrong'); }); x.classList.remove('answered'); $('.key', x).open = false; }
    });
  }
  restore();
  refreshMarks();
  route();
})();
