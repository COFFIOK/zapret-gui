// Zapret · сервер кодов «Поделиться сайтами» и анонимной статистики · by COFFIOK
// Cloudflare Worker + KV (переменная SHARE) + D1 (переменная STATS) + секрет STATS_KEY.
//   POST /s        тело: {"groups":{"Имя группы":["site.com", ...]}}  ->  {"code":"K7M4QX"}
//   GET  /s/K7M4QX                                                    ->  {"groups":{...}}
//   POST /t/ping   один раз от установки, если человек согласился (случайный номер, версии, настройки)
//   POST /t/ev     событие (включил zapret, тест, поделился кодом…)
//   POST /t/err    ошибка приложения (без путей и имени пользователя)
//   GET  /stats  (форма ключа) -> POST /stats                          ->  страница статистики
//   POST /chat/send, /chat/get                                        ->  чат пользователя с автором (по случайному номеру установки)
//   GET  /chat/threads.json, /chat/thread.json?id=                    ->  для автора: список чатов / переписка
//   POST /chat/reply, /chat/set, /chat/ban                            ->  ответить / закрыть, удалить чат / заблокировать
//   POST /chat/img?id=, GET /chat/img/КЛЮЧ?id=                        ->  картинки в чате (хранятся 120 дней в KV)
//   GET  /stats.json                                                  ->  то же в JSON (для приложения Zapret Stats)
//   Все запросы автора (кроме формы /stats) — с заголовком X-Zapret-Key: СЕКРЕТ
// IP-адреса нигде не сохраняются.

const ALPHABET = 'ABCDEFGHJKMNPQRSTUVWXYZ23456789'; // без 0/O, 1/I/L — чтобы не путать
const TTL = 60 * 60 * 24 * 30;
const MAX_BYTES = 32 * 1024;
const DOMAIN = /^([a-z0-9а-яё-]+\.)+[a-z0-9а-яё-]{2,}$/;

const json = (obj, status = 200) =>
  new Response(JSON.stringify(obj), { status, headers: { 'content-type': 'application/json; charset=utf-8', 'x-content-type-options': 'nosniff', 'cache-control': 'no-store' } });

function newCode() {
  const b = crypto.getRandomValues(new Uint8Array(6));
  return Array.from(b, x => ALPHABET[x % ALPHABET.length]).join('');
}

function clean(body) {
  const src = body && body.groups;
  if (!src || typeof src !== 'object') return null;
  const out = {};
  let total = 0;
  for (const [name, list] of Object.entries(src).slice(0, 50)) {
    if (typeof name !== 'string' || !Array.isArray(list)) continue;
    const n = name.replace(/[\u0000-\u001f\u007f-\u009f\u2028\u2029#]+/g, ' ').replace(/\s+/g, ' ').trim().slice(0, 40);
    const sites = [...new Set(list.filter(s => typeof s === 'string').map(s => s.trim().toLowerCase()).filter(s => s.length <= 253 && DOMAIN.test(s)))];
    if (!n || !sites.length) continue;
    out[n] = sites.slice(0, 500);
    total += out[n].length;
  }
  return total ? out : null;
}

// не больше 10 новых кодов в минуту с одного адреса — чтобы никто не забил хранилище мусором
async function tooMany(req, kind = 's', limit = 10) {
  const ip = req.headers.get('cf-connecting-ip') || 'x';
  const key = new Request('https://rl.local/' + kind + '/' + encodeURIComponent(ip) + '/' + Math.floor(Date.now() / 60000));
  const cache = caches.default;
  const hit = await cache.match(key);
  const n = hit ? parseInt(await hit.text(), 10) || 0 : 0;
  if (n >= limit) return true;
  await cache.put(key, new Response(String(n + 1), { headers: { 'cache-control': 'max-age=70' } }));
  return false;
}


// ===================== анонимная статистика (D1) =====================
const TID = /^[0-9a-f]{32}$/;
const EVENTS = new Set(['zapret_on', 'zapret_fail', 'test_run', 'share_code', 'share_import', 'app_update', 'zapret_update', 'tg_on', 'uninstall', 'telemetry_off']);
const s = (v, n) => (typeof v === 'string' ? v.replace(/[\u0000-\u001f\u007f]+/g, ' ').trim().slice(0, n) : '');
const num = v => (Number.isFinite(v) ? Math.max(0, Math.min(1e6, Math.floor(v))) : 0);
const b = v => (v === true ? 1 : 0);
const today = (shift = 0) => new Date(Date.now() - shift * 86400000).toISOString().slice(0, 10);

let schemaReady = false;
async function schema(db) {
  if (schemaReady) return;
  await db.batch([
    db.prepare(`CREATE TABLE IF NOT EXISTS installs (id TEXT PRIMARY KEY, first TEXT, last TEXT, v TEXT, z TEXT, os TEXT, lang TEXT, theme TEXT,
      eco INT, strat TEXT, tg INT, autoupd INT, game TEXT, sites INT, groups INT, home INT, pings INT DEFAULT 0)`),
    db.prepare(`CREATE TABLE IF NOT EXISTS days (day TEXT, id TEXT, v TEXT, PRIMARY KEY (day, id))`),
    db.prepare(`CREATE TABLE IF NOT EXISTS events (day TEXT, name TEXT, detail TEXT, n INT DEFAULT 0, PRIMARY KEY (day, name, detail))`),
    db.prepare(`CREATE TABLE IF NOT EXISTS errors (hash TEXT PRIMARY KEY, first TEXT, last TEXT, v TEXT, w TEXT, m TEXT, s TEXT, n INT DEFAULT 0)`),
    db.prepare(`CREATE TABLE IF NOT EXISTS err_users (hash TEXT, id TEXT, PRIMARY KEY (hash, id))`),
    db.prepare(`CREATE TABLE IF NOT EXISTS chat_threads (id TEXT PRIMARY KEY, first TEXT, last TEXT, contact TEXT, v TEXT, z TEXT, os TEXT, lang TEXT,
      strat TEXT, onz INT, status TEXT DEFAULT 'open', dev_unread INT DEFAULT 0, user_unread INT DEFAULT 0, last_text TEXT, last_dev INT DEFAULT 0)`),
    db.prepare(`CREATE TABLE IF NOT EXISTS chat_msgs (n INTEGER PRIMARY KEY AUTOINCREMENT, id TEXT, at TEXT, dev INT, kind TEXT, text TEXT, log TEXT)`),
    db.prepare(`CREATE INDEX IF NOT EXISTS chat_msgs_id ON chat_msgs (id, n)`),
  ]);
  // новые колонки (ник, блокировка, картинки) — добавляем, если таблицы созданы старой версией
  for (const sql of [`ALTER TABLE chat_threads ADD COLUMN nick TEXT DEFAULT ''`, `ALTER TABLE chat_threads ADD COLUMN ban_until TEXT DEFAULT ''`,
                     `ALTER TABLE chat_threads ADD COLUMN ban_reason TEXT DEFAULT ''`, `ALTER TABLE chat_msgs ADD COLUMN img TEXT DEFAULT ''`]) {
    try { await db.prepare(sql).run(); } catch { /* колонка уже есть */ }
  }
  schemaReady = true;
}

async function hash(text) {
  const d = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(text));
  return Array.from(new Uint8Array(d).slice(0, 8), x => x.toString(16).padStart(2, '0')).join('');
}

async function telemetry(req, env, path) {
  if (!env.STATS) return new Response(null, { status: 204 });   // база ещё не подключена — молча принимаем
  if (await tooMany(req, 't', 30)) return json({ error: 'slow down' }, 429);
  const text = await req.text();
  if (text.length > 8192) return json({ error: 'too big' }, 413);
  let d; try { d = JSON.parse(text); } catch { return json({ error: 'bad json' }, 400); }
  const id = s(d.id, 32), v = s(d.v, 16);
  if (!TID.test(id)) return json({ error: 'bad id' }, 400);
  const db = env.STATS, day = today();
  await schema(db);

  if (path === '/t/ping') {
    await db.batch([
      db.prepare(`INSERT INTO installs (id, first, last, v, z, os, lang, theme, eco, strat, tg, autoupd, game, sites, groups, home, pings)
        VALUES (?1, ?2, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13, ?14, ?15, 1)
        ON CONFLICT(id) DO UPDATE SET last = ?2, v = ?3, z = ?4, os = ?5, lang = ?6, theme = ?7, eco = ?8, strat = ?9, tg = ?10,
          autoupd = ?11, game = ?12, sites = ?13, groups = ?14, home = ?15, pings = pings + 1`)
        .bind(id, day, v, s(d.z, 16), s(d.os, 20), s(d.lang, 4), s(d.theme, 8), b(d.eco), s(d.strat, 40), b(d.tg), b(d.autoupd),
              s(d.game, 10), num(d.sites), num(d.groups), b(d.home)),
      db.prepare(`INSERT OR IGNORE INTO days (day, id, v) VALUES (?, ?, ?)`).bind(day, id, v),
    ]);
    if (Math.random() < 0.01) await db.prepare(`DELETE FROM days WHERE day < ?`).bind(today(180)).run();
    return json({ ok: true });
  }
  if (path === '/t/ev') {
    const name = s(d.e, 32);
    if (!EVENTS.has(name)) return json({ error: 'unknown event' }, 400);
    await db.prepare(`INSERT INTO events (day, name, detail, n) VALUES (?, ?, ?, 1) ON CONFLICT(day, name, detail) DO UPDATE SET n = n + 1`)
      .bind(day, name, s(d.d, 60)).run();
    return json({ ok: true });
  }
  if (path === '/t/err') {
    const w = s(d.w, 40), m = s(d.m, 300), st = s(d.s, 1500);
    if (!m) return json({ error: 'empty' }, 400);
    const h = await hash(w + '|' + m.replace(/\d+/g, '#') + '|' + st.split('\n')[0]);
    await db.batch([
      db.prepare(`INSERT INTO errors (hash, first, last, v, w, m, s, n) VALUES (?1, ?2, ?2, ?3, ?4, ?5, ?6, 1)
        ON CONFLICT(hash) DO UPDATE SET last = ?2, v = ?3, m = ?5, s = ?6, n = n + 1`).bind(h, day, v, w, m, st),
      db.prepare(`INSERT OR IGNORE INTO err_users (hash, id) VALUES (?, ?)`).bind(h, id),
    ]);
    return json({ ok: true });
  }
  return json({ error: 'not found' }, 404);
}

// ---------- чат пользователя с автором ----------
const KINDS = new Set(['bug', 'idea', 'other']);
const IMG_TTL = 60 * 60 * 24 * 120;          // картинки хранятся 120 дней
const IMG_MAX = 1536 * 1024;
const todayISO = () => new Date().toISOString().slice(0, 10);
const cleanText = (v, n) => (typeof v === 'string' ? v.replace(/\r/g, '').replace(/[\u0000-\u0008\u000b-\u001f\u007f]/g, '').trim().slice(0, n) : '');
const cleanNick = v => cleanText(v, 24).replace(/\s+/g, ' ');
const msgOut = m => ({ n: m.n, at: m.at, dev: m.dev === 1, kind: m.kind || '', text: m.text || '', img: m.img || '' });
const IMGKEY = /^[a-z0-9]{20}$/;

async function readBody(req) {
  const raw = await req.text();
  if (raw.length > 16384) return null;
  try { return JSON.parse(raw); } catch { return null; }
}

// блокировка: '' — нет, 'forever' — навсегда, иначе ISO-время окончания
function banState(t) {
  const u = t?.ban_until || '';
  if (!u) return null;
  if (u === 'forever') return { until: 'forever', reason: t.ban_reason || '' };
  return u > new Date().toISOString() ? { until: u, reason: t.ban_reason || '' } : null;
}

function imgKey() {
  const b = crypto.getRandomValues(new Uint8Array(20));
  return Array.from(b, x => 'abcdefghijklmnopqrstuvwxyz0123456789'[x % 36]).join('');
}

// загрузка картинки (пользователь — по своему номеру, автор — по ключу); тело — сами байты jpeg/png
async function chatImgUpload(req, env, url, dev) {
  if (!env.STATS || !env.SHARE) return json({ error: 'unavailable' }, 503);
  const id = url.searchParams.get('id') || '';
  if (!TID.test(id)) return json({ error: 'bad id' }, 400);
  const type = (req.headers.get('content-type') || '').split(';')[0].trim();
  if (type !== 'image/jpeg' && type !== 'image/png') return json({ error: 'only jpeg or png' }, 415);
  const db = env.STATS; await schema(db);
  if (!dev) {
    if (await tooMany(req, 'ci', 6)) return json({ error: 'slow down' }, 429);
    const t = await db.prepare(`SELECT ban_until, ban_reason FROM chat_threads WHERE id = ?`).bind(id).first();
    const ban = banState(t);
    if (ban) return json({ error: 'banned', ...ban }, 403);
    const today = await db.prepare(`SELECT COUNT(*) n FROM chat_msgs WHERE id = ? AND dev = 0 AND img <> '' AND at >= ?`).bind(id, todayISO()).first();
    if ((today?.n ?? 0) >= 15) return json({ error: 'slow down' }, 429);
  }
  const buf = await req.arrayBuffer();
  if (buf.byteLength < 100 || buf.byteLength > IMG_MAX) return json({ error: 'bad size' }, 413);
  const head = new Uint8Array(buf, 0, 4);
  const isJpg = head[0] === 0xFF && head[1] === 0xD8, isPng = head[0] === 0x89 && head[1] === 0x50 && head[2] === 0x4E && head[3] === 0x47;
  if (!isJpg && !isPng) return json({ error: 'not an image' }, 415);
  const key = imgKey();
  await env.SHARE.put('img:' + key, buf, { expirationTtl: IMG_TTL, metadata: { id, type: isJpg ? 'image/jpeg' : 'image/png' } });
  return json({ img: key });
}

async function chatImgGet(env, url, key, dev) {
  if (!IMGKEY.test(key) || !env.SHARE) return json({ error: 'not found' }, 404);
  const r = await env.SHARE.getWithMetadata('img:' + key, { type: 'arrayBuffer' });
  if (!r || !r.value) return json({ error: 'not found' }, 404);
  if (!dev && r.metadata?.id !== url.searchParams.get('id')) return json({ error: 'not found' }, 404);
  return new Response(r.value, { headers: { 'content-type': r.metadata?.type || 'image/jpeg', 'cache-control': 'private, max-age=86400', 'x-content-type-options': 'nosniff' } });
}

async function imgOwned(env, key, id) {
  if (!key) return true;
  if (!IMGKEY.test(key)) return false;
  const r = await env.SHARE.getWithMetadata('img:' + key, { type: 'stream' });
  if (r?.value?.cancel) r.value.cancel();
  return !!r?.value && r.metadata?.id === id;
}

// пользователь пишет автору
async function chatSend(req, env) {
  if (!env.STATS) return json({ error: 'unavailable' }, 503);
  if (await tooMany(req, 'cs', 6)) return json({ error: 'slow down' }, 429);
  const d = await readBody(req);
  if (!d) return json({ error: 'bad json' }, 400);
  const id = s(d.id, 32), text = cleanText(d.text, 2000), nick = cleanNick(d.nick), img = s(d.img, 20);
  if (!TID.test(id)) return json({ error: 'bad id' }, 400);
  if (nick.length < 2) return json({ error: 'nick' }, 400);
  if (!text && !img) return json({ error: 'empty' }, 400);
  const db = env.STATS; await schema(db);
  const t = await db.prepare(`SELECT ban_until, ban_reason FROM chat_threads WHERE id = ?`).bind(id).first();
  const ban = banState(t);
  if (ban) return json({ error: 'banned', ...ban }, 403);
  if (!(await imgOwned(env, img, id))) return json({ error: 'bad img' }, 400);
  const today = await db.prepare(`SELECT COUNT(*) n FROM chat_msgs WHERE id = ? AND dev = 0 AND at >= ?`).bind(id, todayISO()).first();
  if ((today?.n ?? 0) >= 60) return json({ error: 'slow down' }, 429);
  const at = new Date().toISOString(), kind = KINDS.has(d.kind) ? d.kind : '';
  const log = cleanText(d.log, 6000), preview = text ? text.slice(0, 200) : '[картинка]';
  const r = await db.prepare(`INSERT INTO chat_msgs (id, at, dev, kind, text, log, img) VALUES (?, ?, 0, ?, ?, ?, ?) RETURNING n`).bind(id, at, kind, text, log, img).first();
  await db.prepare(`INSERT INTO chat_threads (id, first, last, nick, contact, v, z, os, lang, strat, onz, status, dev_unread, user_unread, last_text, last_dev)
      VALUES (?1, ?2, ?2, ?3, '', ?4, ?5, ?6, ?7, ?8, ?9, 'open', 1, 0, ?10, 0)
      ON CONFLICT(id) DO UPDATE SET last = ?2, nick = ?3, v = ?4, z = ?5, os = ?6, lang = ?7, strat = ?8, onz = ?9,
        status = 'open', dev_unread = dev_unread + 1, last_text = ?10, last_dev = 0`)
    .bind(id, at, nick, s(d.v, 16), s(d.z, 16), s(d.os, 20), s(d.lang, 4), s(d.strat, 40), b(d.on), preview).run();
  return json({ ok: true, n: r?.n ?? 0, at });
}

// пользователь забирает переписку (seen: true — открыл чат, ответы прочитаны)
async function chatGet(req, env) {
  if (!env.STATS) return json({ msgs: [], unread: 0 });
  if (await tooMany(req, 'cg', 40)) return json({ error: 'slow down' }, 429);
  const d = await readBody(req);
  if (!d) return json({ error: 'bad json' }, 400);
  const id = s(d.id, 32);
  if (!TID.test(id)) return json({ error: 'bad id' }, 400);
  const db = env.STATS; await schema(db);
  const t = await db.prepare(`SELECT user_unread, status, ban_until, ban_reason FROM chat_threads WHERE id = ?`).bind(id).first();
  if (!t) return json({ msgs: [], unread: 0, exists: false });
  const after = Number.isInteger(d.after) ? d.after : 0;
  const msgs = d.peek ? [] : (await db.prepare(`SELECT n, at, dev, kind, text, img FROM chat_msgs WHERE id = ? AND n > ? ORDER BY n LIMIT 300`).bind(id, after).all()).results.map(msgOut);
  if (d.seen && t.user_unread) await db.prepare(`UPDATE chat_threads SET user_unread = 0 WHERE id = ?`).bind(id).run();
  return json({ msgs, unread: t.user_unread, exists: true, ban: banState(t) });
}

// ---- для автора (по ключу) ----
async function chatThreads(env, url) {
  const db = env.STATS; await schema(db);
  const all = url.searchParams.get('s') === 'all';
  const rows = (await db.prepare(`SELECT t.*, (SELECT COUNT(*) FROM chat_msgs m WHERE m.id = t.id) total FROM chat_threads t
    ${all ? '' : "WHERE status <> 'done'"} ORDER BY t.last DESC LIMIT 300`).all()).results;
  for (const r of rows) r.banned = banState(r);
  const c = await db.prepare(`SELECT COALESCE(SUM(dev_unread), 0) msgs, COALESCE(SUM(dev_unread > 0), 0) threads, COUNT(*) total, COALESCE(SUM(status = 'done'), 0) done FROM chat_threads`).first();
  return json({ threads: rows, unread: c?.msgs ?? 0, unreadThreads: c?.threads ?? 0, total: c?.total ?? 0, done: c?.done ?? 0 });
}

async function chatThread(env, url) {
  const id = url.searchParams.get('id') || '';
  if (!TID.test(id)) return json({ error: 'bad id' }, 400);
  const db = env.STATS; await schema(db);
  const t = await db.prepare(`SELECT * FROM chat_threads WHERE id = ?`).bind(id).first();
  if (!t) return json({ error: 'not found' }, 404);
  t.banned = banState(t);
  const msgs = (await db.prepare(`SELECT * FROM chat_msgs WHERE id = ? ORDER BY n`).bind(id).all()).results
    .map(m => ({ ...msgOut(m), log: m.log || '' }));
  if (t.dev_unread) await db.prepare(`UPDATE chat_threads SET dev_unread = 0 WHERE id = ?`).bind(id).run();
  return json({ thread: t, msgs });
}

async function chatReply(req, env) {
  const d = await readBody(req);
  if (!d) return json({ error: 'bad json' }, 400);
  const id = s(d.id, 32), text = cleanText(d.text, 4000), img = s(d.img, 20);
  if (!TID.test(id) || (!text && !img)) return json({ error: 'bad request' }, 400);
  const db = env.STATS; await schema(db);
  const t = await db.prepare(`SELECT id FROM chat_threads WHERE id = ?`).bind(id).first();
  if (!t) return json({ error: 'not found' }, 404);
  if (!(await imgOwned(env, img, id))) return json({ error: 'bad img' }, 400);
  const at = new Date().toISOString();
  await db.batch([
    db.prepare(`INSERT INTO chat_msgs (id, at, dev, kind, text, log, img) VALUES (?, ?, 1, '', ?, '', ?)`).bind(id, at, text, img),
    db.prepare(`UPDATE chat_threads SET last = ?, user_unread = user_unread + 1, dev_unread = 0, last_text = ?, last_dev = 1 WHERE id = ?`).bind(at, text ? text.slice(0, 200) : '[картинка]', id),
  ]);
  return json({ ok: true });
}

async function chatSet(req, env) {
  const d = await readBody(req);
  if (!d) return json({ error: 'bad json' }, 400);
  const id = s(d.id, 32);
  if (!TID.test(id)) return json({ error: 'bad id' }, 400);
  const db = env.STATS; await schema(db);
  if (d.status === 'delete') {
    const imgs = (await db.prepare(`SELECT img FROM chat_msgs WHERE id = ? AND img <> ''`).bind(id).all()).results;
    for (const r of imgs) { try { await env.SHARE.delete('img:' + r.img); } catch { } }
    await db.batch([db.prepare(`DELETE FROM chat_msgs WHERE id = ?`).bind(id), db.prepare(`DELETE FROM chat_threads WHERE id = ?`).bind(id)]);
  }
  else if (d.status === 'open' || d.status === 'done') await db.prepare(`UPDATE chat_threads SET status = ?, dev_unread = 0 WHERE id = ?`).bind(d.status, id).run();
  else return json({ error: 'bad status' }, 400);
  return json({ ok: true });
}

// блокировка: hours > 0 — на столько часов, -1 — навсегда, 0 — снять
async function chatBan(req, env) {
  const d = await readBody(req);
  if (!d) return json({ error: 'bad json' }, 400);
  const id = s(d.id, 32), hours = Number(d.hours);
  if (!TID.test(id) || !Number.isFinite(hours)) return json({ error: 'bad request' }, 400);
  const until = hours === 0 ? '' : hours < 0 ? 'forever' : new Date(Date.now() + Math.min(hours, 24 * 3650) * 3600000).toISOString();
  const db = env.STATS; await schema(db);
  await db.prepare(`UPDATE chat_threads SET ban_until = ?, ban_reason = ? WHERE id = ?`).bind(until, until ? cleanText(d.reason, 200) : '', id).run();
  return json({ ok: true, until });
}

// ---------- страница статистики (только по секретному ключу) ----------
const esc = t => String(t ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
function same(a, b2) {
  if (typeof a !== 'string' || typeof b2 !== 'string' || a.length !== b2.length) return false;
  let r = 0; for (let i = 0; i < a.length; i++) r |= a.charCodeAt(i) ^ b2.charCodeAt(i);
  return r === 0;
}
function winName(os) {
  const m = /^(\d+)\.(\d+)\.(\d+)/.exec(os || '');
  if (!m) return os || '?';
  if (m[1] === '10') return +m[3] >= 22000 ? 'Windows 11' : 'Windows 10';
  if (m[1] === '6' && m[2] === '1') return 'Windows 7';
  if (m[1] === '6') return 'Windows 8';
  return 'Windows ' + os;
}
function bars(rows, label = r => r.k || '—', total = null) {
  const max = Math.max(1, ...rows.map(r => r.n));
  const sum = total ?? rows.reduce((a, r) => a + r.n, 0);
  if (!rows.length) return '<p class="muted">пока нет данных</p>';
  return '<table class="bars">' + rows.map(r => `<tr><td class="k">${esc(label(r))}</td><td class="b"><i style="width:${Math.round(r.n / max * 100)}%"></i></td>` +
    `<td class="n">${r.n}</td><td class="p">${sum ? Math.round(r.n / sum * 100) + '%' : ''}</td></tr>`).join('') + '</table>';
}

// Каждая установка присылает данные ОДИН раз (если человек согласился), поэтому здесь — срез по установкам, а не «активность».
async function statsData(env) {
  const db = env.STATS;
  await schema(db);
  const d1 = today(), d7 = today(6), d30 = today(29);
  const q = (sql, ...a) => db.prepare(sql).bind(...a).all().then(r => r.results);
  const one = (sql, ...a) => db.prepare(sql).bind(...a).first();
  const [tot, new1, new7, new30, daily, vers, zv, osr, langs, themes, strat, tgr, ecor, autor, gamer, homer, sites] = await Promise.all([
    one(`SELECT COUNT(*) n FROM installs`),
    one(`SELECT COUNT(*) n FROM installs WHERE first >= ?`, d1),
    one(`SELECT COUNT(*) n FROM installs WHERE first >= ?`, d7),
    one(`SELECT COUNT(*) n FROM installs WHERE first >= ?`, d30),
    q(`SELECT first k, COUNT(*) n FROM installs WHERE first >= ? GROUP BY first ORDER BY first`, d30),
    q(`SELECT v k, COUNT(*) n FROM installs GROUP BY v ORDER BY n DESC LIMIT 10`),
    q(`SELECT z k, COUNT(*) n FROM installs GROUP BY z ORDER BY n DESC LIMIT 8`),
    q(`SELECT os k, COUNT(*) n FROM installs GROUP BY os`),
    q(`SELECT lang k, COUNT(*) n FROM installs GROUP BY lang ORDER BY n DESC`),
    q(`SELECT theme k, COUNT(*) n FROM installs GROUP BY theme ORDER BY n DESC`),
    q(`SELECT strat k, COUNT(*) n FROM installs WHERE strat <> '' GROUP BY strat ORDER BY n DESC LIMIT 12`),
    q(`SELECT CASE tg WHEN 1 THEN 'включён' ELSE 'выключен' END k, COUNT(*) n FROM installs GROUP BY tg ORDER BY n DESC`),
    q(`SELECT CASE eco WHEN 1 THEN 'включён' ELSE 'выключен' END k, COUNT(*) n FROM installs GROUP BY eco ORDER BY n DESC`),
    q(`SELECT CASE autoupd WHEN 1 THEN 'включено' ELSE 'выключено' END k, COUNT(*) n FROM installs GROUP BY autoupd ORDER BY n DESC`),
    q(`SELECT CASE WHEN game = '' OR game = 'disabled' THEN 'выключен' ELSE game END k, COUNT(*) n FROM installs GROUP BY k ORDER BY n DESC`),
    q(`SELECT CASE home WHEN 1 THEN 'установлен (C:\\Zapret)' ELSE 'своя папка zapret' END k, COUNT(*) n FROM installs GROUP BY home ORDER BY n DESC`),
    one(`SELECT ROUND(AVG(sites), 1) avg, MAX(sites) mx, SUM(CASE WHEN sites > 0 THEN 1 ELSE 0 END) withs FROM installs`),
  ]);
  const osAgg = {}; for (const r of osr) { const k = winName(r.k); osAgg[k] = (osAgg[k] || 0) + r.n; }
  const osRows = Object.entries(osAgg).map(([k, n]) => ({ k, n })).sort((a, b2) => b2.n - a.n);
  return { at: new Date().toISOString(), tot: tot?.n ?? 0, new1: new1?.n ?? 0, new7: new7?.n ?? 0, new30: new30?.n ?? 0,
    daily, vers, zv, os: osRows, langs, themes, strat, tg: tgr, eco: ecor, autoupd: autor, game: gamer, home: homer,
    sites: { avg: sites?.avg ?? 0, max: sites?.mx ?? 0, withSites: sites?.withs ?? 0 },
    chatUnread: (await db.prepare(`SELECT COALESCE(SUM(dev_unread), 0) n FROM chat_threads`).first())?.n ?? 0 };
}

// ключ автора — только в заголовке X-Zapret-Key (адреса с ?k= попадают в логи и историю, поэтому больше не принимаются)
function isAuthor(req, env) {
  return !!(env.STATS && env.STATS_KEY && same(req.headers.get('x-zapret-key') || '', env.STATS_KEY));
}

function loginPage(wrong) {
  const html = `<!doctype html><html lang="ru"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Zapret · статистика</title>
<style>body{background:#16181d;color:#e6e8ee;font:15px system-ui,sans-serif;display:flex;min-height:100vh;align-items:center;justify-content:center;margin:0}
form{background:#1f2229;padding:28px;border-radius:10px;display:flex;flex-direction:column;gap:12px;min-width:280px}
input,button{font:inherit;padding:10px 12px;border-radius:6px;border:1px solid #333845;background:#14161b;color:inherit}
button{background:#b18cff;color:#16181d;border:0;font-weight:600;cursor:pointer}.e{color:#ff5e62;margin:0}</style></head>
<body><form method="post" action="/stats"><b>Статистика Zapret</b>${wrong ? '<p class="e">Неверный ключ</p>' : ''}
<input type="password" name="k" placeholder="ключ" autocomplete="current-password" autofocus required><button type="submit">Открыть</button></form></body></html>`;
  return new Response(html, { status: wrong ? 403 : 200, headers: { 'content-type': 'text/html; charset=utf-8', 'cache-control': 'no-store', 'x-robots-tag': 'noindex',
    'content-security-policy': "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'" } });
}

async function statsPage(env) {
  const d = await statsData(env);
  const dMax = Math.max(1, ...d.daily.map(r => r.n));
  const card = (t, v) => `<div class="card"><div class="cv">${v}</div><div class="ct">${t}</div></div>`;
  const box = (t, body) => `<div class="box"><h3>${t}</h3>${body}</div>`;
  const html = `<!doctype html><html lang="ru"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Zapret GUI — статистика</title><meta name="robots" content="noindex">
<style>
:root{--bg:#121419;--card:#1A1D24;--line:#2E3340;--t:#E6E8EE;--m:#8A90A0;--a:#3DDC97}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--t);font:14px/1.45 system-ui,Segoe UI,sans-serif;padding:24px 16px}
.w{max-width:1100px;margin:0 auto}h1{font:700 22px ui-monospace,Consolas,monospace;letter-spacing:2px;margin:0 0 4px}
h2{font:700 13px ui-monospace,Consolas,monospace;letter-spacing:1.5px;text-transform:uppercase;color:var(--m);margin:28px 0 10px}
.muted{color:var(--m)}.cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(150px,1fr));gap:10px}
.card{background:var(--card);border:2px solid var(--line);box-shadow:3px 3px 0 #08090B;padding:14px}
.cv{font:700 28px ui-monospace,Consolas,monospace;color:var(--a)}
.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(320px,1fr));gap:12px}
.box{background:var(--card);border:2px solid var(--line);padding:14px}.box h3{margin:0 0 8px;font-size:13px;color:var(--m);font-weight:600}
table.bars{width:100%;border-collapse:collapse}table.bars td{padding:3px 6px 3px 0;white-space:nowrap}
td.k{max-width:180px;overflow:hidden;text-overflow:ellipsis}td.b{width:100%}td.b i{display:block;height:10px;background:var(--a)}
td.n{text-align:right;font-family:ui-monospace,Consolas,monospace}td.p{color:var(--m);text-align:right;font-size:12px}
.dau{display:flex;align-items:flex-end;gap:3px;height:120px;background:var(--card);border:2px solid var(--line);padding:10px}
.dau div{flex:1;background:var(--a);min-height:2px;position:relative}.dau div:hover::after{content:attr(title);position:absolute;bottom:100%;left:50%;transform:translateX(-50%);background:#000;padding:2px 6px;white-space:nowrap;font-size:12px}
</style></head><body><div class="w">
<h1>ZAPRET GUI · СТАТИСТИКА</h1><div class="muted">обновлено ${esc(new Date().toISOString().replace('T', ' ').slice(0, 16))} UTC · каждая установка присылает данные один раз, если человек согласился</div>
<h2>Прислали данные</h2><div class="cards">${card('всего', d.tot)}${card('сегодня', d.new1)}${card('за 7 дней', d.new7)}${card('за 30 дней', d.new30)}</div>
<h2>По дням (30 дней)</h2><div class="dau">${d.daily.map(r => `<div style="height:${Math.max(2, Math.round(r.n / dMax * 100))}%" title="${esc(r.k)}: ${r.n}"></div>`).join('') || '<span class="muted">пока нет данных</span>'}</div>
<h2>Версии и система</h2><div class="grid">
${box('Версия приложения', bars(d.vers))}${box('Версия zapret', bars(d.zv))}${box('Windows', bars(d.os))}
${box('Язык', bars(d.langs) + '<h3 style="margin-top:12px">Тема</h3>' + bars(d.themes))}</div>
<h2>Как пользуются</h2><div class="grid">
${box('Выбранная стратегия', bars(d.strat))}
${box('Telegram-прокси', bars(d.tg) + '<h3 style="margin-top:12px">Игровой фильтр</h3>' + bars(d.game))}
${box('Режим экономии', bars(d.eco) + '<h3 style="margin-top:12px">Автопроверка обновлений zapret</h3>' + bars(d.autoupd))}
${box('Где стоит zapret', bars(d.home))}
${box('Свои сайты', `<p>в среднем <b>${d.sites.avg}</b> · максимум <b>${d.sites.max}</b> · есть свои сайты у <b>${d.sites.withSites}</b> из ${d.tot}</p>`)}</div>
</div></body></html>`;
  return new Response(html, { headers: { 'content-type': 'text/html; charset=utf-8', 'cache-control': 'no-store', 'x-robots-tag': 'noindex', 'content-security-policy': "default-src 'none'; style-src 'unsafe-inline'" } });
}

export default {
  async fetch(req, env) {
    const url = new URL(req.url);

    if (req.method === 'POST' && url.pathname === '/s') {
      if (await tooMany(req)) return json({ error: 'slow down' }, 429);
      const text = await req.text();
      if (text.length > MAX_BYTES) return json({ error: 'too big' }, 413);
      let body;
      try { body = JSON.parse(text); } catch { return json({ error: 'bad json' }, 400); }
      const groups = clean(body);
      if (!groups) return json({ error: 'empty' }, 400);
      for (let i = 0; i < 5; i++) {
        const code = newCode();
        if (await env.SHARE.get(code)) continue;
        await env.SHARE.put(code, JSON.stringify({ groups }), { expirationTtl: TTL });
        return json({ code, days: 30 });
      }
      return json({ error: 'try again' }, 503);
    }

    const m = url.pathname.match(/^\/s\/([A-Za-z0-9-]{4,12})$/);
    if (req.method === 'GET' && m) {
      const code = m[1].toUpperCase().replace(/-/g, '');
      const v = await env.SHARE.get(code);
      if (!v) return json({ error: 'not found' }, 404);
      return new Response(v, { headers: { 'content-type': 'application/json; charset=utf-8', 'x-content-type-options': 'nosniff', 'cache-control': 'no-store' } });
    }

    if (req.method === 'POST' && url.pathname.startsWith('/t/')) return telemetry(req, env, url.pathname);

    // страница статистики в браузере: ключ вводится в форму и уходит в теле POST, а не в адресе
    if (url.pathname === '/stats' && (req.method === 'GET' || req.method === 'POST')) {
      if (!env.STATS || !env.STATS_KEY) return json({ error: 'not found' }, 404);
      if (req.method === 'GET') return loginPage(false);
      if (await tooMany(req, 'login', 5)) return json({ error: 'slow down' }, 429);
      let k = '';
      try { k = String((await req.formData()).get('k') || ''); } catch { }
      return same(k, env.STATS_KEY) ? statsPage(env) : loginPage(true);
    }
    if (req.method === 'POST' && url.pathname === '/chat/send') return chatSend(req, env);
    if (req.method === 'POST' && url.pathname === '/chat/get') return chatGet(req, env);
    const authed = () => isAuthor(req, env);
    if (req.method === 'POST' && url.pathname === '/chat/img') return chatImgUpload(req, env, url, authed());
    const im = url.pathname.match(/^\/chat\/img\/([a-z0-9]{20})$/);
    if (req.method === 'GET' && im) return chatImgGet(env, url, im[1], authed());
    const dev = { 'GET /chat/threads.json': () => chatThreads(env, url), 'GET /chat/thread.json': () => chatThread(env, url),
                  'POST /chat/reply': () => chatReply(req, env), 'POST /chat/set': () => chatSet(req, env),
                  'POST /chat/ban': () => chatBan(req, env) }[req.method + ' ' + url.pathname];
    if (dev) return authed() ? dev() : json({ error: 'not found' }, 404);

    if (req.method === 'GET' && url.pathname === '/stats.json') {
      if (!isAuthor(req, env)) return json({ error: 'not found' }, 404);
      return json(await statsData(env));
    }

    if (url.pathname === '/') return json({ ok: true, service: 'zapret-share' });
    return json({ error: 'not found' }, 404);
  },
};
