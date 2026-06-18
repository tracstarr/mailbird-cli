using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using MimeKit;
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

    // Optional signature appended to the end of a draft/compose body (after a blank line) when one is
    // requested via --signature "..." or the MAILBIRD_SIGNATURE env var. Empty = no signature by default
    // (Mailbird applies the account's own signature on send, so adding one here would duplicate it).
    const string DefaultSignature = "";

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

            case "draft":
                return Draft(con, opt, pos);

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
        // mailto: is plain text only — keep the author's line breaks and append the signature after a blank line.
        body = AppendSignatureText(NormalizeNewlines(body), ResolveSignature(opt));
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

    // ---- body formatting & signature ----

    static string NormalizeNewlines(string s)
        => (s ?? "").Replace("\r\n", "\n").Replace("\r", "\n");

    // The signature to append, or null if disabled. Precedence: --no-signature > --signature >
    // MAILBIRD_SIGNATURE env var > DefaultSignature. A literal "\n" in the value is treated as a
    // real newline so multi-line signatures survive shell quoting.
    static string ResolveSignature(Dictionary<string, string> opt)
    {
        if (opt.ContainsKey("no-signature")) return null;
        string sig = opt.TryGetValue("signature", out var s)
            ? s
            : Environment.GetEnvironmentVariable("MAILBIRD_SIGNATURE") ?? DefaultSignature;
        sig = NormalizeNewlines(sig).Replace("\\n", "\n").Trim('\n');
        return string.IsNullOrWhiteSpace(sig) ? null : sig;
    }

    // Append a plain-text signature after a blank line (the standard "newline then signature" layout).
    static string AppendSignatureText(string body, string signature)
    {
        body = NormalizeNewlines(body).Trim('\n');
        if (string.IsNullOrEmpty(signature)) return body;
        return body.Length > 0 ? body + "\n\n" + signature : signature;
    }

    // Append a signature to an already-HTML body as its own spaced block.
    static string AppendSignatureHtml(string html, string signature)
    {
        if (string.IsNullOrEmpty(signature)) return html;
        var lines = NormalizeNewlines(signature).Split('\n').Select(WebUtility.HtmlEncode);
        return html + "<p style=\"margin:1em 0 0\">" + string.Join("<br>", lines) + "</p>";
    }

    // Turn a plain-text body into a (text, html) pair with real paragraph spacing: blank lines
    // separate <p> paragraphs and single newlines become <br>. The signature, if any, is appended
    // after a blank line in both forms. Without this, a plain-text body renders as one run-on block.
    static (string text, string html) BuildFormattedBody(string body, string signature)
    {
        string text = AppendSignatureText(body, signature);

        var html = new StringBuilder();
        foreach (var block in Regex.Split(NormalizeNewlines(body).Trim('\n'), @"\n[ \t]*\n"))
        {
            var joined = string.Join("<br>",
                block.Split('\n').Select(l => WebUtility.HtmlEncode(l.TrimEnd()))).Trim();
            if (joined.Length > 0) html.Append("<p style=\"margin:0 0 1em\">").Append(joined).Append("</p>");
        }
        if (!string.IsNullOrEmpty(signature))
        {
            var lines = NormalizeNewlines(signature).Split('\n').Select(WebUtility.HtmlEncode);
            html.Append("<p style=\"margin:0 0 1em\">").Append(string.Join("<br>", lines)).Append("</p>");
        }

        string doc = "<div style=\"font-family:Calibri,Arial,sans-serif;font-size:11pt;line-height:1.4\">"
                   + html + "</div>";
        return (text, doc);
    }

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

    // ---- draft: create a server-side draft via the account's provider, so it syncs INTO Mailbird ----
    // Reads the OAuth token from Store.db (read-only); routes Google -> Gmail REST drafts.create,
    // Microsoft -> IMAP APPEND to Drafts (XOAUTH2). Supports replies that attach to the right thread.
    sealed class Parent
    {
        public int AccountId; public string ThreadId; public string MailMessageId; public string Subject;
        public List<string> References = new List<string>(); public string ReplyToEmail;
    }
    sealed class Cred
    { public string Token; public DateTime Expires; public string Scope; public string Host; public string Username; }

    static int Draft(SqliteConnection con, Dictionary<string, string> opt, List<string> pos)
    {
        long? replyTo = null;
        if (opt.TryGetValue("reply-to", out var rt))
        {
            if (!long.TryParse(rt, out var rv)) { Console.Error.WriteLine("--reply-to needs a numeric messageId"); return 1; }
            replyTo = rv;
        }
        Parent parent = replyTo.HasValue ? LoadParent(con, replyTo.Value) : null;
        if (replyTo.HasValue && parent == null) { Console.Error.WriteLine($"reply-to message {replyTo} not found"); return 1; }

        int accountId;
        if (opt.TryGetValue("account", out var ac))
        {
            if (!int.TryParse(ac, out accountId)) { Console.Error.WriteLine("--account needs a numeric id"); return 1; }
        }
        else if (parent != null) accountId = parent.AccountId;
        else { Console.Error.WriteLine("draft needs --account <id> (or --reply-to <messageId> to inherit it)"); return 1; }

        if (parent != null && parent.AccountId != accountId)
        { Console.Error.WriteLine($"reply must use the parent's account ({parent.AccountId}); a thread is account-specific"); return 1; }

        var (fromEmail, fromName) = ResolveSender(con, accountId);
        if (fromEmail == null) { Console.Error.WriteLine($"account {accountId} not found"); return 2; }

        var cred = ResolveCred(con, accountId);
        if (cred == null) { Console.Error.WriteLine($"account {accountId} has no OAuth credential (only OAuth Google/Microsoft accounts are supported)"); return 2; }
        if (string.IsNullOrEmpty(cred.Token)) { Console.Error.WriteLine($"account {accountId}: no access token stored — open/keep Mailbird running so it refreshes the token."); return 2; }
        bool google = (cred.Scope ?? "").Contains("mail.google.com") || (cred.Host ?? "").Contains("gmail");
        bool microsoft = (cred.Scope ?? "").Contains("outlook.office.com") || (cred.Host ?? "").Contains("office365") || (cred.Host ?? "").Contains("outlook");
        if (!google && !microsoft) { Console.Error.WriteLine($"account {accountId}: unrecognized provider (host {cred.Host})"); return 4; }

        var toList = SplitAddrs(opt.GetValueOrDefault("to") ?? string.Join(",", pos));
        if (toList.Count == 0 && parent?.ReplyToEmail != null) toList.Add(parent.ReplyToEmail);
        if (toList.Count == 0) { Console.Error.WriteLine("draft needs --to <addr[,addr]> (or a --reply-to whose sender becomes the recipient)"); return 1; }
        var ccList = SplitAddrs(opt.GetValueOrDefault("cc"));
        var bccList = SplitAddrs(opt.GetValueOrDefault("bcc"));

        string subject = opt.GetValueOrDefault("subject");
        if (subject == null && parent != null) subject = EnsureRe(parent.Subject);
        subject ??= "";

        string body = opt.GetValueOrDefault("body", "");
        if (opt.TryGetValue("body-file", out var bf))
        {
            if (!File.Exists(bf)) { Console.Error.WriteLine($"body-file not found: {bf}"); return 1; }
            body = File.ReadAllText(bf);
        }
        bool html = opt.ContainsKey("html");
        string signature = ResolveSignature(opt);

        var msg = new MimeMessage();
        msg.From.Add(new MailboxAddress(fromName ?? "", fromEmail));
        try
        {
            foreach (var t in toList) msg.To.Add(MailboxAddress.Parse(t));
            foreach (var c in ccList) msg.Cc.Add(MailboxAddress.Parse(c));
            foreach (var b in bccList) msg.Bcc.Add(MailboxAddress.Parse(b));
        }
        catch (Exception ex) { Console.Error.WriteLine($"bad address: {ex.Message}"); return 1; }
        msg.Subject = subject;
        string domain = fromEmail.Contains('@') ? fromEmail.Substring(fromEmail.IndexOf('@') + 1) : "mailbird.local";
        msg.MessageId = $"Mailbird-{Guid.NewGuid()}@{domain}";
        if (parent != null && !string.IsNullOrEmpty(parent.MailMessageId))
        {
            string pid = parent.MailMessageId.Trim('<', '>', ' ');
            msg.InReplyTo = pid;
            foreach (var r in parent.References) { var rr = r.Trim('<', '>', ' '); if (rr.Length > 0) msg.References.Add(rr); }
            if (!msg.References.Contains(pid)) msg.References.Add(pid);
        }
        var bb = new BodyBuilder();
        if (html)
        {
            // Body is already HTML — append the signature as its own block, keep it HTML-only.
            bb.HtmlBody = AppendSignatureHtml(body, signature);
        }
        else
        {
            // Plain-text input: send multipart/alternative so the draft renders with real paragraph
            // spacing (HTML) while staying readable in text-only clients. Fixes run-on, one-line bodies.
            var (textBody, htmlBody) = BuildFormattedBody(body, signature);
            bb.TextBody = textBody;
            bb.HtmlBody = htmlBody;
        }
        msg.Body = bb.ToMessageBody();

        string provider = google ? "google" : "microsoft";
        Console.WriteLine($"Account : {accountId} <{fromEmail}>  [{provider}]");
        Console.WriteLine($"To      : {string.Join(", ", toList)}");
        if (ccList.Count > 0) Console.WriteLine($"Cc      : {string.Join(", ", ccList)}");
        Console.WriteLine($"Subject : {subject}");
        if (parent != null) Console.WriteLine($"Reply   : msg {replyTo} (thread {(parent.ThreadId ?? "via In-Reply-To/References")})");

        if (opt.ContainsKey("dry-run")) { Console.WriteLine("[dry-run] nothing created."); return 0; }
        if (cred.Expires != default && cred.Expires < DateTime.UtcNow)
            Console.Error.WriteLine("warning: the stored access token looks expired; keep Mailbird running so it refreshes the token.");

        try
        {
            return google
                ? GmailCreate(cred.Token, msg, ToGmailThreadId(parent?.ThreadId))
                : ImapAppend(cred.Token, cred.Host, cred.Username ?? fromEmail, msg);
        }
        catch (Exception ex) { Console.Error.WriteLine($"draft creation failed: {ex.Message}"); return 5; }
    }

    static Parent LoadParent(SqliteConnection con, long id)
    {
        var p = new Parent();
        using (var c = con.CreateCommand())
        {
            c.CommandText = "SELECT AccountId, ThreadId, MailMessageId, Subject FROM Messages WHERE Id=$id";
            c.Parameters.AddWithValue("$id", id);
            using var r = c.ExecuteReader();
            if (!r.Read()) return null;
            p.AccountId = Convert.ToInt32(r["AccountId"]);
            p.ThreadId = r["ThreadId"] as string;
            p.MailMessageId = r["MailMessageId"] as string;
            p.Subject = r["Subject"] as string ?? "";
        }
        using (var c = con.CreateCommand())
        {
            c.CommandText = "SELECT MailMessageId FROM MessageReferences WHERE MessageId=$id ORDER BY Id";
            c.Parameters.AddWithValue("$id", id);
            using var r = c.ExecuteReader();
            while (r.Read()) { var s = r.GetValue(0) as string; if (!string.IsNullOrEmpty(s)) p.References.Add(s); }
        }
        using (var c = con.CreateCommand())
        {
            // Prefer Reply-To (Type 1) over From (Type 0) as the reply recipient.
            c.CommandText = "SELECT Email FROM Messages_Contacts WHERE MessageId=$id AND Type IN (0,1) ORDER BY Type DESC LIMIT 1";
            c.Parameters.AddWithValue("$id", id);
            p.ReplyToEmail = c.ExecuteScalar() as string;
        }
        return p;
    }

    static (string, string) ResolveSender(SqliteConnection con, int accountId)
    {
        using var c = con.CreateCommand();
        c.CommandText = @"SELECT COALESCE(si.Email, a.Username) AS Email, si.Name
                          FROM Accounts a
                          LEFT JOIN SenderIdentities si ON si.Id = COALESCE(a.PrimarySenderIdentityId, a.DefaultSenderIdentityId)
                          WHERE a.Id=$a";
        c.Parameters.AddWithValue("$a", accountId);
        using var r = c.ExecuteReader();
        if (!r.Read()) return (null, null);
        return (r["Email"] as string, r["Name"] as string);
    }

    static Cred ResolveCred(SqliteConnection con, int accountId)
    {
        using var c = con.CreateCommand();
        c.CommandText = @"SELECT o.AccessToken, o.AccessTokenExpiresAt_UTC, o.ProviderScope, a.Server_Host, a.Username
                          FROM Accounts a JOIN OAuth2Credentials o ON o.Id = a.OAuth2CredentialsId WHERE a.Id=$a";
        c.Parameters.AddWithValue("$a", accountId);
        using var r = c.ExecuteReader();
        if (!r.Read()) return null;
        DateTime.TryParse(r["AccessTokenExpiresAt_UTC"] as string, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var exp);
        return new Cred
        {
            Token = r["AccessToken"] as string,
            Expires = exp,
            Scope = r["ProviderScope"] as string,
            Host = r["Server_Host"] as string,
            Username = r["Username"] as string,
        };
    }

    static string EnsureRe(string s)
    {
        s ??= "";
        return s.TrimStart().StartsWith("re:", StringComparison.OrdinalIgnoreCase) ? s : "Re: " + s;
    }

    // Mailbird stores Gmail's threadId as a decimal Int64; the Gmail REST API expects it in hex.
    static string ToGmailThreadId(string stored)
        => string.IsNullOrEmpty(stored) ? stored
           : (ulong.TryParse(stored, out var v) ? v.ToString("x") : stored);

    static int GmailCreate(string token, MimeMessage msg, string threadId)
    {
        var fo = FormatOptions.Default.Clone();
        fo.NewLineFormat = NewLineFormat.Dos;   // RFC-compliant CRLF
        byte[] mime;
        using (var ms = new MemoryStream()) { msg.WriteTo(fo, ms); mime = ms.ToArray(); }
        string raw = Convert.ToBase64String(mime).Replace('+', '-').Replace('/', '_').TrimEnd('=');

        (System.Net.HttpStatusCode status, string body) Post(string thread)
        {
            object payload = string.IsNullOrEmpty(thread)
                ? new { message = new { raw } }
                : new { message = new { raw, threadId = thread } };
            using var http = new HttpClient();
            var req = new HttpRequestMessage(HttpMethod.Post, "https://gmail.googleapis.com/gmail/v1/users/me/drafts")
            { Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json") };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = http.Send(req);
            using var sr = new StreamReader(resp.Content.ReadAsStream());
            return (resp.StatusCode, sr.ReadToEnd());
        }

        var (status, body) = Post(threadId);
        // Only a 400 means the threadId/subject didn't match the thread — retry unthreaded.
        // Auth (401/403) and server (5xx) errors are surfaced as-is, not misreported as a thread problem.
        if (status == System.Net.HttpStatusCode.BadRequest && !string.IsNullOrEmpty(threadId))
        {
            Console.Error.WriteLine("note: Gmail rejected the threadId (subject/reference mismatch); creating an unthreaded draft instead.");
            (status, body) = Post(null);
        }
        if ((int)status < 200 || (int)status >= 300)
            throw new InvalidOperationException($"Gmail API HTTP {(int)status}: {body}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        string draftId = root.GetProperty("id").GetString();
        var m = root.GetProperty("message");
        string msgId = m.GetProperty("id").GetString();
        string thr = m.TryGetProperty("threadId", out var te) ? te.GetString() : null;
        if (Json)
            Console.WriteLine(JsonSerializer.Serialize(new { ok = true, provider = "google", draftId, messageId = msgId, threadId = thr }));
        else
        {
            Console.WriteLine($"Created Gmail draft {draftId} (thread {thr}).");
            Console.WriteLine("It will sync into Mailbird's Drafts folder on the next poll.");
        }
        return 0;
    }

    static int ImapAppend(string token, string host, string user, MimeMessage msg)
    {
        using var client = new ImapClient();
        client.Connect(host, 993, SecureSocketOptions.SslOnConnect);
        client.Authenticate(new SaslMechanismOAuth2(user, token));
        IMailFolder drafts = null;
        try { drafts = client.GetFolder(SpecialFolder.Drafts); } catch { /* Outlook IMAP lacks SPECIAL-USE/XLIST */ }
        if (drafts == null)
        {
            var personal = client.GetFolder(client.PersonalNamespaces[0]);
            foreach (var f in personal.GetSubfolders(false))
                if (string.Equals(f.Name, "Drafts", StringComparison.OrdinalIgnoreCase)) { drafts = f; break; }
        }
        if (drafts == null) { client.Disconnect(true); Console.Error.WriteLine("could not locate the Drafts folder over IMAP"); return 5; }
        drafts.Open(FolderAccess.ReadWrite);
        var uid = drafts.Append(msg, MessageFlags.Draft | MessageFlags.Seen);
        string folder = drafts.FullName;
        client.Disconnect(true);
        if (Json)
            Console.WriteLine(JsonSerializer.Serialize(new { ok = true, provider = "microsoft", folder, appendUid = uid.HasValue ? (object)uid.Value.Id : null, messageId = msg.MessageId }));
        else
        {
            Console.WriteLine($"Appended draft to {folder} (uid {(uid.HasValue ? uid.Value.ToString() : "n/a")}).");
            Console.WriteLine("It will sync into Mailbird's Drafts folder on the next poll.");
        }
        return 0;
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
                       [--cc ..] [--bcc ..] [--signature S | --no-signature]
                       [--dry-run] [--use-default-handler] [--mailbird-path EXE]
  mailbird-cli [<db>] draft [--account ID] --to <addr[,addr]> [--subject S] [--body B | --body-file F]
                       [--reply-to <messageId>] [--cc ..] [--bcc ..] [--html]
                       [--signature S | --no-signature] [--dry-run] [--json]
  mailbird-cli [<db>] accounts
  mailbird-cli [<db>] folders [accountId]
  mailbird-cli [<db>] search <query...> [--account ID] [--limit N] [--raw]
  mailbird-cli [<db>] list [--folder NAME] [--account ID] [--from SUBSTR] [--unread] [--days N] [--limit N]
  mailbird-cli [<db>] read <messageId> [--max CHARS]
  mailbird-cli [<db>] tables | schema <like> | sql <query...>
  (append --json to any read/search command for machine-readable output)

  compose opens a DRAFT only and never sends; From is Mailbird's default account; body is plain text.
  draft creates a server-side draft via the account's provider (Gmail API / Outlook IMAP) using the OAuth
       token Mailbird already holds, so it syncs back INTO Mailbird's Drafts. Picks the From account and,
       with --reply-to, attaches to that message's thread. Never sends. (Reads the DB read-only.)
       A plain-text body is sent as multipart/alternative with proper paragraph spacing (blank lines =
       paragraphs, single newlines = line breaks); pass --html if the body is already HTML.
  signature: optional, off by default. When set via --signature ""..."" (use \n for line breaks) or the
       MAILBIRD_SIGNATURE env var, it is appended after a blank line at the end of the body.
  <db> is optional; defaults to %LOCALAPPDATA%\Mailbird\Store\Store.db (override with MAILBIRD_STORE_DB).
  Read/search open the DB read-only and never write to it.

EXAMPLES
  mailbird-cli compose --to you@example.com --subject ""Hi"" --body ""Hello there""
  mailbird-cli draft --account 1 --to you@example.com --subject ""Hi"" --body ""Hello there""
  mailbird-cli draft --reply-to 112187 --body ""Thanks — sounds good.""   (reply in the parent's account+thread)
  mailbird-cli search ""invoice overdue"" --limit 10
  mailbird-cli list --folder Inbox --account 4 --unread --limit 20
  mailbird-cli read 112097

SEARCH SYNTAX (FTS5): plain words = AND; ""quoted phrase""; col:term (Subject/Body/From_/To_/Cc/Bcc);
  AND / OR / NOT; prefix*   (use --raw to pass this syntax through verbatim)");
    }
}
