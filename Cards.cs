// 카드 이름/설명/이미지 — EDOPro 없이 YGOPRODeck API 로 해결한다(안드로이드).
//   이름: language=ko → (없으면) 영문 → #ID
//   설명: API ko → 번역(한국어가 아니면) → 영문
//   그림: images.ygoprodeck.com → 로컬 캐시
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using WindBotAIEditor.Services;

namespace ReplayVideoCore
{
    public sealed class CardRec
    {
        public string Name, Type, Desc;
        public int Atk = -1, Def = -1;
    }

    public sealed class CardLibrary : ICardText
    {
        private readonly string _root;
        private readonly string _pics;
        private readonly Dictionary<int, CardRec> _api = new Dictionary<int, CardRec>();
        private readonly HashSet<int> _koMiss = new HashSet<int>();
        private readonly Dictionary<int, string> _names = new Dictionary<int, string>();
        private readonly Dictionary<int, string> _descs = new Dictionary<int, string>();
        private readonly Dictionary<int, string> _trans = new Dictionary<int, string>();
        private readonly Dictionary<int, int> _atk = new Dictionary<int, int>();
        private readonly object _lock = new object();

        public bool ApiEnabled = true;
        public bool TranslateEnabled = true;
        public Action<string> Log;
        /// <summary>cdb 카드 DB(레포에서 받은 것). null 이면 API 만 사용.</summary>
        public CdbDb Cdb;

        public CardLibrary(string root, string picsDir)
        {
            _root = root;
            _pics = picsDir;
            try { Directory.CreateDirectory(CardsDir); } catch { }
            try { Directory.CreateDirectory(ImgDir); } catch { }
            try { Directory.CreateDirectory(TransDir); } catch { }
        }

        private string CardsDir { get { return Path.Combine(_root, "cards"); } }
        private string ImgDir { get { return Path.Combine(_root, "imgcache"); } }
        private string TransDir { get { return Path.Combine(_root, "trans"); } }

        // ── 미리 받기 ─────────────────────────────────────────
        public void Prefetch(ReplayFile rep, Action<int, int> progress)
        {
            if (!ApiEnabled) return;
            var codes = new List<int>();
            var seen = new HashSet<int>();
            var hi = new HashSet<int>();
            foreach (ReplayPacket p in rep.Packets)
            {
                int code = 0;
                switch (p.Message)
                {
                    case ReplayMsg.MOVE:
                    case ReplayMsg.SUMMONING:
                    case ReplayMsg.SPSUMMONING:
                    case ReplayMsg.SET:
                    case ReplayMsg.CHAINING:
                    case ReplayMsg.ATTACK:
                    case ReplayMsg.POS_CHANGE:
                        code = p.Length >= 4 ? (int)p.U32(0) : 0; break;
                    case ReplayMsg.DRAW:
                        int cnt = p.U8(1), off = 5;
                        for (int k = 0; k < cnt && off + 4 <= p.Length; k++, off += 8)
                        {
                            int cc = (int)p.U32(off);
                            if (cc > 0 && seen.Add(cc)) codes.Add(cc);
                        }
                        continue;
                }
                if (code > 0 && seen.Add(code)) codes.Add(code);
                if (code > 0 && p.Message == ReplayMsg.CHAINING) hi.Add(code);
            }

            int total = codes.Count, next = -1, done = 0;
            var tasks = new List<Task>();
            for (int t = 0; t < 4; t++)
                tasks.Add(Task.Run(delegate
                {
                    while (true)
                    {
                        int i = System.Threading.Interlocked.Increment(ref next);
                        if (i >= total) break;
                        ApiRec(codes[i]);
                        if (hi.Contains(codes[i])) DownloadImage(codes[i]);
                        int d = System.Threading.Interlocked.Increment(ref done);
                        if (progress != null) progress(d, total);
                    }
                }));
            try { Task.WaitAll(tasks.ToArray()); } catch { }
            Log?.Invoke("카드 정보 " + total + "종 준비");
        }

        /// <summary>설명 번역을 미리 데운다(2 병렬, 최대 40종).</summary>
        public void WarmTranslations()
        {
            if (!TranslateEnabled) return;
            var codes = new List<int>();
            lock (_api) foreach (int k in _api.Keys) codes.Add(k);
            if (codes.Count == 0) return;
            int total = Math.Min(codes.Count, 40), next = -1;
            var tasks = new List<Task>();
            for (int t = 0; t < 2; t++)
                tasks.Add(Task.Run(delegate
                {
                    while (true)
                    {
                        int i = System.Threading.Interlocked.Increment(ref next);
                        if (i >= total) break;
                        DescKo(codes[i]);
                    }
                }));
            try { Task.WaitAll(tasks.ToArray()); } catch { }
        }

        // ── ICardText ─────────────────────────────────────────
        public string NameOf(int code)
        {
            if (code <= 0) return "";
            lock (_names) { string v; if (_names.TryGetValue(code, out v)) return v; }

            CdbCard cd = null;
            if (Cdb != null) Cdb.TryGet(code, out cd);

            // ① cdb 이름이 한국어면 그대로 (가장 정확·빠름)
            if (cd != null && !string.IsNullOrEmpty(cd.Name) && HasHangul(cd.Name))
            { lock (_names) _names[code] = cd.Name; return cd.Name; }

            // ② API 한국어
            CardRec ko = ApiKo(code);
            if (ko != null && !string.IsNullOrEmpty(ko.Name) && HasHangul(ko.Name))
            { lock (_names) _names[code] = ko.Name; return ko.Name; }

            // ③ cdb 이름(영문 등 — 커스텀 카드는 API 에 없어 여기서 해결)
            if (cd != null && !string.IsNullOrEmpty(cd.Name))
            { lock (_names) _names[code] = cd.Name; return cd.Name; }

            // ④ API(영문/기타) → #ID
            CardRec r = ApiRec(code);
            string name = (r != null && !string.IsNullOrEmpty(r.Name)) ? r.Name : ("#" + code);
            lock (_names) _names[code] = name;
            return name;
        }

        public string DescOf(int code)
        {
            if (code <= 0) return null;
            lock (_descs) { string v; if (_descs.TryGetValue(code, out v)) return v; }

            CdbCard cd = null;
            if (Cdb != null) Cdb.TryGet(code, out cd);

            if (cd != null && !string.IsNullOrEmpty(cd.Desc) && HasHangul(cd.Desc))
            { lock (_descs) _descs[code] = cd.Desc; return cd.Desc; }

            CardRec ko = ApiKo(code);
            if (ko != null && !string.IsNullOrEmpty(ko.Desc)) { lock (_descs) _descs[code] = ko.Desc; return ko.Desc; }

            if (cd != null && !string.IsNullOrEmpty(cd.Desc))
            { lock (_descs) _descs[code] = cd.Desc; return cd.Desc; }   // 영문 → DescKo 가 번역

            CardRec r = ApiRec(code);
            string d = (r != null) ? r.Desc : null;
            lock (_descs) _descs[code] = d;
            return d;
        }

        public int AtkOf(int code)
        {
            if (code <= 0) return -1;
            lock (_atk) { int v; if (_atk.TryGetValue(code, out v)) return v; }

            if (Cdb != null)
            {
                CdbCard cd;
                if (Cdb.TryGet(code, out cd) && cd.Atk >= 0)
                { lock (_atk) _atk[code] = cd.Atk; return cd.Atk; }
            }
            CardRec r = ApiRec(code);
            int a = (r != null) ? r.Atk : -1;
            lock (_atk) _atk[code] = a;
            return a;
        }

        /// <summary>효과 텍스트(한국어). 이미 한국어면 그대로, 영문이면 번역.</summary>
        public string DescKo(int code)
        {
            if (code <= 0) return null;
            lock (_trans) { string v; if (_trans.TryGetValue(code, out v)) return v; }

            string desc = DescOf(code);
            if (string.IsNullOrEmpty(desc)) { lock (_trans) _trans[code] = null; return null; }
            if (HasHangul(desc)) { lock (_trans) _trans[code] = desc; return desc; }
            if (!TranslateEnabled) { lock (_trans) _trans[code] = desc; return desc; }

            string key = Sha1(desc);
            string path = Path.Combine(TransDir, key + ".txt");
            try { if (File.Exists(path)) { string v = File.ReadAllText(path); lock (_trans) _trans[code] = v; return v; } } catch { }

            string tr = null;
            try
            {
                string url = "https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl=ko&dt=t&q="
                             + Uri.EscapeDataString(desc);
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Timeout = 8000;
                req.UserAgent = "ReplayVideoAndroid/1.0";
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var s = resp.GetResponseStream())
                using (var sr = new StreamReader(s, Encoding.UTF8))
                {
                    string json = sr.ReadToEnd();
                    var sb = new StringBuilder();
                    using (JsonDocument doc = JsonDocument.Parse(json))
                    {
                        JsonElement root = doc.RootElement;
                        if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 &&
                            root[0].ValueKind == JsonValueKind.Array)
                            foreach (JsonElement line in root[0].EnumerateArray())
                                if (line.ValueKind == JsonValueKind.Array && line.GetArrayLength() > 0 &&
                                    line[0].ValueKind == JsonValueKind.String)
                                    sb.Append(line[0].GetString());
                    }
                    tr = sb.Length > 0 ? sb.ToString() : null;
                }
            }
            catch { tr = null; }

            if (!string.IsNullOrEmpty(tr))
            {
                try { File.WriteAllText(path, tr, Encoding.UTF8); } catch { }
            }
            else tr = desc;
            lock (_trans) _trans[code] = tr;
            return tr;
        }

        // ── 이미지 ────────────────────────────────────────────
        public string LocalImagePath(int code)
        {
            if (string.IsNullOrEmpty(_pics) || code <= 0) return null;
            try
            {
                string p = Path.Combine(_pics, code + ".jpg");
                if (File.Exists(p)) return p;
            }
            catch { }
            return null;
        }

        public string CachedImagePath(int code)
        {
            try
            {
                string p = Path.Combine(ImgDir, code + ".jpg");
                if (File.Exists(p)) return p;
            }
            catch { }
            return null;
        }

        /// <summary>로컬 pics → 캐시 → (없으면) 다운로드.</summary>
        public string ImagePath(int code)
        {
            string p = LocalImagePath(code);
            if (p != null) return p;
            p = CachedImagePath(code);
            if (p != null) return p;
            if (!ApiEnabled) return null;
            DownloadImage(code);
            return CachedImagePath(code);
        }

        /// <summary>컷인용 — 내려받은 고해상도만(없으면 null → 렌더러가 일반 이미지로 폴백).</summary>
        public string HiImagePath(int code)
        {
            return CachedImagePath(code);
        }

        /// <summary>이미지 다운로드 — ① 같은 ID ② cdb alias(원본 ID) ③ cdb 영문 이름으로 YGOPRODeck 검색 ④ Yugipedia.
        ///   각 단계마다 cards → cards_small → cards_cropped 후보, 일시 오류 3회 재시도(404 는 다음 후보).</summary>
        public void DownloadImage(int code)
        {
            if (!ApiEnabled || code <= 0) return;
            if (CachedImagePath(code) != null) return;
            string dst = Path.Combine(ImgDir, code + ".jpg");

            CdbCard cd = null;
            if (Cdb != null) Cdb.TryGet(code, out cd);

            var ids = new List<int> { code };
            if (cd != null && cd.Alias > 0 && !ids.Contains(cd.Alias)) ids.Add(cd.Alias);
            foreach (int id in ids) if (DownloadById(dst, id)) return;

            string name = (cd != null && !string.IsNullOrEmpty(cd.Name) && !CardLibrary.HasHangul(cd.Name)) ? cd.Name : null;
            if (name != null)
            {
                int found = SearchYgoProDeckId(name);
                if (found > 0 && found != code && DownloadById(dst, found)) return;
                string url = SearchYugipediaImage(name);
                if (!string.IsNullOrEmpty(url) && DownloadUrl(dst, url)) return;
            }
            try { if (File.Exists(dst)) File.Delete(dst); } catch { }
        }

        private bool DownloadById(string dst, int id)
        {
            string[] urls =
            {
                "https://images.ygoprodeck.com/images/cards/" + id + ".jpg",
                "https://images.ygoprodeck.com/images/cards_small/" + id + ".jpg",
                "https://images.ygoprodeck.com/images/cards_cropped/" + id + ".jpg"
            };
            foreach (string u in urls) if (DownloadUrl(dst, u)) return true;
            return false;
        }

        /// <summary>URL 하나를 받아 저장(임시 파일 → 교체). 404 는 포기, 그 외 3회 재시도, HTML 오류 페이지 거름.</summary>
        private bool DownloadUrl(string dst, string url)
        {
            string tmp = dst + ".tmp";
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    var req = (HttpWebRequest)WebRequest.Create(url);
                    req.Timeout = 12000;
                    req.ReadWriteTimeout = 12000;
                    req.UserAgent = "ReplayVideoAndroid/1.1 (+https://github.com/Hellzgate/ReplayVideoApp)";
                    using (var resp = (HttpWebResponse)req.GetResponse())
                    using (var s = resp.GetResponseStream())
                    using (var fs = File.Create(tmp))
                        s.CopyTo(fs);
                    if (new FileInfo(tmp).Length < 512) throw new Exception("empty image");
                    try { if (File.Exists(dst)) File.Delete(dst); } catch { }
                    File.Move(tmp, dst);
                    return true;
                }
                catch (WebException wex)
                {
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                    var http = wex.Response as HttpWebResponse;
                    if (http != null && (int)http.StatusCode == 404) return false;
                    if (attempt < 2) System.Threading.Thread.Sleep(300 * (attempt + 1));
                }
                catch
                {
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                    if (attempt < 2) System.Threading.Thread.Sleep(300 * (attempt + 1));
                }
            }
            return false;
        }

        private static int SearchYgoProDeckId(string name)
        {
            try
            {
                string url = "https://db.ygoprodeck.com/api/v7/cardinfo.php?fname=" + Uri.EscapeDataString(name) + "&num=1&offset=0";
                string json = SimpleGet(url);
                if (string.IsNullOrEmpty(json)) return 0;
                var m = System.Text.RegularExpressions.Regex.Match(json, "\"id\"\\s*:\\s*(\\d+)");
                int id;
                return (m.Success && int.TryParse(m.Groups[1].Value, out id)) ? id : 0;
            }
            catch { return 0; }
        }

        private static string SearchYugipediaImage(string name)
        {
            try
            {
                string url = "https://yugipedia.com/api.php?action=query&titles=" + Uri.EscapeDataString(name)
                           + "&prop=pageimages&piprop=original&redirects=1&format=json";
                string json = SimpleGet(url);
                var m = System.Text.RegularExpressions.Regex.Match(json ?? "", "\"source\"\\s*:\\s*\"([^\"]+)\"");
                if (m.Success) return m.Groups[1].Value.Replace("\\/", "/");
                string url2 = "https://yugipedia.com/api.php?action=query&generator=search&gsrsearch=" + Uri.EscapeDataString(name)
                            + "&gsrlimit=1&prop=pageimages&piprop=original&format=json";
                json = SimpleGet(url2);
                m = System.Text.RegularExpressions.Regex.Match(json ?? "", "\"source\"\\s*:\\s*\"([^\"]+)\"");
                return m.Success ? m.Groups[1].Value.Replace("\\/", "/") : null;
            }
            catch { return null; }
        }

        private static string SimpleGet(string url)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Timeout = 12000;
                req.UserAgent = "ReplayVideoAndroid/1.1 (+https://github.com/Hellzgate/ReplayVideoApp)";
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var s = resp.GetResponseStream())
                using (var sr = new StreamReader(s, Encoding.UTF8))
                    return sr.ReadToEnd();
            }
            catch { return null; }
        }

        // ── API ───────────────────────────────────────────────
        private CardRec ApiKo(int code)
        {
            if (!ApiEnabled || code <= 0) return null;
            lock (_koMiss) if (_koMiss.Contains(code)) return null;
            string p = Path.Combine(CardsDir, code + ".ko.json");
            try
            {
                if (File.Exists(p))
                {
                    CardRec r = Parse(File.ReadAllText(p));
                    if (r != null && !string.IsNullOrEmpty(r.Name)) return r;
                }
            }
            catch { }
            try
            {
                string json = Get("https://db.ygoprodeck.com/api/v7/cardinfo.php?id=" + code + "&language=ko");
                CardRec rr = Parse(json);
                if (rr != null && !string.IsNullOrEmpty(rr.Name))
                {
                    try { File.WriteAllText(p, json, Encoding.UTF8); } catch { }
                    return rr;
                }
            }
            catch { }
            lock (_koMiss) _koMiss.Add(code);
            return null;
        }

        private CardRec ApiRec(int code)
        {
            if (!ApiEnabled || code <= 0) return null;
            lock (_api) { CardRec c; if (_api.TryGetValue(code, out c)) return c; }

            foreach (string lang in new[] { "ko", "en" })
            {
                string p = Path.Combine(CardsDir, code + "." + lang + ".json");
                try
                {
                    if (File.Exists(p))
                    {
                        CardRec r = Parse(File.ReadAllText(p));
                        if (r != null && !string.IsNullOrEmpty(r.Name)) { lock (_api) _api[code] = r; return r; }
                    }
                }
                catch { }
            }
            foreach (string lang in new[] { "ko", "" })
            {
                try
                {
                    string url = "https://db.ygoprodeck.com/api/v7/cardinfo.php?id=" + code
                                 + (lang.Length > 0 ? "&language=" + lang : "");
                    string json = Get(url);
                    CardRec r = Parse(json);
                    if (r != null && !string.IsNullOrEmpty(r.Name))
                    {
                        try
                        {
                            File.WriteAllText(Path.Combine(CardsDir, code + "." + (lang.Length > 0 ? lang : "en") + ".json"),
                                json, Encoding.UTF8);
                        }
                        catch { }
                        lock (_api) _api[code] = r;
                        return r;
                    }
                }
                catch { }
            }
            lock (_api) _api[code] = null;
            return null;
        }

        private static string Get(string url)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Timeout = 10000;
            req.UserAgent = "ReplayVideoAndroid/1.0";
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var s = resp.GetResponseStream())
            using (var sr = new StreamReader(s, Encoding.UTF8))
                return sr.ReadToEnd();
        }

        private static CardRec Parse(string json)
        {
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(json))
                {
                    JsonElement root = doc.RootElement;
                    JsonElement data;
                    if (!root.TryGetProperty("data", out data)) return null;
                    if (data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0) return null;
                    JsonElement c = data[0];
                    var rec = new CardRec();
                    JsonElement t;
                    rec.Name = c.TryGetProperty("name", out t) ? t.GetString() : null;
                    rec.Type = c.TryGetProperty("type", out t) ? t.GetString() : null;
                    rec.Desc = c.TryGetProperty("desc", out t) ? t.GetString() : null;
                    if (c.TryGetProperty("atk", out t) && t.ValueKind == JsonValueKind.Number) rec.Atk = t.GetInt32();
                    if (c.TryGetProperty("def", out t) && t.ValueKind == JsonValueKind.Number) rec.Def = t.GetInt32();
                    return rec;
                }
            }
            catch { return null; }
        }

        // ── 유틸 ──────────────────────────────────────────────
        public static bool HasHangul(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (char ch in s) if (ch >= 0xAC00 && ch <= 0xD7A3) return true;
            return false;
        }

        private static string Sha1(string s)
        {
            using (var sha = System.Security.Cryptography.SHA1.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(s));
                var sb = new StringBuilder();
                foreach (byte b in h) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
