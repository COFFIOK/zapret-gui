// Zapret (C#) — QR-код: байтовый режим, уровень коррекции L, версии 1–6 (как в старой версии)
using System;
using System.Collections.Generic;
using System.Text;

namespace ZapretCS
{
    static class Qr
    {
        public static bool[,] Encode(string text)
        {
            byte[] data = Encoding.UTF8.GetBytes(text);
            int[,] caps = { { 0, 0, 0 }, { 19, 7, 1 }, { 34, 10, 1 }, { 55, 15, 1 }, { 80, 20, 1 }, { 108, 26, 1 }, { 136, 18, 2 } };
            int ver = 0;
            for (int v = 1; v <= 6; v++) if (4 + 8 + 8 * data.Length <= caps[v, 0] * 8) { ver = v; break; }
            if (ver == 0) throw new Exception("QR: слишком длинный текст");
            int dcw = caps[ver, 0], ecn = caps[ver, 1], blocks = caps[ver, 2];

            // биты данных
            var bits = new List<int>();
            Action<int, int> add = (val, len) => { for (int i = len - 1; i >= 0; i--) bits.Add((val >> i) & 1); };
            add(4, 4); add(data.Length, 8);
            foreach (var b in data) add(b, 8);
            int max = dcw * 8;
            for (int i = 0; i < 4 && bits.Count < max; i++) bits.Add(0);
            while (bits.Count % 8 != 0) bits.Add(0);
            var cw = new List<int>();
            for (int i = 0; i < bits.Count; i += 8) { int v = 0; for (int j = 0; j < 8; j++) v = (v << 1) | bits[i + j]; cw.Add(v); }
            int[] pad = { 0xEC, 0x11 }; int k = 0;
            while (cw.Count < dcw) { cw.Add(pad[k % 2]); k++; }

            // Рид — Соломон в GF(256)
            var exp = new int[512]; var log = new int[256]; int x = 1;
            for (int i = 0; i < 255; i++) { exp[i] = x; log[x] = i; x <<= 1; if ((x & 0x100) != 0) x ^= 0x11D; }
            for (int i = 255; i < 512; i++) exp[i] = exp[i - 255];
            var gen = new List<int> { 1 };
            for (int i = 0; i < ecn; i++)
            {
                var ng = new int[gen.Count + 1];
                for (int j = 0; j < gen.Count; j++)
                {
                    ng[j] ^= gen[j];
                    if (gen[j] != 0) ng[j + 1] ^= exp[log[gen[j]] + i];
                }
                gen = new List<int>(ng);
            }
            int per = dcw / blocks;
            var dB = new List<int[]>(); var eB = new List<int[]>();
            for (int bI = 0; bI < blocks; bI++)
            {
                var blk = cw.GetRange(bI * per, per).ToArray();
                var rem = new int[ecn];
                foreach (var c in blk)
                {
                    int f = c ^ rem[0];
                    for (int j = 0; j < ecn - 1; j++) rem[j] = rem[j + 1];
                    rem[ecn - 1] = 0;
                    if (f != 0) for (int j = 0; j < ecn; j++) if (gen[j + 1] != 0) rem[j] ^= exp[log[gen[j + 1]] + log[f]];
                }
                dB.Add(blk); eB.Add(rem);
            }
            var fin = new List<int>();
            for (int i = 0; i < per; i++) foreach (var b in dB) fin.Add(b[i]);
            for (int i = 0; i < ecn; i++) foreach (var b in eB) fin.Add(b[i]);

            // матрица
            int n = 17 + 4 * ver;
            var m = new int[n, n]; var fn = new bool[n, n];
            Action<int, int, int> set = (r, c, v) => { m[r, c] = v; fn[r, c] = true; };
            foreach (var o in new[] { new[] { 0, 0 }, new[] { 0, n - 7 }, new[] { n - 7, 0 } })
                for (int r = -1; r <= 7; r++)
                    for (int c = -1; c <= 7; c++)
                    {
                        int rr = o[0] + r, cc = o[1] + c;
                        if (rr < 0 || cc < 0 || rr >= n || cc >= n) continue;
                        bool inside = (r >= 0 && r <= 6 && c >= 0 && c <= 6) && ((r == 0 || r == 6 || c == 0 || c == 6) || (r >= 2 && r <= 4 && c >= 2 && c <= 4));
                        set(rr, cc, inside ? 1 : 0);
                    }
            for (int i = 8; i < n - 8; i++) { set(6, i, i % 2 == 0 ? 1 : 0); set(i, 6, i % 2 == 0 ? 1 : 0); }
            int[] al = { 0, 0, 18, 22, 26, 30, 34 };
            if (ver >= 2)
            {
                int a = al[ver];
                for (int r = -2; r <= 2; r++) for (int c = -2; c <= 2; c++) set(a + r, a + c, Math.Max(Math.Abs(r), Math.Abs(c)) != 1 ? 1 : 0);
            }
            set(n - 8, 8, 1);
            for (int i = 0; i < 9; i++) { if (!fn[8, i]) set(8, i, 0); if (!fn[i, 8]) set(i, 8, 0); }
            for (int i = 0; i < 8; i++) { set(8, n - 1 - i, 0); set(n - 1 - i, 8, 0); }
            set(n - 8, 8, 1);

            // размещение данных змейкой
            var all = new List<int>();
            foreach (var c in fin) for (int i = 7; i >= 0; i--) all.Add((c >> i) & 1);
            int idx = 0; bool up = true;
            for (int col = n - 1; col > 0; col -= 2)
            {
                if (col == 6) col--;
                for (int t = 0; t < n; t++)
                {
                    int r = up ? n - 1 - t : t;
                    foreach (var cc in new[] { col, col - 1 })
                        if (!fn[r, cc]) { m[r, cc] = idx < all.Count ? all[idx] : 0; idx++; }
                }
                up = !up;
            }

            // маски + форматная информация, выбираем лучшую
            int[,] best = null; int bestScore = int.MaxValue;
            for (int mask = 0; mask < 8; mask++)
            {
                var q = (int[,])m.Clone();
                for (int r = 0; r < n; r++)
                    for (int c = 0; c < n; c++)
                    {
                        if (fn[r, c]) continue;
                        bool inv;
                        switch (mask)
                        {
                            case 0: inv = (r + c) % 2 == 0; break;
                            case 1: inv = r % 2 == 0; break;
                            case 2: inv = c % 3 == 0; break;
                            case 3: inv = (r + c) % 3 == 0; break;
                            case 4: inv = (r / 2 + c / 3) % 2 == 0; break;
                            case 5: inv = ((r * c) % 2 + (r * c) % 3) == 0; break;
                            case 6: inv = (((r * c) % 2 + (r * c) % 3) % 2) == 0; break;
                            default: inv = (((r + c) % 2 + (r * c) % 3) % 2) == 0; break;
                        }
                        if (inv) q[r, c] = 1 - q[r, c];
                    }
                int fmt = (1 << 3) | mask;   // уровень L = 01
                int vv = fmt << 10;
                for (int i = 14; i >= 10; i--) if (((vv >> i) & 1) != 0) vv ^= 0x537 << (i - 10);
                int fb = ((fmt << 10) | vv) ^ 0x5412;
                for (int i = 0; i < 15; i++)
                {
                    int bit = (fb >> i) & 1;
                    if (i < 6) q[i, 8] = bit; else if (i < 8) q[i + 1, 8] = bit; else q[n - 15 + i, 8] = bit;
                    if (i < 8) q[8, n - 1 - i] = bit; else if (i < 9) q[8, 15 - i] = bit; else q[8, 14 - i] = bit;
                }
                q[n - 8, 8] = 1;
                // штраф (упрощённо: правила 1 и 2 и баланс)
                int score = 0, dark = 0;
                for (int r = 0; r < n; r++)
                {
                    int runR = 1, runC = 1;
                    for (int c = 0; c < n; c++)
                    {
                        dark += q[r, c];
                        if (c > 0)
                        {
                            if (q[r, c] == q[r, c - 1]) runR++; else { if (runR >= 5) score += runR - 2; runR = 1; }
                            if (q[c, r] == q[c - 1, r]) runC++; else { if (runC >= 5) score += runC - 2; runC = 1; }
                        }
                        if (r < n - 1 && c < n - 1) { int s = q[r, c] + q[r, c + 1] + q[r + 1, c] + q[r + 1, c + 1]; if (s == 0 || s == 4) score += 3; }
                    }
                    if (runR >= 5) score += runR - 2; if (runC >= 5) score += runC - 2;
                }
                score += (int)Math.Floor(Math.Abs(dark * 100.0 / (n * n) - 50) / 5) * 10;
                if (score < bestScore) { bestScore = score; best = q; }
            }
            var res = new bool[n, n];
            for (int r = 0; r < n; r++) for (int c = 0; c < n; c++) res[r, c] = best[r, c] == 1;
            return res;
        }
    }
}
