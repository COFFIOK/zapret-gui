// Zapret (C#) — скачивание с GitHub с обязательной проверкой.
// Все загрузки (zapret, Telegram-прокси, EasyTier, обновления самого приложения) идут только через этот файл:
//   1) узнаём последний релиз через API GitHub — там же GitHub сообщает SHA-256 каждого файла;
//   2) скачиваем файл только из релизов этого репозитория;
//   3) сверяем SHA-256. Нет суммы или не совпала — файл удаляется, ничего не устанавливается.
// Для обновлений самого приложения дополнительно проверяется подпись автора (ReleaseKey.cs).
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace ZapretCS
{
    class GhAsset
    {
        public string Repo, Tag, Name, Url, Sha256;
    }

    class GhRelease
    {
        public string Repo, Tag;
        public List<GhAsset> Assets = new List<GhAsset>();

        public GhAsset Find(Func<string, bool> pick)
        {
            foreach (var a in Assets) if (pick(a.Name)) return a;
            return null;
        }
        public GhAsset Need(Func<string, bool> pick, string what)
        {
            var a = Find(pick);
            if (a == null) throw new Exception("в релизе " + Tag + " нет " + what);
            return a;
        }
    }

    static class GitHub
    {
        // последний релиз репозитория; без ответа API — ошибка (скачивать вслепую не будем)
        public static GhRelease Latest(string repo)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            string json;
            try
            {
                using (var wc = new WebClient())
                {
                    wc.Headers[HttpRequestHeader.UserAgent] = "ZapretGUI";
                    wc.Encoding = Encoding.UTF8;
                    json = wc.DownloadString("https://api.github.com/repos/" + repo + "/releases/latest");
                }
            }
            catch (WebException ex)
            {
                var r = ex.Response as HttpWebResponse;
                if (r != null && (int)r.StatusCode == 404) throw new Exception("у " + repo + " пока нет релизов");
                throw new Exception("GitHub сейчас не отвечает — попробуй чуть позже", ex);
            }
            var rel = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
            if (rel == null || !rel.ContainsKey("tag_name")) throw new Exception("не удалось прочитать релиз " + repo);
            var res = new GhRelease { Repo = repo, Tag = Convert.ToString(rel["tag_name"]) };
            if (!Sec.SafeTag(res.Tag)) throw new Exception("странное имя версии: " + res.Tag);
            string prefix = "https://github.com/" + repo + "/releases/download/";
            object list;
            if (rel.TryGetValue("assets", out list) && list is object[])
                foreach (var o in (object[])list)
                {
                    var d = o as Dictionary<string, object>;
                    if (d == null) continue;
                    var a = new GhAsset { Repo = repo, Tag = res.Tag, Name = Convert.ToString(d["name"]), Url = Convert.ToString(d["browser_download_url"]) };
                    // файл должен лежать в релизах именно этого репозитория
                    if (!a.Url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    object dg;
                    if (d.TryGetValue("digest", out dg) && dg is string && ((string)dg).StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                        a.Sha256 = ((string)dg).Substring(7).ToLowerInvariant();
                    res.Assets.Add(a);
                }
            return res;
        }

        // скачать и сверить SHA-256; при любой ошибке файл удаляется
        public static void Fetch(GhAsset a, string file, Action<long, long> progress)
        {
            if (string.IsNullOrEmpty(a.Sha256)) throw new Exception("GitHub не сообщил контрольную сумму " + a.Name + " — без проверки не скачиваю");
            try
            {
                Net.Download(a.Url, file, progress);
                if (Sec.Sha256(file) != a.Sha256) throw new Exception("файл " + a.Name + " не совпадает с релизом на GitHub (контрольная сумма)");
            }
            catch
            {
                try { File.Delete(file); } catch { }
                throw;
            }
        }

        // небольшой файл из релиза целиком в память (подпись .sig)
        public static byte[] FetchSmall(string url, int max)
        {
            string tmp = Path.Combine(Sec.TempDir("sig"), "f.bin");
            try
            {
                Net.Download(url, tmp, null);
                var fi = new FileInfo(tmp);
                if (fi.Length == 0 || fi.Length > max) throw new Exception("неверный размер файла подписи");
                return File.ReadAllBytes(tmp);
            }
            finally { try { Directory.Delete(Path.GetDirectoryName(tmp), true); } catch { } }
        }
    }

    // проверка подписи автора: RSA-3072, PKCS#1 v1.5, SHA-256 от всего файла установщика
    static class Signature
    {
        public static bool Configured { get { return !string.IsNullOrEmpty(ReleaseKey.Modulus); } }

        public static bool Verify(string file, byte[] sigFile)
        {
            if (!Configured) return false;
            byte[] sig;
            try { sig = Convert.FromBase64String(Encoding.ASCII.GetString(sigFile).Trim()); }
            catch { return false; }
            byte[] hash;
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(file)) hash = sha.ComputeHash(fs);
            using (var rsa = new RSACryptoServiceProvider())
            {
                rsa.PersistKeyInCsp = false;
                rsa.ImportParameters(new RSAParameters { Modulus = Convert.FromBase64String(ReleaseKey.Modulus), Exponent = Convert.FromBase64String(ReleaseKey.Exponent) });
                return rsa.VerifyHash(hash, CryptoConfig.MapNameToOID("SHA256"), sig);
            }
        }
    }
}
