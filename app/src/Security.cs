// Zapret (C#) — безопасность: защищённая временная папка, проверка скачанных файлов,
// безопасная распаковка архивов, права на папку Zapret
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.RegularExpressions;

namespace ZapretCS
{
    static class Sec
    {
        static readonly SecurityIdentifier Admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        static readonly SecurityIdentifier System_ = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        static readonly SecurityIdentifier Users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);

        static string AppDir { get { return AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'); } }

        // Временная папка, куда может писать только администратор и система.
        // Обычная %TEMP% доступна любой программе пользователя: она могла бы подменить
        // скачанный установщик или файл задачи, пока Zapret (с правами администратора) его не запустил.
        public static string TempDir(string prefix)
        {
            string root = Path.Combine(AppDir, "tmp");
            var ds = new DirectorySecurity();
            ds.SetAccessRuleProtection(true, false);
            const InheritanceFlags inh = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            ds.AddAccessRule(new FileSystemAccessRule(Admins, FileSystemRights.FullControl, inh, PropagationFlags.None, AccessControlType.Allow));
            ds.AddAccessRule(new FileSystemAccessRule(System_, FileSystemRights.FullControl, inh, PropagationFlags.None, AccessControlType.Allow));
            if (!Directory.Exists(root)) Directory.CreateDirectory(root, ds);
            else Directory.SetAccessControl(root, ds);
            string d = Path.Combine(root, prefix + "-" + Guid.NewGuid().ToString("N").Substring(0, 12));
            Directory.CreateDirectory(d);
            return d;
        }
        // папка, в которую может писать только администратор и система (остальным — чтение)
        public static void ProtectedDir(string dir)
        {
            var ds = new DirectorySecurity();
            ds.SetAccessRuleProtection(true, false);
            const InheritanceFlags inh = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            ds.AddAccessRule(new FileSystemAccessRule(Admins, FileSystemRights.FullControl, inh, PropagationFlags.None, AccessControlType.Allow));
            ds.AddAccessRule(new FileSystemAccessRule(System_, FileSystemRights.FullControl, inh, PropagationFlags.None, AccessControlType.Allow));
            ds.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute, inh, PropagationFlags.None, AccessControlType.Allow));
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir, ds);
            else Directory.SetAccessControl(dir, ds);
        }
        public static void CleanTemp()
        {
            try
            {
                string root = Path.Combine(AppDir, "tmp");
                if (!Directory.Exists(root)) return;
                foreach (var d in Directory.GetDirectories(root))
                    try { if (Directory.GetLastWriteTimeUtc(d) < DateTime.UtcNow.AddHours(-6)) Directory.Delete(d, true); } catch { }
            }
            catch (Exception logEx) { Log.Err("clean temp", logEx); }
        }

        // имя версии из GitHub попадает в адреса и имена папок — пропускаем только безопасные символы
        public static bool SafeTag(string tag)
        {
            return tag != null && tag.Length <= 40 && Regex.IsMatch(tag, @"^[A-Za-z0-9][A-Za-z0-9._+-]*$") && !tag.Contains("..");
        }

        // скачиваем только с GitHub
        public static bool TrustedHost(Uri u)
        {
            if (u == null || u.Scheme != Uri.UriSchemeHttps) return false;
            string h = u.Host.ToLowerInvariant();
            return h == "github.com" || h == "api.github.com" || h.EndsWith(".githubusercontent.com");
        }

        public static string Sha256(string file)
        {
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(file))
                return string.Concat(sha.ComputeHash(fs).Select(b => b.ToString("x2")));
        }

        // это вообще программа Windows (а не страница с ошибкой и т.п.)?
        public static bool LooksLikeExe(string file)
        {
            var fi = new FileInfo(file);
            if (!fi.Exists || fi.Length < 64 * 1024) return false;
            using (var fs = File.OpenRead(file)) return fs.ReadByte() == 'M' && fs.ReadByte() == 'Z';
        }

        // распаковка без выхода за пределы папки (защита от «../» в архиве) и от архивов-бомб
        public static void Extract(string zip, string dir, long maxBytes = 400L * 1024 * 1024)
        {
            string root = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
            Directory.CreateDirectory(root);
            long total = 0;
            using (var z = ZipFile.OpenRead(zip))
            {
                if (z.Entries.Count > 5000) throw new Exception("в архиве слишком много файлов");
                foreach (var e in z.Entries)
                {
                    string full = Path.GetFullPath(Path.Combine(root, e.FullName));
                    if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new Exception("архив содержит недопустимый путь: " + e.FullName);
                    if (e.FullName.EndsWith("/") || e.FullName.EndsWith("\\")) { Directory.CreateDirectory(full); continue; }
                    total += e.Length;
                    if (total > maxBytes) throw new Exception("архив слишком большой");
                    Directory.CreateDirectory(Path.GetDirectoryName(full));
                    e.ExtractToFile(full, true);
                }
            }
        }

        // папка Zapret: менять файлы могут только администраторы и система, остальным — только чтение.
        // Иначе любая программа без прав могла бы подменить Zapret.exe или winws.exe,
        // которые потом запускаются с правами администратора.
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
        struct TP { public int Count; public long Luid; public int Attr; }
        [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr h, int acc, out IntPtr tok);
        [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)] static extern bool LookupPrivilegeValue(string s, string n, out long luid);
        [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)] static extern bool AdjustTokenPrivileges(IntPtr tok, bool dis, ref TP n, int len, IntPtr p, IntPtr r);
        static void EnablePrivilege(string name)
        {
            IntPtr t; if (!OpenProcessToken(Process.GetCurrentProcess().Handle, 0x28, out t)) return;
            var tp = new TP { Count = 1, Attr = 2 };
            if (LookupPrivilegeValue(null, name, out tp.Luid)) AdjustTokenPrivileges(t, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
        }

        // сама папка: администраторы и система — полный доступ, пользователи — чтение;
        // у всего внутри владелец — администраторы, своих прав нет, только наследование от папки
        static void Repair(string dir)
        {
            foreach (var p in new[] { "SeTakeOwnershipPrivilege", "SeRestorePrivilege", "SeBackupPrivilege" }) EnablePrivilege(p);
            const InheritanceFlags inh = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            var own = new DirectorySecurity(); own.SetOwner(Admins); Directory.SetAccessControl(dir, own);
            var root = new DirectorySecurity();
            root.SetAccessRuleProtection(true, false);
            root.AddAccessRule(new FileSystemAccessRule(Admins, FileSystemRights.FullControl, inh, PropagationFlags.None, AccessControlType.Allow));
            root.AddAccessRule(new FileSystemAccessRule(System_, FileSystemRights.FullControl, inh, PropagationFlags.None, AccessControlType.Allow));
            root.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute, inh, PropagationFlags.None, AccessControlType.Allow));
            Directory.SetAccessControl(dir, root);
            foreach (var path in Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    if (Directory.Exists(path))
                    {
                        var o = new DirectorySecurity(); o.SetOwner(Admins); Directory.SetAccessControl(path, o);
                        var d = new DirectorySecurity(); d.SetAccessRuleProtection(false, false); Directory.SetAccessControl(path, d);
                    }
                    else
                    {
                        var o = new FileSecurity(); o.SetOwner(Admins); File.SetAccessControl(path, o);
                        var d = new FileSecurity(); d.SetAccessRuleProtection(false, false); File.SetAccessControl(path, d);
                    }
                }
                catch (Exception ex) { Log.Write("security: " + path + ": " + ex.Message); }
            }
        }
        public static void HardenFolder(string dir)
        {
            try
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
                var ds = Directory.GetAccessControl(dir);
                bool weak = false;
                foreach (FileSystemAccessRule r in ds.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                {
                    if (r.AccessControlType != AccessControlType.Allow) continue;
                    var sid = (SecurityIdentifier)r.IdentityReference;
                    if (sid.Equals(Admins) || sid.Equals(System_)) continue;
                    if (sid.IsWellKnown(WellKnownSidType.CreatorOwnerSid)) { weak = true; break; }
                    const FileSystemRights write = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership | FileSystemRights.WriteAttributes;
                    if ((r.FileSystemRights & write) != 0) { weak = true; break; }
                }
                // файлы с пустыми правами (так их оставляла ранняя сборка 2.0.1) — тоже чиним
                if (!weak)
                    foreach (var f in new[] { System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName })
                        try { if (File.GetAccessControl(f).GetAccessRules(true, true, typeof(SecurityIdentifier)).Count == 0) weak = true; } catch { weak = true; }
                if (!weak) return;
                Log.Write("security: права на " + dir + " исправлены");
                Repair(dir);
            }
            catch (Exception ex) { Log.Write("security harden: " + ex.Message); }
        }
    }
}
