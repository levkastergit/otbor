using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace Otbor
{
    // Ключ лежит вне папки results и вне репозитория и расшифровывается только этим пользователем Windows.
    public static class TokenStore
    {
        public static string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Otbor");

        static string KeyFile
        {
            get { return Path.Combine(Dir, "github.key"); }
        }

        public static string Load()
        {
            try
            {
                if (!File.Exists(KeyFile)) return null;
                byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(KeyFile), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
            catch (CryptographicException) { return null; }
            catch (IOException) { return null; }
        }

        public static void Save(string token)
        {
            Directory.CreateDirectory(Dir);
            byte[] plain = Encoding.UTF8.GetBytes(token);
            File.WriteAllBytes(KeyFile, ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser));
        }
    }

    public class ApiError : Exception
    {
        public readonly int Status;
        public readonly bool Limited;
        public readonly int WaitSeconds;

        public ApiError(int status, bool limited, int waitSeconds, string message) : base(message)
        {
            Status = status;
            Limited = limited;
            WaitSeconds = waitSeconds;
        }
    }

    public enum SyncState { NoKey, Waiting, Sending, Sent, Retrying, BadKey }

    public class GitHubSync
    {
        public const string Repo = "levkastergit/otbor";
        public const string Branch = "main";
        public const string ApiUrl = "https://api.github.com/repos/" + Repo + "/contents/status/status.json";
        public const string KeyPage = "https://github.com/settings/personal-access-tokens/new?name=Otbor" +
                                      "&description=Otbor+status+upload&target_name=levkastergit&expires_in=90&contents=write";

        // Лимит GitHub — 500 запросов на запись в час; отправка раз в 30 секунд даёт не больше 120.
        const int DefaultQuietMs = 10000;
        const int DefaultGapMs = 30000;
        const int RequestTimeoutMs = 20000;
        const int MaxBackoffSeconds = 900;

        public int BackoffBaseSeconds = 60;

        readonly object gate = new object();
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        readonly string url;
        readonly string pc;
        readonly int quietMs;
        readonly int gapMs;

        string token;
        List<StatusRow> pending;
        int pendingLeft;
        bool sending;
        bool flushing;
        int failures;
        DateTime lastChange;
        DateTime notBefore = DateTime.MinValue;
        DateTime limitedUntil = DateTime.MinValue;
        SyncState state;
        string detail;
        DateTime lastSent;

        public event Action Changed;

        public GitHubSync(string token, string pc) : this(token, pc, ApiUrl, DefaultQuietMs, DefaultGapMs)
        {
        }

        public GitHubSync(string token, string pc, string url, int quietMs, int gapMs)
        {
            this.token = token;
            this.pc = pc;
            this.url = url;
            this.quietMs = quietMs;
            this.gapMs = gapMs;
            state = token == null ? SyncState.NoKey : SyncState.Sent;
        }

        public void Start()
        {
            // Сборка csc не указывает целевую версию .NET, поэтому по умолчанию были бы только устаревшие протоколы.
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            new Thread(Loop) { IsBackground = true, Name = "GitHub" }.Start();
        }

        public SyncState State
        {
            get { lock (gate) return state; }
        }

        public string Detail
        {
            get { lock (gate) return detail; }
        }

        public bool NeedsKey
        {
            get { lock (gate) return token == null; }
        }

        public bool HasUnsent
        {
            get { lock (gate) return token != null && (pending != null || sending); }
        }

        public string Label
        {
            get
            {
                lock (gate)
                {
                    switch (state)
                    {
                        case SyncState.NoKey: return "GitHub: подключить";
                        case SyncState.BadKey: return "GitHub: нужен новый ключ";
                        case SyncState.Waiting: return "GitHub: ждёт отправки";
                        case SyncState.Sending: return "GitHub: отправляю…";
                        case SyncState.Retrying: return "GitHub: не отправлено, повторю";
                    }
                    return lastSent == default(DateTime)
                        ? "GitHub: всё на месте"
                        : "GitHub: отправлено в " + lastSent.ToString("HH:mm:ss");
                }
            }
        }

        public void SetToken(string value)
        {
            lock (gate)
            {
                token = value;
                failures = 0;
                state = pending != null ? SyncState.Waiting : SyncState.Sent;
                detail = null;
                Monitor.PulseAll(gate);
            }
            Notify();
        }

        public void Request(List<StatusRow> rows, int left)
        {
            lock (gate)
            {
                pending = rows;
                pendingLeft = left;
                lastChange = DateTime.UtcNow;
                if (token != null && state != SyncState.Retrying) state = SyncState.Waiting;
                Monitor.PulseAll(gate);
            }
            Notify();
        }

        public void SendNow()
        {
            lock (gate)
            {
                if (pending == null) return;
                flushing = true;
                Monitor.PulseAll(gate);
            }
        }

        // Лимит GitHub не обходится: если он действует, данные уйдут при следующем запуске.
        public bool Flush(int timeoutMs)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            lock (gate)
            {
                if (token == null) return pending == null;
                if (pending == null && !sending) return true;
                flushing = true;
                Monitor.PulseAll(gate);
                while (flushing)
                {
                    TimeSpan rest = deadline - DateTime.UtcNow;
                    if (rest <= TimeSpan.Zero)
                    {
                        flushing = false;
                        return false;
                    }
                    Monitor.Wait(gate, rest);
                }
                return pending == null && !sending;
            }
        }

        void Notify()
        {
            Action handler = Changed;
            if (handler != null) handler();
        }

        static DateTime Later(DateTime a, DateTime b)
        {
            return a > b ? a : b;
        }

        TimeSpan TimeToSend()
        {
            if (pending == null || token == null) return Timeout.InfiniteTimeSpan;
            DateTime due = flushing
                ? limitedUntil
                : Later(limitedUntil, Later(notBefore, lastChange.AddMilliseconds(quietMs)));
            TimeSpan wait = due - DateTime.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        void Loop()
        {
            while (true)
            {
                List<StatusRow> rows;
                int left;
                string key;
                lock (gate)
                {
                    TimeSpan wait;
                    while ((wait = TimeToSend()) != TimeSpan.Zero) Monitor.Wait(gate, wait);
                    rows = pending;
                    left = pendingLeft;
                    key = token;
                    pending = null;
                    sending = true;
                    state = SyncState.Sending;
                }
                Notify();
                try
                {
                    Finished(Send(key, rows, left));
                }
                catch (ApiError e)
                {
                    if (e.Limited) Failed(rows, left, BackoffSeconds(e.WaitSeconds), true, "GitHub просит подождать.");
                    else if (e.Status == 401) KeyRejected(rows, left, "GitHub не принял ключ: он неверный или срок вышел.");
                    else if (e.Status == 403 || e.Status == 404)
                        KeyRejected(rows, left, "У ключа нет права записи в " + Repo + ".");
                    else Failed(rows, left, BackoffSeconds(0), false, "GitHub ответил " + e.Status + ": " + e.Message);
                }
                catch (Exception e)
                {
                    Failed(rows, left, BackoffSeconds(0), false, "Не отправлено: " + e.Message);
                }
                Notify();
            }
        }

        int BackoffSeconds(int asked)
        {
            int doubled = BackoffBaseSeconds << Math.Min(failures, 4);
            return Math.Min(Math.Max(asked, doubled), MaxBackoffSeconds);
        }

        void Finished(bool committed)
        {
            lock (gate)
            {
                sending = false;
                failures = 0;
                detail = null;
                if (committed)
                {
                    lastSent = DateTime.Now;
                    notBefore = DateTime.UtcNow.AddMilliseconds(gapMs);
                }
                if (pending == null) flushing = false;
                state = pending != null ? SyncState.Waiting : SyncState.Sent;
                Monitor.PulseAll(gate);
            }
        }

        void Restore(List<StatusRow> rows, int left)
        {
            sending = false;
            flushing = false;
            if (pending != null) return;
            pending = rows;
            pendingLeft = left;
        }

        void Failed(List<StatusRow> rows, int left, int seconds, bool limited, string message)
        {
            lock (gate)
            {
                Restore(rows, left);
                failures++;
                DateTime next = DateTime.UtcNow.AddSeconds(seconds);
                if (limited) limitedUntil = next;
                notBefore = next;
                state = SyncState.Retrying;
                detail = message + " Повтор через " + seconds + " с, разбор сохранён на этом ПК.";
                Monitor.PulseAll(gate);
            }
        }

        void KeyRejected(List<StatusRow> rows, int left, string message)
        {
            lock (gate)
            {
                Restore(rows, left);
                token = null;
                state = SyncState.BadKey;
                detail = message + " Нажмите кнопку GitHub и вставьте новый ключ.";
                Monitor.PulseAll(gate);
            }
        }

        // false — на GitHub уже то же самое, новой записи в истории не нужно.
        bool Send(string key, List<StatusRow> rows, int left)
        {
            for (int attempt = 0; ; attempt++)
            {
                string sha = null;
                string remoteText = null;
                var remote = new List<StatusRow>();
                try
                {
                    var file = json.Deserialize<Dictionary<string, object>>(Call("GET", url + "?ref=" + Branch, key, null));
                    sha = (string)file["sha"];
                    remoteText = Encoding.UTF8.GetString(Convert.FromBase64String((string)file["content"]));
                    remote = StatusDoc.Parse(remoteText);
                }
                catch (ApiError e)
                {
                    if (e.Status != 404) throw;
                }

                List<StatusRow> merged = StatusDoc.Merge(rows, remote, pc);
                string text = StatusDoc.Format(merged, left);
                if (text == remoteText) return false;

                var body = new Dictionary<string, object>
                {
                    { "message", Message(merged, left) },
                    { "content", Convert.ToBase64String(new UTF8Encoding(false).GetBytes(text)) },
                    { "branch", Branch }
                };
                if (sha != null) body["sha"] = sha;
                try
                {
                    Call("PUT", url, key, json.Serialize(body));
                    return true;
                }
                catch (ApiError e)
                {
                    if (e.Status != 409 || attempt >= 2) throw;
                }
            }
        }

        static string Message(List<StatusRow> rows, int left)
        {
            int ready = 0;
            int regen = 0;
            foreach (StatusRow row in rows)
            {
                if (row.state == StatusRow.Ready) ready++;
                else regen++;
            }
            return "Отбор: готово " + ready + ", перегенерировать " + regen + ", осталось " + left;
        }

        public static string Call(string method, string address, string key, string body)
        {
            var request = (HttpWebRequest)WebRequest.Create(address);
            request.Method = method;
            request.UserAgent = "Otbor";
            request.Accept = "application/vnd.github+json";
            request.Timeout = RequestTimeoutMs;
            request.ReadWriteTimeout = RequestTimeoutMs;
            request.ServicePoint.Expect100Continue = false;
            if (key != null) request.Headers["Authorization"] = "Bearer " + key;
            try
            {
                if (body != null)
                {
                    byte[] bytes = new UTF8Encoding(false).GetBytes(body);
                    request.ContentType = "application/json";
                    request.ContentLength = bytes.Length;
                    using (Stream stream = request.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);
                }
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                    return reader.ReadToEnd();
            }
            catch (WebException e)
            {
                var response = e.Response as HttpWebResponse;
                if (response == null) throw;
                using (response) throw ToApiError(response);
            }
        }

        static ApiError ToApiError(HttpWebResponse response)
        {
            string text;
            using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                text = reader.ReadToEnd();
            int status = (int)response.StatusCode;
            int retryAfter = Number(response.Headers["Retry-After"]);
            bool exhausted = response.Headers["X-RateLimit-Remaining"] == "0";
            bool limited = status == 429 ||
                           (status == 403 && (retryAfter > 0 || exhausted ||
                                              text.IndexOf("rate limit", StringComparison.OrdinalIgnoreCase) >= 0));
            int wait = retryAfter;
            if (wait == 0 && exhausted)
            {
                long reset = Number(response.Headers["X-RateLimit-Reset"]);
                long now = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                wait = (int)Math.Max(0, reset - now);
            }
            return new ApiError(status, limited, wait, text);
        }

        static int Number(string value)
        {
            int n;
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : 0;
        }
    }
}
