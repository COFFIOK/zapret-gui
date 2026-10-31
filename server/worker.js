// Zapret · сервер кодов «Поделиться сайтами» · by COFFIOK
// Cloudflare Worker + KV (переменная SHARE). Код живёт 30 дней.
//   POST /s        тело: {"groups":{"Имя группы":["site.com", ...]}}  ->  {"code":"K7M4QX"}
//   GET  /s/K7M4QX                                                    ->  {"groups":{...}}

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
async function tooMany(req) {
  const ip = req.headers.get('cf-connecting-ip') || 'x';
  const key = new Request('https://rl.local/' + encodeURIComponent(ip) + '/' + Math.floor(Date.now() / 60000));
  const cache = caches.default;
  const hit = await cache.match(key);
  const n = hit ? parseInt(await hit.text(), 10) || 0 : 0;
  if (n >= 10) return true;
  await cache.put(key, new Response(String(n + 1), { headers: { 'cache-control': 'max-age=70' } }));
  return false;
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

    if (url.pathname === '/') return json({ ok: true, service: 'zapret-share' });
    return json({ error: 'not found' }, 404);
  },
};
