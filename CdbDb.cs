// cdb 읽기(Android) — cdb 는 SQLite 파일이다. texts/datas 테이블에서 이름·설명·공격력을 읽는다.
// 여러 cdb 를 열고 **먼저 찾은 값**을 쓴다(한국어 레포를 앞에 두므로 한국어가 우선).
using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace ReplayVideoCore
{
    public sealed class CdbCard
    {
        public int Id;
        public string Name;
        public string Desc;
        public int Atk = -1;
        public int Def = -1;
    }

    public sealed class CdbDb
    {
        private readonly List<SqliteConnection> _conns = new List<SqliteConnection>();
        private readonly Dictionary<int, CdbCard> _cache = new Dictionary<int, CdbCard>();
        private readonly HashSet<int> _miss = new HashSet<int>();
        private readonly object _lock = new object();

        public int FileCount { get { return _conns.Count; } }
        public string Info { get; private set; }

        /// <summary>cdb 파일들을 읽기 전용으로 연다(최대 64개).</summary>
        public static CdbDb Open(IEnumerable<string> files)
        {
            var db = new CdbDb();
            int opened = 0, failed = 0;
            if (files != null)
            {
                foreach (string f in files)
                {
                    if (opened >= 64) break;
                    try
                    {
                        if (!File.Exists(f)) continue;
                        var cs = new SqliteConnectionStringBuilder
                        {
                            DataSource = f,
                            Mode = SqliteOpenMode.ReadOnly,
                            Cache = SqliteCacheMode.Shared
                        };
                        var conn = new SqliteConnection(cs.ToString());
                        conn.Open();
                        db._conns.Add(conn);
                        opened++;
                    }
                    catch { failed++; }
                }
            }
            db.Info = string.Format("cdb {0}개", opened) + (failed > 0 ? " (실패 " + failed + ")" : "");
            return db;
        }

        public void Close()
        {
            lock (_lock)
            {
                foreach (var c in _conns) { try { c.Dispose(); } catch { } }
                _conns.Clear();
                _cache.Clear();
                _miss.Clear();
            }
        }

        public bool TryGet(int id, out CdbCard card)
        {
            card = null;
            if (id <= 0) return false;
            lock (_lock)
            {
                if (_cache.TryGetValue(id, out card) && card != null) return true;
                if (_miss.Contains(id)) return false;
            }

            CdbCard found = null;
            lock (_lock)
            {
                foreach (var conn in _conns)
                {
                    try
                    {
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.CommandText =
                                "SELECT t.name, t.desc, d.atk, d.def FROM texts t " +
                                "LEFT JOIN datas d ON d.id = t.id WHERE t.id = $id LIMIT 1";
                            cmd.Parameters.AddWithValue("$id", id);
                            using (var rd = cmd.ExecuteReader())
                            {
                                if (rd.Read())
                                {
                                    var c = new CdbCard { Id = id };
                                    c.Name = rd.IsDBNull(0) ? null : rd.GetString(0);
                                    c.Desc = rd.IsDBNull(1) ? null : rd.GetString(1);
                                    c.Atk = rd.IsDBNull(2) ? -1 : rd.GetInt32(2);
                                    c.Def = rd.IsDBNull(3) ? -1 : rd.GetInt32(3);
                                    if (!string.IsNullOrEmpty(c.Name) || !string.IsNullOrEmpty(c.Desc)) { found = c; break; }
                                }
                            }
                        }
                    }
                    catch { }
                }
                if (found != null) _cache[id] = found; else _miss.Add(id);
            }
            card = found;
            return found != null;
        }

        /// <summary>cdb 안에 있는 카드 수(진단용).</summary>
        public int CountAll()
        {
            int n = 0;
            lock (_lock)
            {
                foreach (var conn in _conns)
                {
                    try
                    {
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = "SELECT COUNT(*) FROM texts";
                            object v = cmd.ExecuteScalar();
                            if (v != null) n += Convert.ToInt32(v);
                        }
                    }
                    catch { }
                }
            }
            return n;
        }
    }
}
