using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;

// mailbird-cli — one tool for the local Mailbird:
//   * compose : open a pre-filled draft for human review (mailto: handoff; never sends)
//   * read/search : READ-ONLY queries over Mailbird's Store.db (never writes)
internal static class Program
{
    static readonly string DefaultDb = ResolveDefaultDb();

    // Resolve the Store.db at runtime so the tool is portable: honor MAILBIRD_STORE_DB if set,
    // otherwise the standard per-user location (%LOCALAPPDATA%\Mailbird\Store\Store.db).
    static string ResolveDefaultDb()
    {
        var env = Environment.GetEnvironmentVariable("MAILBIRD_STORE_DB");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Mailbird", "Store", "Store.db");
    }

    static bool Json;   // when set, tabular modes emit a JSON array instead of pipe-delimited text

    static int Main(string[] argv)
    {
        Json = argv.Contains("--json");
        // Allow an optional leading db path; otherwise use the default.
        string db = DefaultDb;
        var a = new List<string>(argv);
        if (a.Count > 0 && (File.Exists(a[0]) || a[0].EndsWith(".db", StringComparison.OrdinalIgnoreCase)))
        {
            db = a[0]; a.RemoveAt(0);
        }
        if (a.Count == 0) { Usage(); return 1; }

        string mode = a[0].ToLowerInvariant();
        var rest = a.Skip(1).ToList();
        var opt = ParseOpts(rest, out var pos);

        // compose needs no database — handle it before opening Store.db.
        if (mode == "compose") return Compose(opt, pos);
        if (mode is "-h" or "--help" or "help") { Usage(); return 0; }

        if (!File.Exists(db)) { Console.Error.WriteLine($"DB not found: {db}"); return 2; }
        string cs = new SqliteConnectionStringBuilder
        { DataSource = db, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Private }.ToString();

        using var con = new SqliteConnection(cs);
        con.Open();

        switch (mode)
        {
            case "accounts":
                Query(con, @"SELECT a.Id, a.Username,
                                    (SELECT COUNT(*) FROM Folders f WHERE f.AccountId=a.Id) AS folders
                             FROM Accounts a ORDER BY a.Id");
                break;

            case "folders":
            {
                string where = pos.Count > 0 ? "WHERE f.AccountId=$acct" : "";
                var p = new Dictionary<string, object>();
                if (pos.Count > 0) p["$acct"] = int.Parse(pos[0]);
                Query(con, $@"SELECT f.Id, f.AccountId, f.Name,
                                     (SELECT COUNT(*) FROM Folders_Messages fm WHERE fm.FolderId=f.Id) AS msgs
                              FROM Folders f {where} ORDER BY f.AccountId, f.Name", p);
                break;
            }

            case "search":
            {
                if (pos.Count == 0) { Console.Error.WriteLine("search needs a query"); return 1; }
                int limit = OptInt(opt, "limit", 25);
                // Default: quote each token so punctuation (-, @, ., etc.) can't break FTS5 parsing.
                // --raw passes the query verbatim for power-user FTS syntax (col:term, OR, prefix*).
                string q = opt.ContainsKey("raw") ? string.Join(" ", pos) : BuildFtsQuery(pos);
                var p = new Dictionary<string, object> { ["$q"] = q };
                string acct = "";
                if (opt.TryGetValue("account", out var ac)) { acct = "AND m.AccountId=$acct"; p["$acct"] = int.Parse(ac); }
                p["$lim"] = limit;
                try
                {
                    Query(con, $@"SELECT m.Id, m.ReceivedAt_UTC AS date, m.IsRead AS rd,
                                         trim(ff.From_) AS sender, trim(ff.Subject) AS subject,
                                         snippet(FTS_Messages, 1, '«', '»', '…', 6) AS match
                                  FROM FTS_Messages ff JOIN Messages m ON m.Id=ff.rowid
                                  WHERE FTS_Messages MATCH $q {acct}
                                  ORDER BY rank LIMIT $lim", p);
                }
                catch (SqliteException ex)
                {
                    Console.Error.WriteLine($"FTS query error: {ex.Message}");
                    Console.Error.WriteLine("Tip: use plain words, \"a phrase\", col:term (e.g. Subject:invoice), AND/OR/NOT.");
                    return 3;
                }
                break;
            }

            case "list":
            {
                int limit = OptInt(opt, "limit", 25);
                var p = new Dictionary<string, object> { ["$lim"] = limit };
                var w = new List<string>();
                string folderJoin = "";
                if (opt.TryGetValue("folder", out var fol))
                {
                    folderJoin = "JOIN Folders_Messages fm ON fm.MessageId=m.Id JOIN Folders fo ON fo.Id=fm.FolderId";
                    w.Add("fo.Name=$folder"); p["$folder"] = fol;
                }
                if (opt.TryGetValue("account", out var ac)) { w.Add("m.AccountId=$acct"); p["$acct"] = int.Parse(ac); }
                if (opt.ContainsKey("unread")) w.Add("m.IsRead=0");
                if (opt.TryGetValue("from", out var fr)) { w.Add("ff.From_ LIKE $from"); p["$from"] = "%" + fr + "%"; }
                if (opt.TryGetValue("days", out var dv))
                {
                    int days = int.Parse(dv);
                    w.Add("m.ReceivedAt_UTC >= $since");
                    p["$since"] = DateTime.UtcNow.AddDays(-days).ToString("yyyy-MM-dd HH:mm:ssZ", CultureInfo.InvariantCulture);
                }
                string where = w.Count > 0 ? "WHERE " + string.Join(" AND ", w) : "";
                Query(con, $@"SELECT DISTINCT m.Id, m.ReceivedAt_UTC AS date, m.IsRead AS rd,
                                     trim(ff.From_) AS sender, trim(ff.Subject) AS subject
                              FROM Messages m JOIN FTS_Messages ff ON ff.rowid=m.Id {folderJoin}
                              {where}
                              ORDER BY m.ReceivedAt_UTC DESC LIMIT $lim", p);
                break;
            }

            case "read":
            {
                if (pos.Count == 0) { Console.Error.WriteLine("read needs a messageId"); return 1; }
                long id = long.Parse(pos[0]);
                ReadMessage(con, id, OptInt(opt, "max", 16000));
                break;
            }

            case "tables":
                Query(con, "SELECT name, type FROM sqlite_master WHERE type IN ('table','view') ORDER BY name");
                break;
            case "schema":
                Query(con, $"SELECT name, sql FROM sqlite_master WHERE type='table' AND name LIKE '%{(pos.Count>0?pos[0]:"")}%' ORDER BY name");
                break;
            case "sql":
                Query(con, string.Join(" ", rest));
                break;

            default:
                Usage(); return 1;
        }
        return 0;
    }

    // ---- compose: pre-filled draft via the mailto: handoff (never sends) ----
    static int Compose(Dictionary<string, string> opt, List<string> pos)
    {
        string to = opt.GetValueOrDefault("to") ?? string.Join(",", pos);
        var toList = SplitAddrs(to);
        if (toList.Count == 0) { Console.Error.WriteLine("compose needs --to <addr[,addr]>"); return 1; }

        string subject = opt.GetValueOrDefault("subject", "");
        string body = opt.GetValueOrDefault("body", "");
        if (opt.TryGetValue("body-file", out var bf))
        {
            if (!File.Exists(bf)) { Console.Error.WriteLine($"body-file not found: {bf}"); return 1; }
            body = File.ReadAllText(bf);
        }
        var ccList = SplitAddrs(opt.GetValueOrDefault("cc"));
        var bccList = SplitAddrs(opt.GetValueOrDefault("bcc"));
        bool dry = opt.ContainsKey("dry-run");
        bool useHandler = opt.ContainsKey("use-default-handler");

        var qp = new List<string>();
        if (subject.Length > 0) qp.Add("subject=" + Uri.EscapeDataString(subject));
        if (body.Length > 0) qp.Add("body=" + Uri.EscapeDataString(body));
        if (ccList.Count > 0) qp.Add("cc=" + string.Join(",", ccList.Select(Uri.EscapeDataString)));
        if (bccList.Count > 0) qp.Add("bcc=" + string.Join(",", bccList.Select(Uri.EscapeDataString)));
        string url = "mailto:" + string.Join(",", toList) + (qp.Count > 0 ? "?" + string.Join("&", qp) : "");

        string exe = opt.GetValueOrDefault("mailbird-path")
                     ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Mailbird", "Mailbird.exe");
        if (!useHandler && !File.Exists(exe))
        {
            Console.Error.WriteLine($"Mailbird.exe not found at '{exe}'; using the OS default mailto: handler.");
            useHandler = true;
        }

        Console.WriteLine($"To      : {string.Join(", ", toList)}");
        if (ccList.Count > 0) Console.WriteLine($"Cc      : {string.Join(", ", ccList)}");
        if (subject.Length > 0) Console.WriteLine($"Subject : {subject}");
        Console.WriteLine($"mailto  : {url}");
        if (url.Length > 1800) Console.Error.WriteLine($"warning: mailto URL is {url.Length} chars; the OS may truncate a long body.");

        if (dry) { Console.WriteLine("[dry-run] nothing launched."); return 0; }

        try
        {
            var psi = useHandler
                ? new ProcessStartInfo(url) { UseShellExecute = true }
                : new ProcessStartInfo(exe, "\"" + url + "\"") { UseShellExecute = false };
            Process.Start(psi);
            Console.WriteLine("Opened a pre-filled Mailbird composer (DRAFT — review and click Send).");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine($"launch failed: {ex.Message}"); return 1; }
    }

    static List<string> SplitAddrs(string csv)
        => string.IsNullOrWhiteSpace(csv)
            ? new List<string>()
            : csv.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

    static void ReadMessage(SqliteConnection con, long id, int maxBody)
    {
        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = @"SELECT m.ReceivedAt_UTC, m.IsRead, m.AccountId,
                                       ff.From_, ff.To_, ff.Cc, ff.Subject, ff.Body
                                FROM Messages m JOIN FTS_Messages ff ON ff.rowid=m.Id WHERE m.Id=$id";
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) { Console.Error.WriteLine($"message {id} not found"); return; }
            string folders;
            using (var fc = con.CreateCommand())
            {
                fc.CommandText = @"SELECT group_concat(fo.Name, ', ') FROM Folders_Messages fm
                                   JOIN Folders fo ON fo.Id=fm.FolderId WHERE fm.MessageId=$id";
                fc.Parameters.AddWithValue("$id", id);
                folders = fc.ExecuteScalar() as string ?? "";
            }
            Console.WriteLine($"Id      : {id}");
            Console.WriteLine($"Date    : {r["ReceivedAt_UTC"]}");
            Console.WriteLine($"From    : {(r["From_"] as string)?.Trim()}");
            Console.WriteLine($"To      : {(r["To_"] as string)?.Trim()}");
            string cc = (r["Cc"] as string)?.Trim();
            if (!string.IsNullOrEmpty(cc)) Console.WriteLine($"Cc      : {cc}");
            Console.WriteLine($"Subject : {(r["Subject"] as string)?.Trim()}");
            Console.WriteLine($"Folder  : {folders}    Read: {(Convert.ToInt64(r["IsRead"]) != 0 ? "yes" : "no")}");
            Console.WriteLine(new string('-', 72));
            string body = r["Body"] as string ?? "";
            Console.WriteLine(body.Length > maxBody ? body.Substring(0, maxBody) + $"\n…[truncated {body.Length - maxBody} more chars]" : body);
        }
    }

    // ---- generic helpers ----
    static void Query(SqliteConnection con, string sql, Dictionary<string, object> ps = null)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        if (ps != null) foreach (var kv in ps) cmd.Parameters.AddWithValue(kv.Key, kv.Value ?? DBNull.Value);
        using var r = cmd.ExecuteReader();
        int n = r.FieldCount;

        if (Json)
        {
            var list = new List<Dictionary<string, object>>();
            while (r.Read())
            {
                var row = new Dictionary<string, object>();
                for (int i = 0; i < n; i++) row[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i)?.ToString();
                list.Add(row);
                if (list.Count >= 1000) break;
            }
            Console.WriteLine(JsonSerializer.Serialize(list));
            return;
        }

        Console.WriteLine(string.Join(" | ", Enumerable.Range(0, n).Select(r.GetName)));
        int rows = 0;
        while (r.Read())
        {
            Console.WriteLine(string.Join(" | ", Enumerable.Range(0, n)
                .Select(i => r.IsDBNull(i) ? "" : Trunc(r.GetValue(i)?.ToString()))));
            if (++rows >= 300) { Console.WriteLine("...(truncated at 300)"); break; }
        }
        Console.WriteLine($"[{rows} row(s)]");
    }

    static Dictionary<string, string> ParseOpts(List<string> args, out List<string> positionals)
    {
        var opt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        positionals = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i].StartsWith("--"))
            {
                string k = args[i].Substring(2);
                if (i + 1 < args.Count && !args[i + 1].StartsWith("--")) { opt[k] = args[++i]; }
                else opt[k] = "true";
            }
            else positionals.Add(args[i]);
        }
        return opt;
    }

    static int OptInt(Dictionary<string, string> o, string k, int dflt)
        => o.TryGetValue(k, out var v) && int.TryParse(v, out var n) ? n : dflt;

    // Quote each whitespace-separated token as an FTS5 phrase (implicit AND between them).
    // Makes arbitrary user/agent text safe regardless of punctuation.
    static string BuildFtsQuery(IEnumerable<string> words)
    {
        var toks = string.Join(" ", words).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", toks.Select(t => "\"" + t.Replace("\"", "\"\"") + "\""));
    }

    static string Trunc(string s)
    {
        if (s == null) return "";
        s = s.Replace("\r", " ").Replace("\n", " ");
        return s.Length > 160 ? s.Substring(0, 160) + "…" : s;
    }

    static void Usage()
    {
        Console.WriteLine(@"mailbird-cli — compose drafts + read-only search of Mailbird's Store.db

USAGE
  mailbird-cli compose --to <addr[,addr]> [--subject S] [--body B | --body-file F]
                       [--cc ..] [--bcc ..] [--dry-run] [--use-default-handler] [--mailbird-path EXE]
  mailbird-cli [<db>] accounts
  mailbird-cli [<db>] folders [accountId]
  mailbird-cli [<db>] search <query...> [--account ID] [--limit N] [--raw]
  mailbird-cli [<db>] list [--folder NAME] [--account ID] [--from SUBSTR] [--unread] [--days N] [--limit N]
  mailbird-cli [<db>] read <messageId> [--max CHARS]
  mailbird-cli [<db>] tables | schema <like> | sql <query...>
  (append --json to any read/search command for machine-readable output)

  compose opens a DRAFT only and never sends; From is Mailbird's default account; body is plain text.
  <db> is optional; defaults to %LOCALAPPDATA%\Mailbird\Store\Store.db (override with MAILBIRD_STORE_DB).
  Read/search open the DB read-only and never write to it.

EXAMPLES
  mailbird-cli compose --to you@example.com --subject ""Hi"" --body ""Hello there""
  mailbird-cli search ""invoice overdue"" --limit 10
  mailbird-cli list --folder Inbox --account 4 --unread --limit 20
  mailbird-cli read 112097

SEARCH SYNTAX (FTS5): plain words = AND; ""quoted phrase""; col:term (Subject/Body/From_/To_/Cc/Bcc);
  AND / OR / NOT; prefix*   (use --raw to pass this syntax through verbatim)");
    }
}
