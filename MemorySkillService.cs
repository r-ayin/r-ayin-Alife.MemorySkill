using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Alife.Foundation;
using Alife.Framework;
using Alife.Function.FunctionCaller;

namespace RAyin.MemorySkill;

[Module("长期记忆检索",
    "为角色安装 memory 技能（Storage/Skills/memory）并自动维护索引：跨会话长期记忆检索，关键词 FTS + 可选 BGE 向量语义混合召回、时间衰减排序、只读角色自己的存档。\n启动时自动增量索引；检测到本机嵌入服务（http://127.0.0.1:18436/health）在线时自动附带 --vec 建向量，离线自动降级纯关键词。\nAI 函数：MemoryReindex（重建/增量索引）、MemorySearch（混合检索）。依赖系统 PATH 中的 python（3.8+，memory.py 纯标准库）。",
    defaultCategory: "第三方/记忆")]
public class MemorySkillService(
    XmlFunctionCaller functionService,
    Interactor<MemorySkillService> interactor) : ChatBehaviour
{
    readonly string skillDir = Path.Combine(AlifePath.StorageFolderPath, "Skills", "memory");
    readonly Task initTask = Task.Run(async () =>
    {
        try
        {
            await Task.Delay(3000);
            InitOnce();
        }
        catch (Exception) { }
    });

    void InitOnce()
    {
        DeployFiles();
        string? py = FindPython();
        if (py == null)
        {
            interactor.Poke("[MemorySkill] 未找到 python（PATH），跳过自动索引；技能文件已就位，装好 python 后调用 MemoryReindex 即可。");
            return;
        }
        string vecArg = EmbedOnline() ? " --vec" : "";
        var r = Run(py, "memory.py index" + vecArg);
        interactor.Poke($"[MemorySkill] 技能已安装，自动索引完成{(vecArg.Length > 0 ? "（含向量）" : "")}：{Trunc(r.stdout, 400)}");
    }

    void DeployFiles()
    {
        Directory.CreateDirectory(skillDir);
        Directory.CreateDirectory(Path.Combine(skillDir, "data"));
        WriteIfMissing("memory.py", MEMORY_PY);
        WriteIfMissing("SKILL.md", SKILL_MD);
        WriteIfMissing("DATA-BOUNDARY.md", DATA_BOUNDARY_MD);
    }

    void WriteIfMissing(string name, string content)
    {
        if (content.Length == 0) return;
        string p = Path.Combine(skillDir, name);
        if (File.Exists(p) == false || new FileInfo(p).Length == 0)
            File.WriteAllText(p, content, new UTF8Encoding(false));
    }

    static string? FindPython()
    {
        foreach (var cand in new[] { "python", "py -3" })
        {
            try
            {
                string exe = cand.Split(" ")[0];
                string pre = cand.Contains(" ") ? "-3 " : "";
                var psi = NewPsi(exe, pre + "--version", null);
                using var p = Process.Start(psi);
                if (p == null) continue;
                if (p.WaitForExit(8000) && p.ExitCode == 0) return cand;
            }
            catch (Exception) { }
        }
        return null;
    }

    static bool EmbedOnline()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var resp = http.GetAsync("http://127.0.0.1:18436/health").GetAwaiter().GetResult();
            return resp.IsSuccessStatusCode;
        }
        catch (Exception) { return false; }
    }

    static ProcessStartInfo NewPsi(string exe, string args, string? workdir)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (workdir != null) psi.WorkingDirectory = workdir;
        psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
        return psi;
    }

    (int code, string stdout, string stderr) Run(string python, string args)
    {
        string exe = python.Split(" ")[0];
        string pre = python.Contains(" ") ? "-3 " : "";
        var psi = NewPsi(exe, pre + args, skillDir);
        using var p = Process.Start(psi);
        if (p == null) return (-1, "", "process start failed");
        string so = p.StandardOutput.ReadToEnd();
        string se = p.StandardError.ReadToEnd();
        if (p.WaitForExit(300000) == false)
        {
            try { p.Kill(); } catch (Exception) { }
            return (-2, so, "timeout");
        }
        return (p.ExitCode, so, se);
    }

    static string Trunc(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + "…";

    [XmlFunction(FunctionMode.OneShot)]
    public void MemoryReindex(string rebuild = "false", string vec = "auto")
    {
        string? py = FindPython();
        if (py == null) { interactor.Poke("[MemorySkill] 未找到 python，无法索引"); return; }
        bool useVec = vec == "auto" ? EmbedOnline() : vec == "true";
        string args = "memory.py index" + (rebuild == "true" ? " --rebuild" : "") + (useVec ? " --vec" : "");
        var r = Run(py, args);
        string tail = r.stderr.Length > 0 ? "\nstderr: " + Trunc(r.stderr, 200) : "";
        interactor.Poke($"[MemorySkill] 索引完成(exit={r.code}, vec={useVec})：{Trunc(r.stdout, 500)}{tail}");
    }

    [XmlFunction(FunctionMode.OneShot)]
    public void MemorySearch(string query, string k = "5")
    {
        string? py = FindPython();
        if (py == null) { interactor.Poke("[MemorySkill] 未找到 python，无法检索"); return; }
        int kk = 5;
        int.TryParse(k, out kk);
        if (kk < 1 || kk > 20) kk = 5;
        var r = Run(py, "memory.py search " + Quote(query) + " --k " + kk.ToString());
        interactor.Poke($"[MemorySkill] 检索结果：\n{Trunc(r.stdout, 3000)}");
    }

    static string Quote(string s)
    {
        return "\u0022" + s.Replace("\u0022", "\u0022\u0022") + "\u0022";
    }

    const string MEMORY_PY = """"
#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Alife 记忆检索技能：混合召回（FTS5 trigram + 向量）+ RRF 融合 + 时间衰减。

纯标准库，无第三方依赖。向量腿不可用时自动降级为纯关键词。

用法:
  python memory.py root
  python memory.py index [--root PATH] [--rebuild] [--vec]
  python memory.py search "查询" [--k 5] [--root PATH]
  python memory.py read <id> [--root PATH]
  python memory.py stats [--root PATH]
  python memory.py annotate <id> "批注文字"        # 给某条存档挂批注
  python memory.py supersede <id> --by <新id> [--reason 原因]   # 登记「该结论已被推翻/更新」
  python memory.py notes <id>                      # 查看一条存档的修订账本

修订账本：批注与推翻关系记在索引库 mem_note 表里，绝不改角色存档原文。
被标记的存档每次在 search/read 中被调出时，警告自动随行显示。
"""
import argparse, json, math, os, re, sqlite3, struct, sys, urllib.request
from datetime import datetime
from pathlib import Path

# 库文件固定放在本技能目录的 data/ 下，不散落在 Alife 的角色目录里。
# 这样"这个技能 + 它的库"是一整包，拷走即用，删掉即净。
DEFAULT_DB = Path(__file__).resolve().parent / "data" / "alife-memory.db"
EMBED_URL = os.environ.get("ALIFE_EMBED_URL", "http://127.0.0.1:18436/v1/embeddings")
EMBED_MODEL = os.environ.get("ALIFE_EMBED_MODEL", "bge-small-zh-v1.5")
def _embed_token():
    t = os.environ.get("ALIFE_EMBED_TOKEN", "").strip()
    if t:
        return t
    for p in (r"E:\x-tool\vector-search\token.txt",):
        try:
            with open(p, encoding="utf-8") as f:
                t = f.read().strip()
            if t:
                return t
        except OSError:
            pass
    return ""
EMBED_TOKEN = _embed_token()
RRF_K = 60
RECENCY_WEIGHT = 0.3
RECENCY_HALF_LIFE_H = 720.0
FILE_RE = re.compile(r"^(\d+)-(\d{14})-(\d{14})\.txt$")        # 1-20261028014834-20261028022105.txt
FILE_RE_LABEL = re.compile(r"^(\d+)-(.+)\.txt$")                   # 6-事件-20261028首夜.txt

SCHEMA = """
CREATE TABLE IF NOT EXISTS memories(
  id TEXT PRIMARY KEY, level INTEGER, start_at TEXT, end_at TEXT,
  body TEXT, file TEXT, size INTEGER, indexed_at TEXT);
CREATE INDEX IF NOT EXISTS idx_mem_level ON memories(level);
CREATE INDEX IF NOT EXISTS idx_mem_time  ON memories(end_at);
CREATE VIRTUAL TABLE IF NOT EXISTS mem_fts USING fts5(
  id UNINDEXED, level UNINDEXED, body, tokenize='trigram');
CREATE TABLE IF NOT EXISTS mem_vec(mem_id TEXT PRIMARY KEY, dim INTEGER, vec BLOB);
CREATE TABLE IF NOT EXISTS mem_note(
  seq INTEGER PRIMARY KEY AUTOINCREMENT,
  mem_id TEXT, kind TEXT, note TEXT, ref_id TEXT, created_at TEXT);
CREATE INDEX IF NOT EXISTS idx_note_mem ON mem_note(mem_id);
CREATE TABLE IF NOT EXISTS kv(k TEXT PRIMARY KEY, v TEXT);
"""


def resolve_root(explicit=None):
    if explicit:
        p = Path(explicit).expanduser()
        # 传入角色目录时自动下钻到 Memory 子目录（scan_files 期望 root 即 Memory 目录）
        sub = p / "Memory"
        if sub.is_dir() and any(sub.glob("L*")):
            return sub
        return p
    env = os.environ.get("ALIFE_MEMORY_ROOT")
    if env:
        return Path(env).expanduser()
    for base in [Path.cwd(), Path(__file__).resolve().parent]:
        for cand in [base, *list(base.parents)[:6]]:
            if cand.name == "Memory" and any(cand.glob("L*")):
                return cand
            sub = cand / "Memory"
            if sub.is_dir() and any(sub.glob("L*")):
                return sub
    home = Path.home()
    for pat in ["AppData/Local/Alife", "AppData/Roaming/Alife", "Documents/Alife"]:
        for cand in home.glob(pat + "/**/Memory"):
            if any(cand.glob("L*")):
                return cand
    return Path.cwd() / "Memory"


def db_file(explicit=None):
    """库文件路径：--db 优先，否则固定在本技能 data/ 下。"""
    p = Path(explicit).expanduser() if explicit else DEFAULT_DB
    p.parent.mkdir(parents=True, exist_ok=True)
    return p


def connect_db(explicit=None):
    con = sqlite3.connect(str(db_file(explicit)))
    con.row_factory = sqlite3.Row
    con.executescript(SCHEMA)
    return con


def parse_entry(level, path):
    """解析一个存档文件，返回 (level, start_at, end_at, mid) 或 None。

    Alife 真实命名有两种（目录 L1..L8，文件首段数字等于层级）：
      1-20261028014834-20261028022105.txt  起止时间戳
      6-事件-20261028首夜.txt              语义标签
    时间戳统一成 %Y%m%d%H%M%S，供 hours_since 做时间衰减。
    """
    name, stem = path.name, path.stem
    m = FILE_RE.match(name)
    if m and int(m.group(1)) == level:
        return int(m.group(1)), m.group(2), m.group(3), f"L{level}-{stem}"
    m = FILE_RE_LABEL.match(name)
    if not m or int(m.group(1)) != level:
        return None
    label = m.group(2)
    ts = None
    d14 = re.search(r"(\d{14})", label)
    if d14:
        ts = d14.group(1)
    else:
        d8 = re.search(r"(\d{8})", label)
        if d8:
            ts = d8.group(1) + "000000"
    if ts is None:
        try:
            ts = datetime.fromtimestamp(path.stat().st_mtime).strftime("%Y%m%d%H%M%S")
        except OSError:
            ts = "19700101000000"
    return int(m.group(1)), ts, ts, f"L{level}-{stem}"


def scan_files(root):
    out = []
    for i in range(1, 9):
        d = root / f"L{i}"
        if not d.is_dir():
            continue
        for p in sorted(d.glob("*.txt")):
            parsed = parse_entry(i, p)
            if parsed:
                out.append((parsed[0], parsed[1], parsed[2], p, parsed[3]))
    return out


def do_index(args):
    root = resolve_root(args.root)
    con = connect_db(args.db)
    if args.rebuild:
        con.executescript("DROP TABLE IF EXISTS mem_fts; DROP TABLE IF EXISTS memories;")
        con.executescript(SCHEMA)
        con.commit()
    files = scan_files(root)
    known = {r[0]: r[1] for r in con.execute("SELECT id, size FROM memories")}
    added = updated = 0
    for level, start_at, end_at, path, mid in files:
        try:
            body = path.read_text(encoding="utf-8", errors="replace")
        except OSError:
            continue
        size = len(body.encode("utf-8"))
        if mid in known and known[mid] == size:
            continue                      # 未变化，跳过
        is_update = mid in known
        con.execute(
            "INSERT OR REPLACE INTO memories VALUES(?,?,?,?,?,?,?,?)",
            (mid, level, start_at, end_at, body, str(path), size,
             datetime.now().isoformat(timespec="seconds")))
        con.execute("DELETE FROM mem_fts WHERE id=?", (mid,))
        con.execute("INSERT INTO mem_fts(id, level, body) VALUES(?,?,?)", (mid, level, body))
        con.execute("DELETE FROM mem_vec WHERE mem_id=?", (mid,))   # 正文变了，旧向量作废
        if is_update:
            updated += 1
        else:
            added += 1
    con.commit()
    vec_done = 0
    if args.vec:
        vec_done = build_vectors(con)
    total = con.execute("SELECT COUNT(*) FROM memories").fetchone()[0]
    print(json.dumps({"root": str(root), "scanned": len(files), "added": added,
                      "updated": updated, "vectors_built": vec_done, "total": total},
                     ensure_ascii=False))


def embed(text, timeout=8, kind="document"):
    payload = json.dumps({"input": [text], "model": EMBED_MODEL, "kind": kind}).encode()
    req = urllib.request.Request(EMBED_URL, data=payload,
                                 headers={"Content-Type": "application/json", **({"Authorization": "Bearer " + EMBED_TOKEN} if EMBED_TOKEN else {})})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        data = json.loads(r.read())
    return list(data["data"][0]["embedding"])


def build_vectors(con, limit=500):
    rows = con.execute(
        "SELECT m.id, m.body FROM memories m LEFT JOIN mem_vec v ON v.mem_id=m.id "
        "WHERE v.mem_id IS NULL LIMIT ?", (limit,)).fetchall()
    done = 0
    for r in rows:
        try:
            v = embed(r["body"][:2000])
        except Exception as e:
            print(f"[warn] embed failed {r['id']}: {e}", file=sys.stderr)
            break
        con.execute("INSERT OR REPLACE INTO mem_vec VALUES(?,?,?)",
                    (r["id"], len(v), struct.pack(f"{len(v)}f", *v)))
        done += 1
    con.commit()
    return done


def fts_terms(q):
    words = [w for w in re.split(r"\s+", q.strip()) if len(w) >= 3]
    return " OR ".join('"' + w.replace('"', '""') + '"' for w in words) if words else None


def like_terms(q):
    """中文切 2-gram，英文按长度>=2 保留，去重保序。用于 FTS 不足时的宽松保底。"""
    out = []
    for seg in re.split(r"[\s\W_]+", q):
        if not seg:
            continue
        if re.search(r"[\u4e00-\u9fff]", seg):
            if len(seg) == 1:
                out.append(seg)                      # 单字中文查询也要能命中
            else:
                out.extend(seg[i:i + 2] for i in range(len(seg) - 1))
        elif len(seg) >= 2:
            out.append(seg)
    return list(dict.fromkeys(out))


def like_search(con, q, k):
    """多关键词 LIKE OR，按命中词数排序。中文无空格时提供召回保底。"""
    terms = like_terms(q)
    if not terms:
        return []
    hits = " + ".join(["CASE WHEN body LIKE ? THEN 1 ELSE 0 END"] * len(terms))
    where = " OR ".join(["body LIKE ?"] * len(terms))
    params = [f"%{t}%" for t in terms] + [f"%{t}%" for t in terms] + [k]
    try:
        return [r["id"] for r in con.execute(
            f"SELECT id FROM memories WHERE {where} ORDER BY ({hits}) DESC LIMIT ?", params)]
    except sqlite3.OperationalError:
        return []


def fts_search(con, q, k):
    """两阶段：先 trigram 精确短语（高精度），不足 k 条再用关键词 OR 补足（高召回）。"""
    ids = []
    expr = fts_terms(q)
    if expr:
        try:
            ids = [r["id"] for r in con.execute(
                "SELECT id FROM mem_fts WHERE mem_fts MATCH ? ORDER BY bm25(mem_fts) LIMIT ?",
                (expr, k))]
        except sqlite3.OperationalError:
            ids = []
    if len(ids) < k:
        seen = set(ids)
        for mid in like_search(con, q, k):
            if mid not in seen:
                ids.append(mid)
                seen.add(mid)
    return ids[:k]


def vec_search(con, q, k):
    try:
        qv = embed(q, kind="query")
    except Exception:
        return []
    rows = con.execute("SELECT mem_id, dim, vec FROM mem_vec").fetchall()
    if not rows:
        return []
    nq = math.sqrt(sum(x * x for x in qv)) or 1.0
    scored = []
    for r in rows:
        v = struct.unpack(f"{r['dim']}f", r["vec"])
        if len(v) != len(qv):
            continue
        nv = math.sqrt(sum(x * x for x in v)) or 1.0
        scored.append((sum(a * b for a, b in zip(qv, v)) / (nq * nv), r["mem_id"]))
    scored.sort(reverse=True)
    return [mid for s, mid in scored[:k] if s > 0]


def rrf(lists, k=RRF_K):
    acc = {}
    for lst in lists:
        for rank, mid in enumerate(lst, 1):
            acc[mid] = acc.get(mid, 0.0) + 1.0 / (k + rank)
    return acc


def hours_since(ts):
    try:
        return max(0.0, (datetime.now() - datetime.strptime(ts, "%Y%m%d%H%M%S")).total_seconds() / 3600)
    except ValueError:
        return 0.0


def do_search(args):
    con = connect_db(args.db)
    q = args.query.strip()
    if not q:
        print(json.dumps([]))
        return
    legs, sources = [], {}
    f_ids = fts_search(con, q, 50)
    if f_ids:
        legs.append(f_ids)
        for i in f_ids:
            sources.setdefault(i, []).append("fts")
    v_ids = vec_search(con, q, 50)
    if v_ids:
        legs.append(v_ids)
        for i in v_ids:
            sources.setdefault(i, []).append("vec")
    fused = rrf(legs)
    out = []
    for mid, base in fused.items():
        row = con.execute("SELECT * FROM memories WHERE id=?", (mid,)).fetchone()
        if row is None:
            continue
        decay = 1.0 + RECENCY_WEIGHT * math.exp(-hours_since(row["end_at"]) / RECENCY_HALF_LIFE_H)
        out.append({
            "id": mid, "level": row["level"],
            "start_at": row["start_at"], "end_at": row["end_at"],
            "score": round(base * decay, 6), "sources": sources.get(mid, []),
            "snippet": row["body"][:200].replace("\n", " "),
            "file": row["file"],
        })
    out.sort(key=lambda x: -x["score"])
    out = out[:args.k]
    for x in out:
        warns, notes, supersedes = ledger_for(con, x["id"])
        if warns:
            x["warn"] = "⚠ 已过期：" + "；".join(warns)
        if supersedes:
            x["supersedes"] = supersedes
        if notes:
            x["notes"] = notes
    if len(out) > 1:
        hi, lo = out[0]["score"], out[-1]["score"]
        span = hi - lo
        for x in out:
            x["rel"] = round(1.0 if span <= 0 else 1.0 - (hi - x["score"]) / span, 3)
    elif out:
        out[0]["rel"] = 1.0
    print(json.dumps(out, ensure_ascii=False, indent=2))


def ledger_for(con, mid):
    """一条存档的修订账本：(推翻警告, 批注, 本条推翻了谁)。"""
    rows = con.execute(
        "SELECT kind, note, ref_id, created_at FROM mem_note WHERE mem_id=? ORDER BY seq",
        (mid,)).fetchall()
    back = con.execute(
        "SELECT mem_id, created_at FROM mem_note WHERE ref_id=? AND kind='supersede' ORDER BY seq",
        (mid,)).fetchall()
    warns = [f"已被 {r['ref_id']} 推翻/更新（{r['created_at'][:10]}）"
             + (f"：{r['note']}" if r["note"] else "")
             for r in rows if r["kind"] == "supersede"]
    notes = [f"（{r['created_at'][:10]}）{r['note']}" for r in rows if r["kind"] == "note"]
    supersedes = [f"{r['mem_id']}（{r['created_at'][:10]}）" for r in back]
    return warns, notes, supersedes


def do_annotate(args):
    con = connect_db(args.db)
    if not con.execute("SELECT 1 FROM memories WHERE id=?", (args.id,)).fetchone():
        print(f"未找到 {args.id}", file=sys.stderr)
        sys.exit(2)
    con.execute("INSERT INTO mem_note(mem_id, kind, note, ref_id, created_at) VALUES(?,?,?,?,?)",
                (args.id, "note", args.text, None,
                 datetime.now().isoformat(timespec="seconds")))
    con.commit()
    print(json.dumps({"annotated": args.id, "note": args.text}, ensure_ascii=False))


def do_supersede(args):
    con = connect_db(args.db)
    if not con.execute("SELECT 1 FROM memories WHERE id=?", (args.id,)).fetchone():
        print(f"未找到 {args.id}", file=sys.stderr)
        sys.exit(2)
    if not con.execute("SELECT 1 FROM memories WHERE id=?", (args.by,)).fetchone():
        print(f"[warn] {args.by} 尚不在索引中（先跑 index 收录它）；关系照记", file=sys.stderr)
    con.execute("INSERT INTO mem_note(mem_id, kind, note, ref_id, created_at) VALUES(?,?,?,?,?)",
                (args.id, "supersede", args.reason or "", args.by,
                 datetime.now().isoformat(timespec="seconds")))
    con.commit()
    print(json.dumps({"superseded": args.id, "by": args.by,
                      "reason": args.reason or ""}, ensure_ascii=False))


def do_notes(args):
    con = connect_db(args.db)
    warns, notes, supersedes = ledger_for(con, args.id)
    print(json.dumps({"id": args.id, "warns": warns, "notes": notes,
                      "supersedes": supersedes}, ensure_ascii=False, indent=2))


def do_read(args):
    con = connect_db(args.db)
    row = con.execute("SELECT body, file FROM memories WHERE id=?", (args.id,)).fetchone()
    if row is None:
        print(f"未找到 {args.id}", file=sys.stderr)
        sys.exit(2)
    warns, notes, supersedes = ledger_for(con, args.id)
    if warns:
        print("⚠⚠ 修订警告：本条结论已过期，引用前必看 ⚠⚠")
        for w in warns:
            print("  - " + w)
    if supersedes:
        print("ℹ 本条推翻/更新了：" + "、".join(supersedes))
    if notes:
        print("批注：")
        for n in notes:
            print("  - " + n)
    if warns or notes or supersedes:
        print("-" * 46)
    print(row["body"])


def do_stats(args):
    con = connect_db(args.db)
    total = con.execute("SELECT COUNT(*) FROM memories").fetchone()[0]
    vecs = con.execute("SELECT COUNT(*) FROM mem_vec").fetchone()[0]
    levels = {str(r[0]): r[1] for r in con.execute(
        "SELECT level, COUNT(*) FROM memories GROUP BY level ORDER BY level")}
    span = con.execute("SELECT MIN(start_at), MAX(end_at) FROM memories").fetchone()
    print(json.dumps({"db": str(db_file(args.db)), "total": total, "vectors": vecs,
                      "vector_coverage": round(vecs / total, 3) if total else 0,
                      "by_level": levels,
                      "time_span": [span[0], span[1]] if span and span[0] else None},
                     ensure_ascii=False, indent=2))


# 不该出现在角色记忆里的东西：别的项目、账号凭据、个人身份信息
SENSITIVE_PATTERNS = [
    ("个人身份", r"1[3-9]\\d{9}|[\\w.+-]+@[\\w-]+\\.[\\w.]{2,}|\\d{17}[\\dXx]"),
    ("凭据词", r"(?i)(password|passwd|token|api[_-]?key|secret|密码|密钥|口令|凭据)"),
    ("别的项目", r"(?i)(upwork|boss直聘|二姐|锴标|调车|dobestmusic|视频侦探|evo-trace|tailscale|cloudflare|钉钉)"),
]


def do_audit(args):
    """逐条列出库内容并标注可疑项。这是"这库干不干净"的唯一判据。"""
    con = connect_db(args.db)
    rows = con.execute(
        "SELECT id, level, start_at, end_at, body FROM memories ORDER BY id").fetchall()
    print(f"库文件：{db_file(args.db)}")
    print(f"条数：{len(rows)}")
    if not rows:
        print("（空库 —— 干净，等灌入角色存档）")
        return
    span = con.execute("SELECT MIN(start_at), MAX(end_at) FROM memories").fetchone()
    print(f"时间跨度：{span[0]} ~ {span[1]}")
    print("")
    flagged = 0
    for r in rows:
        hits = []
        for name, pat in SENSITIVE_PATTERNS:
            m = re.search(pat, r["body"] or "")
            if m:
                hits.append(f"{name}={m.group(0)[:24]}")
        mark = "  !! " if hits else "     "
        if hits:
            flagged += 1
        preview = (r["body"] or "")[:46].replace("\n", " ")
        print(f"{mark}{r['id']}  {preview}")
        if hits:
            print(f"        命中 → {' / '.join(hits)}")
    print(f"可疑条目：{flagged} / {len(rows)}")
    if flagged:
        print("确认不该留的，用 drop 子命令删掉：python memory.py drop <id>")


def do_drop(args):
    con = connect_db(args.db)
    if not con.execute("SELECT 1 FROM memories WHERE id=?", (args.id,)).fetchone():
        print(f"未找到 {args.id}", file=sys.stderr)
        sys.exit(2)
    con.execute("DELETE FROM memories WHERE id=?", (args.id,))
    con.execute("DELETE FROM mem_fts WHERE id=?", (args.id,))
    con.execute("DELETE FROM mem_vec WHERE mem_id=?", (args.id,))
    con.commit()
    print(json.dumps({"dropped": args.id}, ensure_ascii=False))


def main():
    ap = argparse.ArgumentParser(description="Alife 记忆检索（只读角色自己的存档）")
    sub = ap.add_subparsers(dest="cmd", required=True)
    sub.add_parser("root")
    p = sub.add_parser("index"); p.add_argument("--root"); p.add_argument("--db"); p.add_argument("--rebuild", action="store_true"); p.add_argument("--vec", action="store_true")
    p = sub.add_parser("search"); p.add_argument("query"); p.add_argument("--k", type=int, default=5); p.add_argument("--root"); p.add_argument("--db")
    p = sub.add_parser("read"); p.add_argument("id"); p.add_argument("--db")
    p = sub.add_parser("stats"); p.add_argument("--db")
    p = sub.add_parser("audit"); p.add_argument("--db")
    p = sub.add_parser("drop"); p.add_argument("id"); p.add_argument("--db")
    p = sub.add_parser("annotate"); p.add_argument("id"); p.add_argument("text"); p.add_argument("--db")
    p = sub.add_parser("supersede"); p.add_argument("id"); p.add_argument("--by", required=True); p.add_argument("--reason"); p.add_argument("--db")
    p = sub.add_parser("notes"); p.add_argument("id"); p.add_argument("--db")
    args = ap.parse_args()
    if args.cmd == "root":
        print(resolve_root(None))
    elif args.cmd == "index":
        do_index(args)
    elif args.cmd == "search":
        do_search(args)
    elif args.cmd == "read":
        do_read(args)
    elif args.cmd == "stats":
        do_stats(args)
    elif args.cmd == "audit":
        do_audit(args)
    elif args.cmd == "drop":
        do_drop(args)
    elif args.cmd == "annotate":
        do_annotate(args)
    elif args.cmd == "supersede":
        do_supersede(args)
    elif args.cmd == "notes":
        do_notes(args)


if __name__ == "__main__":
    main()
    """";

    const string SKILL_MD = """"
---
name: memory
description: 长期记忆检索。当用户提到过去发生过的事、需要回忆某人某事、或你想确认"之前是不是聊过这个"时使用。支持关键词+语义混合召回，也支持按索引精读全文。
---

# 长期记忆检索

Alife 的记忆存档是一堆文件，躺在你自己角色的 Memory 目录里。这个技能让你按**意思**去找它们，而不是靠猜索引号。

## 什么时候用

- 用户说「上次」「之前」「你还记得吗」「我们聊过」
- 话题里出现一个你觉得自己应该有印象的人名/项目名/事件
- 你准备下结论，但不确定这是不是以前踩过的坑
- 用户明显在延续一个你不记得上下文的话题

**不要用**的情况：用户刚说过的事（还在上下文里）、纯知识问答、当前对话里的信息足够。

## 怎么用

所有命令都在本技能目录下执行。`<ROOT>` 是你角色的记忆存档根目录
（形如 `C:/Users/xxx/AppData/Local/Alife/Characters/xxx/Memory`），
如不确定，先跑 `python memory.py root` 让它自己猜。

### 1. 混合召回（最常用）

```bash
python memory.py search "上次说的那个显卡驱动问题" --k 5
```

返回 JSON 数组，每项含：

| 字段 | 含义 |
|---|---|
| `id` | 存档索引，如 `L2-20261025164300-20261025171245` |
| `level` | 层级，1 最细、8 最粗 |
| `start_at` / `end_at` | 这段时间的起止 |
| `score` | 融合分（原始值，只在同一查询内可比） |
| `rel` | 相对分，1.0 = 本次最佳，0.0 = 本次最差；**判断相关性主要看它** |
| `sources` | 哪几路命中的：`["fts"]` / `["vec"]` / 两者都有 |
| `snippet` | 摘要片段（约 200 字） |
| `file` | 全文文件路径 |

### 2. 精读全文

摘要不够时，按 id 读原文：

```bash
python memory.py read L2-20261025164300-20261025171245
```

会打印完整存档内容（可能几千字）。**只在摘要明显相关时才读**，省上下文。

### 3. 建索引 / 看状态

```bash
python memory.py index          # 增量索引新增的存档（每次启动跑一次即可）
python memory.py index --rebuild # 全部重扫
python memory.py stats          # 条数、层级分布、向量覆盖率
```

### 4. 体检：这个库干不干净

```bash
python memory.py audit          # 逐条列出，并标出可疑内容
```

「可疑」= 命中三类模式：

| 类别 | 抓什么 | 为什么要抓 |
|---|---|---|
| 个人身份 | 手机号、邮箱、身份证号 | 角色不需要知道主人的证件 |
| 凭据词 | password / token / 密码 / 密钥 | 更不能知道 |
| 别的项目 | upwork、BOSS、二姐、锴标、DoBestMusic 等 | 角色只该记得跟主人的对话，不该有开发者的工作记忆 |

确认某条确实不该留：

```bash
python memory.py drop <id>
```

这条命令**只删索引里的条目**，不动磁盘上的 .txt 原档。要彻底删对话记忆，用 Alife 自带的 `Forget`。

### 5. 修订账本（防止过期结论冒充现行）

存档是快照，不会自我覆盖：一条 L8 结论后来被推翻，原文永远停在旧版本。
账本机制把「推翻关系」记在索引库里（**绝不改存档原文**），被标记的条目每次
在 search/read 中被调出时，警告自动随行：

```bash
# 结论被新档推翻时登记（旧档 id + 新档 id + 原因）
python memory.py supersede <旧id> --by <新id> --reason "哪里被推翻了"

# 给任何一条挂普通批注（补充语境、提醒别当结论用）
python memory.py annotate <id> "批注文字"

# 查一条的完整账本
python memory.py notes <id>
```

效果：search 结果里该条目多出 `warn` 字段（⚠ 已过期：已被 xxx 推翻/更新）；
read 正文前印醒目警告头；新档一侧自动显示「本条推翻了哪些旧档」（反向链接）。

**纪律：发现自己引用过的结论被新证据推翻时，当场 supersede，别指望下次记得。**
另外 index 现在会检测存档正文变化（角色自己改过文件的情况），重跑即刷新索引与向量。

## 判断召回结果好不好的三条

0. 有 `warn` 字段的条目：**结论已过期**，先看推翻它的新档再决定引不引用
1. `sources` 只有 `fts`：只是字面命中，**语义可能不准**，要谨慎引用
2. `rel` 第一名 1.0、第二名 ≤0.3：第一名可信；如果都是 1.0/0.9/0.8 挤在一起，说明几条都差不多相关，要读 snippet 分辨
3. 前五名 `level` 全是 7-8（最粗层）：只有高层摘要相关，细节要靠 `read` 展开
4. 一条都没搜到：换更短的词再试（中文单字也能搜），或直接用 `stats` 确认索引里到底有没有东西

## 注意事项

- 这个技能**只读**，不改你的存档。要写记忆请用 Alife 自带的 `Memorize`。
- 向量检索需要本地嵌入服务在线；不在线时自动降级为纯关键词，结果里 `sources` 不会出现 `vec`。
- 索引库固定在**本技能目录**的 `data/alife-memory.db`，不散落在角色目录里；删掉它再跑 `index` 就能重建，不丢原始存档。
- 这个库**只装当前角色自己的对话存档**。数据边界详见同目录 `DATA-BOUNDARY.md`。

## 向量检索依赖

语义（向量）检索腿依赖本机嵌入服务 vector-search（BGE bge-small-zh-v1.5，512 维，OpenAI /v1/embeddings 兼容，端口 18436，仅绑 127.0.0.1，Bearer token 在 E:\x-tool\vector-search\token.txt，memory.py 会自动读取）。服务未启动时自动降级为纯关键词（FTS）检索，不影响可用性；启动脚本：E:\x-tool\vector-search\start-vector-search.ps1（幂等）。索引时加 --vec 建向量；辅助脚本 run_vec.py 会自动拉起服务→建向量→验证。
    """";

    const string DATA_BOUNDARY_MD = """"
# 数据边界：这个库里装什么，不装什么

一句话：**只装露露自己和你聊过的东西，不装我（开发助手）的工作记忆。**

## 进来的是什么

| 来源 | 具体路径 | 内容 |
|---|---|---|
| 露露自己的记忆存档 | `{Alife 存储根}/Characters/{角色}/Memory/L1~L8/L*.txt` | 她和你的对话摘要，按 1~8 层金字塔分级 |

扫描规则写死在代码里：**只认 `L1`~`L8` 这八个目录，只认 `L<数字>-<数字>-<数字>.txt` 这个文件名格式**。别的东西一律忽略，哪怕目录里混进了相册或日志。

## 不进来的是什么

这些库我都查过，**一个都不接**：

| 库 | 大小 | 条数 | 为什么不接 |
|---|---|---|---|
| `/root/.dsh/memory/memory.db` | 260 MB | 10602 条（6467 活跃 + 4135 归档） | 混着 Upwork 投递 1012 条、BOSS 直聘、二姐调车 436 条、evo 审计 1919 条、家宽网络配置、账号凭据路径 |
| `/root/memhome/memory.db` | 918 MB | 73.8 万条 | 微信聊天全量，跟这个项目无关 |
| `/root/memhome/people.db` | 376 KB | — | 人物关系库 |
| `/root/memhome/moments.db` | 776 KB | — | 时刻记忆 |
| `/root/memhome/voice_cache.db` | 780 KB | — | 语音缓存 |
| `/root/memhome/cockpit.db` | 152 KB | — | 工作驾驶舱 |

那 10602 条里，**跟 Alife 有关的只有 20 条**（就是这几天我们一起做这个项目时记的）。剩下 10582 条是别的工作。如果接进来，露露会知道你在投什么简历、有哪些客户、服务器怎么配的——这不该是一个桌宠该知道的事。

## 三道锁

1. **物理隔离**：库文件固定在技能自己的目录 `data/alife-memory.db`，不在 `/root/.dsh`、不在 `/root/memhome`、不在角色目录。这个技能是全套自带的，拷走就是一个独立世界。
2. **路径白名单**：扫描只认 `L[1-8]/L*-*.txt`，代码里写死，不给"顺手扫到别的目录"留口子。
3. **体检命令**：`python memory.py audit` 会逐条列出库内容，并标出三类可疑项——个人身份（手机号/邮箱/身份证）、凭据词（password/token/密码/密钥）、别的项目（upwork/BOSS/二姐/锴标/DoBestMusic 等）。

## 怎么验

```bash
cd /root/dsh\ coding/alife-qq/skills/memory

# 看库里有什么、干不干净
python memory.py audit

# 看规模和层级分布
python memory.py stats
```

实测样例（我造了三条假数据，其中一条故意含 upwork + 手机号 + 密码）：

```
     L2-20261020100000-20261020113000  今天和主人聊了桌宠的动画……
  !! L2-20261021140000-20261021153000  主人让我帮他查 upwork 的提案怎么写，还给了手机号 13812345678 和密码 a
        命中 → 凭据词=密码 / 别的项目=upwork
可疑条目：1 / 3
```

## 发现污染怎么办

```bash
python memory.py drop <id>     # 只删索引条目，不动磁盘 .txt
```

要连磁盘上的对话存档一起删，用 Alife 自带的 `Forget`——那才是真正的原始数据。

## 一个诚实的前提

这套边界保证的是：**这个技能不会主动去读别的库**。

它不能保证的是：如果你（或 Alife 本体）把别处的文本手动拷进了 `L1~L8` 目录，那些文本会被索引——因为在文件层面它和正常存档长得一样。这种情况只能靠 `audit` 发现、靠 `drop` 清掉。

定期跑一次 `audit` 就够了。
    """";

}
