// Zapret Dev — ключ подписи релизов на флешке · by COFFIOK
// Файл <флешка>:\ZapretKey\release-key.zkey — секретный ключ RSA, зашифрованный паролем:
//   PBKDF2-SHA256 (400 000 повторов) -> AES-256-CBC + HMAC-SHA256 (подделку или неверный пароль видно сразу).
// Без пароля файл бесполезен, поэтому потерянная флешка не даёт никому подписать обновление.
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ZapretDevTool
{
    static class UsbKey
    {
        public const string Folder = "ZapretKey", FileName = "release-key.zkey";
        static readonly byte[] Magic = Encoding.ASCII.GetBytes("ZKEY1");
        const int Iter = 400000;

        // ищем ключ на всех подключённых дисках, кроме системного
        public static string Find()
        {
            string sys = Path.GetPathRoot(Environment.SystemDirectory);
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (!d.IsReady || string.Equals(d.RootDirectory.FullName, sys, StringComparison.OrdinalIgnoreCase)) continue;
                    if (d.DriveType != DriveType.Removable && d.DriveType != DriveType.Fixed) continue;
                    string p = Path.Combine(d.RootDirectory.FullName, Folder, FileName);
                    if (File.Exists(p)) return p;
                }
                catch { }
            }
            return null;
        }

        // первая вставленная флешка (для создания ключа)
        public static string FirstRemovable()
        {
            foreach (var d in DriveInfo.GetDrives())
                try { if (d.DriveType == DriveType.Removable && d.IsReady) return d.RootDirectory.FullName; } catch { }
            return null;
        }

        static void Derive(string password, byte[] salt, int iter, out byte[] encKey, out byte[] macKey)
        {
            using (var kdf = new Rfc2898DeriveBytes(Encoding.UTF8.GetBytes(password), salt, iter, HashAlgorithmName.SHA256))
            {
                var k = kdf.GetBytes(64);
                encKey = k.Take(32).ToArray();
                macKey = k.Skip(32).ToArray();
            }
        }

        public static void Save(string file, string keyXml, string password)
        {
            var salt = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(salt);
            byte[] encKey, macKey;
            Derive(password, salt, Iter, out encKey, out macKey);
            byte[] iv, cipher;
            using (var aes = Aes.Create())
            {
                aes.KeySize = 256; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
                aes.Key = encKey; aes.GenerateIV(); iv = aes.IV;
                var plain = Encoding.UTF8.GetBytes(keyXml);
                using (var enc = aes.CreateEncryptor()) cipher = enc.TransformFinalBlock(plain, 0, plain.Length);
            }
            var ms = new MemoryStream();
            ms.Write(Magic, 0, Magic.Length);
            ms.Write(BitConverter.GetBytes(Iter), 0, 4);
            ms.Write(salt, 0, salt.Length);
            ms.Write(iv, 0, iv.Length);
            ms.Write(cipher, 0, cipher.Length);
            var data = ms.ToArray();
            byte[] mac;
            using (var h = new HMACSHA256(macKey)) mac = h.ComputeHash(data);
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            string tmp = file + ".tmp";
            File.WriteAllBytes(tmp, data.Concat(mac).ToArray());
            if (File.Exists(file)) File.Delete(file);
            File.Move(tmp, file);
        }

        // вернёт XML ключа; неверный пароль или испорченный файл — исключение
        public static string Open(string file, string password)
        {
            var all = File.ReadAllBytes(file);
            if (all.Length < 5 + 4 + 16 + 16 + 16 + 32 || !all.Take(5).SequenceEqual(Magic)) throw new Exception("это не файл ключа Zapret");
            int iter = BitConverter.ToInt32(all, 5);
            if (iter < 100000 || iter > 10000000) throw new Exception("файл ключа повреждён");
            var salt = all.Skip(9).Take(16).ToArray();
            var iv = all.Skip(25).Take(16).ToArray();
            var data = all.Take(all.Length - 32).ToArray();
            var mac = all.Skip(all.Length - 32).ToArray();
            byte[] encKey, macKey;
            Derive(password, salt, iter, out encKey, out macKey);
            byte[] want;
            using (var h = new HMACSHA256(macKey)) want = h.ComputeHash(data);
            int diff = 0;
            for (int i = 0; i < 32; i++) diff |= want[i] ^ mac[i];
            if (diff != 0) throw new Exception("неверный пароль");
            var cipher = data.Skip(41).ToArray();
            using (var aes = Aes.Create())
            {
                aes.KeySize = 256; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
                aes.Key = encKey; aes.IV = iv;
                using (var dec = aes.CreateDecryptor()) return Encoding.UTF8.GetString(dec.TransformFinalBlock(cipher, 0, cipher.Length));
            }
        }
    }

    // открытый ключ в src\ReleaseKey.cs и отпечаток ключа
    static class KeyInfo
    {
        public static string ReadModulus(string releaseKeyCs)
        {
            if (!File.Exists(releaseKeyCs)) return null;
            var m = Regex.Match(File.ReadAllText(releaseKeyCs), "Modulus = \"([A-Za-z0-9+/=]+)\"");
            return m.Success ? m.Groups[1].Value : null;
        }

        public static string Fingerprint(string modulusB64)
        {
            if (string.IsNullOrEmpty(modulusB64)) return "—";
            using (var sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(Convert.FromBase64String(modulusB64)).Take(8).Select(b => b.ToString("X2")));
        }

        public static void WriteReleaseKey(string releaseKeyCs, RSAParameters pub)
        {
            string cs =
                "// Zapret (C#) — открытый ключ автора для проверки подписи обновлений.\r\n" +
                "// Этот файл заполняет ZapretDev.exe (ключ на флешке). Секретная половина ключа хранится только у автора\r\n" +
                "// и в программу не попадает. Пока ключ пустой, обновления проверяются только по контрольной сумме GitHub.\r\n" +
                "namespace ZapretCS\r\n{\r\n    static class ReleaseKey\r\n    {\r\n" +
                "        public const string Modulus = \"" + Convert.ToBase64String(pub.Modulus) + "\";\r\n" +
                "        public const string Exponent = \"" + Convert.ToBase64String(pub.Exponent) + "\";\r\n" +
                "    }\r\n}\r\n";
            File.WriteAllText(releaseKeyCs, cs, new UTF8Encoding(true));
        }
    }
}
