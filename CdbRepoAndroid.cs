// 카드 DB 레포(Android) — 윈도우 앱(CdbRepos.cs)의 이식판.
//   · git 이 없으므로 **zip(codeload)** 만 사용한다.
//   · 캐시: <files>/cdb/repos/<owner>_<repo>
//   · 목록은 EDOPro 설정(configs.json·user_configs.json)에서 읽은 기본 5개(Defaults).
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;

namespace ReplayVideoCore
{
    public sealed class CdbRepo
    {
        public string Name = "";
        public string Url = "";
        public string SubPath = "";
        public bool IsLanguage;
        public bool Enabled = true;
        public bool Update = true;
        public string Status = "";

        public CdbRepo Clone()
        {
            return new CdbRepo { Name = Name, Url = Url, SubPath = SubPath, IsLanguage = IsLanguage, Enabled = Enabled, Update = Update, Status = Status };
        }

        public string Slug
        {
            get
            {
                string s = (Url ?? "").Replace("https://github.com/", "").Replace("http://github.com/", "").TrimEnd('/');
                if (s.EndsWith(".git")) s = s.Substring(0, s.Length - 4);
                return s.Replace('/', '_').Replace('\\', '_');
            }
        }
    }

    public static class CdbRepoStore
    {
        private static string _root = "";
        private static readonly object _lock = new object();

        /// <summary>앱 시작 시 한 번 호출 — files 폴더 아래 cdb 캐시를 만든다.</summary>
        public static void Init(string filesDir)
        {
            _root = Path.Combine(filesDir, "cdb");
            try { Directory.CreateDirectory(Path.Combine(_root, "repos")); } catch { }
        }

        public static string CacheRoot
        {
            get { return string.IsNullOrEmpty(_root) ? "" : Path.Combine(_root, "repos"); }
        }

        public static string LocalDirOf(CdbRepo r)
        {
            return Path.Combine(CacheRoot, r.Slug);
        }

        /// <summary>APK 에 내장된 기본 cdb 를 풀어 두는 폴더.</summary>
        public static string BundledDir
        {
            get { return string.IsNullOrEmpty(_root) ? "" : Path.Combine(_root, "bundled"); }
        }

        private static string ListPath
        {
            get { return string.IsNullOrEmpty(_root) ? "" : Path.Combine(_root, "repos.json"); }
        }

        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions { WriteIndented = true, IncludeFields = true };

        /// <summary>윈도우 앱과 같은 기본 목록(EDOPro configs 두 파일에서 확인한 레포).</summary>
        public static List<CdbRepo> Defaults()
        {
            return new List<CdbRepo>
            {
                new CdbRepo { Name = "한국어 패치", Url = "https://github.com/Team-AllYGOPro/edopro-korean", SubPath = "./repositories/languages/한국어", IsLanguage = true },
                new CdbRepo { Name = "한국어 패치 업데이트", Url = "https://github.com/Team-AllYGOPro/edopro-korean-delta", SubPath = "./repositories/languages/한국어-1", IsLanguage = true },
                new CdbRepo { Name = "한국어 패치(Hellz)", Url = "https://github.com/Hellzgate/EdoKroCDB", SubPath = "./repositories/languages/한국어-3", IsLanguage = true },
                new CdbRepo { Name = "커스텀 한국어 패치", Url = "https://github.com/CCC-Server/EMP-TY", SubPath = "./repositories/languages/한국어-2", IsLanguage = true, Update = false },
                new CdbRepo { Name = "최신 카드 업데이트", Url = "https://github.com/ProjectIgnis/DeltaBagooska", SubPath = "./repositories/delta-bagooska" }
            };
        }

        public static List<CdbRepo> Load()
        {
            lock (_lock)
            {
                try
                {
                    string p = ListPath;
                    if (!string.IsNullOrEmpty(p) && File.Exists(p))
                    {
                        var list = JsonSerializer.Deserialize<List<CdbRepo>>(File.ReadAllText(p), JsonOpts);
                        if (list != null && list.Count > 0) return list;
                    }
                }
                catch { }
                return Defaults();
            }
        }

        public static void Save(IEnumerable<CdbRepo> repos)
        {
            lock (_lock)
            {
                try
                {
                    string p = ListPath;
                    if (string.IsNullOrEmpty(p)) return;
                    File.WriteAllText(p, JsonSerializer.Serialize(repos.ToList(), JsonOpts));
                }
                catch { }
            }
        }

        // ── 다운로드(zip 전용) ───────────────────────────────
        public static bool Sync(CdbRepo r, Action<string> log)
        {
            if (r == null || string.IsNullOrEmpty(r.Url)) return false;
            string dir = LocalDirOf(r);
            try { Directory.CreateDirectory(CacheRoot); } catch { }

            string slug = r.Slug;
            int us = slug.IndexOf('_');
            if (us <= 0) { r.Status = "주소 형식 오류"; return false; }
            string owner = slug.Substring(0, us);
            string repo = slug.Substring(us + 1);
            string zip = Path.Combine(CacheRoot, slug + ".zip");

            try
            {
                using (var http = new HttpClient())
                {
                    http.Timeout = TimeSpan.FromMinutes(10);
                    bool got = false;
                    foreach (string branch in new[] { "main", "master" })
                    {
                        string url = string.Format("https://codeload.github.com/{0}/{1}/zip/refs/heads/{2}", owner, repo, branch);
                        try
                        {
                            log?.Invoke("  받는 중… " + url);
                            byte[] bytes = http.GetByteArrayAsync(url).GetAwaiter().GetResult();
                            if (bytes == null || bytes.Length < 1000) continue;
                            File.WriteAllBytes(zip, bytes);
                            got = true;
                            break;
                        }
                        catch { }
                    }
                    if (!got) { r.Status = "다운로드 실패(비공개/주소 오류)"; return false; }

                    string tmp = Path.Combine(CacheRoot, "_unzip_" + Guid.NewGuid().ToString("N").Substring(0, 6));
                    ZipFile.ExtractToDirectory(zip, tmp);
                    string inner = Directory.GetDirectories(tmp).FirstOrDefault() ?? tmp;

                    DeleteDirSafe(dir);
                    Directory.CreateDirectory(dir);
                    MoveDir(inner, dir);
                    try { DeleteDirSafe(tmp); } catch { }
                    try { File.Delete(zip); } catch { }
                }
                int n = CountCdb(dir);
                r.Status = n > 0 ? ("완료 · cdb " + n + "개") : "완료(cdb 없음)";
                return n > 0;
            }
            catch (Exception ex)
            {
                r.Status = "오류: " + ex.Message;
                log?.Invoke("  ✗ " + ex.Message);
                return false;
            }
        }

        private static void MoveDir(string from, string to)
        {
            foreach (string d in Directory.GetDirectories(from))
            {
                string target = Path.Combine(to, Path.GetFileName(d));
                Directory.CreateDirectory(target);
                MoveDir(d, target);
            }
            foreach (string f in Directory.GetFiles(from))
            {
                string target = Path.Combine(to, Path.GetFileName(f));
                if (File.Exists(target)) File.Delete(target);
                File.Move(f, target);
            }
        }

        private static void DeleteDirSafe(string dir)
        {
            if (!Directory.Exists(dir)) return;
            for (int i = 0; i < 3; i++)
            {
                try { Directory.Delete(dir, true); return; }
                catch { System.Threading.Thread.Sleep(150); }
            }
            try { Directory.Move(dir, dir + ".old-" + DateTime.Now.ToString("HHmmss")); } catch { }
        }

        public static int CountCdb(string dir)
        {
            try { return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.cdb", SearchOption.AllDirectories).Length : 0; }
            catch { return 0; }
        }

        /// <summary>카드 정보를 읽을 cdb 파일들 — 한국어 cdb 를 앞에 둔다(먼저 연 것이 이긴다).</summary>
        public static List<string> ResolveCdbFiles(IEnumerable<CdbRepo> repos)
        {
            var files = new List<string>();
            if (repos != null)
                foreach (var r in repos)
                {
                    if (!r.Enabled) continue;
                    string d = LocalDirOf(r);
                    if (!Directory.Exists(d)) continue;
                    try
                    {
                        foreach (string f in Directory.GetFiles(d, "*.cdb", SearchOption.AllDirectories))
                            if (!files.Contains(f)) files.Add(f);
                    }
                    catch { }
                }
            // 내장(배포 포함) cdb — 레포가 없을 때의 기본값
            try
            {
                string b = BundledDir;
                if (!string.IsNullOrEmpty(b) && Directory.Exists(b))
                    foreach (string f in Directory.GetFiles(b, "*.cdb"))
                        if (!files.Contains(f)) files.Add(f);
            }
            catch { }

            // 한국어 우선 정렬(안정 정렬)
            var ordered = new List<string>();
            for (int rank = 0; rank <= 1; rank++)
                foreach (string f in files)
                    if (Rank(f) == rank) ordered.Add(f);
            return ordered;
        }

        public static int Rank(string path)
        {
            string p = (path ?? "").ToLowerInvariant();
            return (p.Contains("한국") || p.Contains("korean") || p.Contains("languages")) ? 0 : 1;
        }

        public static int TotalCdb(IEnumerable<CdbRepo> repos)
        {
            int n = 0;
            if (repos == null) return 0;
            foreach (var r in repos) if (r.Enabled) n += CountCdb(LocalDirOf(r));
            return n;
        }
    }
}
