using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace Otbor
{
    // Запускает скрипты FluxKit и отдаёт их вывод построчно. Строки, которые перерисовывают себя через \r
    // (прогресс обучения), идут в Live и в журнал не попадают: иначе их были бы тысячи.
    public class KitTask
    {
        static readonly Regex Steps = new Regex(@"^\[(\d+)/(\d+)\]|\b(\d+)/(\d+) \[", RegexOptions.Compiled);

        public event Action<string> Line;
        public event Action<string> Live;
        public event Action<int> Finished;

        readonly object gate = new object();
        Process process;
        int readers;

        public bool WasStopped { get; private set; }

        public bool Running
        {
            get { lock (gate) return process != null; }
        }

        // Кодировка задаётся в самом PowerShell: без этого его русский текст приходит в кодовой странице консоли.
        public static ProcessStartInfo PowerShell(string kit, string script, string arguments)
        {
            string path = Path.Combine(kit, "scripts", script).Replace("'", "''");
            string command = "[Console]::OutputEncoding = [Text.Encoding]::UTF8; & '" + path + "' " + arguments + "; exit $LASTEXITCODE";
            string shell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell\\v1.0\\powershell.exe");
            return new ProcessStartInfo(shell, "-NoProfile -ExecutionPolicy Bypass -Command \"" + command + "\"") { WorkingDirectory = kit };
        }

        public static bool TryProgress(string line, out int done, out int total)
        {
            done = total = 0;
            Match m = Steps.Match(line);
            if (!m.Success) return false;
            int first = m.Groups[1].Success ? 1 : 3;
            done = int.Parse(m.Groups[first].Value);
            total = int.Parse(m.Groups[first + 1].Value);
            return total > 0;
        }

        public void Start(ProcessStartInfo info)
        {
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            info.EnvironmentVariables["OTBOR"] = "1";
            lock (gate)
            {
                if (process != null) throw new InvalidOperationException("Задача уже идёт.");
                WasStopped = false;
                process = Process.Start(info);
                readers = 2;
                Read(process.StandardOutput.BaseStream);
                Read(process.StandardError.BaseStream);
            }
        }

        // Скрипт запускает Python, а тот свои процессы, поэтому останавливается всё дерево.
        public void Stop()
        {
            Process target;
            lock (gate) target = process;
            if (target == null) return;
            WasStopped = true;
            var kill = new ProcessStartInfo("taskkill", "/T /F /PID " + target.Id) { UseShellExecute = false, CreateNoWindow = true };
            using (Process p = Process.Start(kill)) p.WaitForExit(10000);
        }

        void Read(Stream stream)
        {
            new Thread(() => Pump(stream)) { IsBackground = true, Name = "KitTask" }.Start();
        }

        void Pump(Stream stream)
        {
            Decoder decoder = new UTF8Encoding(false).GetDecoder();
            var bytes = new byte[4096];
            var chars = new char[new UTF8Encoding(false).GetMaxCharCount(bytes.Length)];
            var line = new StringBuilder();
            bool rewind = false;
            try
            {
                int read;
                while ((read = stream.Read(bytes, 0, bytes.Length)) > 0)
                {
                    int count = decoder.GetChars(bytes, 0, read, chars, 0);
                    for (int i = 0; i < count; i++)
                    {
                        char c = chars[i];
                        if (c == '\n')
                        {
                            Send(Line, line.ToString());
                            line.Clear();
                            rewind = false;
                        }
                        else if (c == '\r') rewind = true;
                        else
                        {
                            if (rewind) line.Clear();
                            rewind = false;
                            line.Append(c);
                        }
                    }
                    if (line.Length > 0) Send(Live, line.ToString());
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            if (line.Length > 0) Send(Line, line.ToString());
            Done();
        }

        static void Send(Action<string> handler, string text)
        {
            if (handler != null) handler(text);
        }

        void Done()
        {
            int code;
            lock (gate)
            {
                if (--readers > 0) return;
                process.WaitForExit();
                code = process.ExitCode;
                process.Dispose();
                process = null;
            }
            Action<int> handler = Finished;
            if (handler != null) handler(code);
        }
    }

    // Обновление из релиза GitHub: программа и файлы кита берутся из одного тега, поэтому они всегда подходят друг другу.
    public static class KitUpdate
    {
        public const string Current = "1.5.8";

        public static string ApiUrl = "https://api.github.com/repos/" + GitHubSync.Repo + "/releases/latest";
        public static string AssetUrl = "https://github.com/" + GitHubSync.Repo + "/releases/download/";
        public static string RawUrl = "https://raw.githubusercontent.com/" + GitHubSync.Repo + "/";

        public static string LatestTag()
        {
            var doc = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(GitHubSync.Call("GET", ApiUrl, null, null));
            return (string)doc["tag_name"];
        }

        public static bool IsNewer(string tag)
        {
            return new Version(tag.TrimStart('v')) > new Version(Current);
        }

        static byte[] Download(string url)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = "Otbor";
            request.Timeout = 60000;
            request.ReadWriteTimeout = 60000;
            using (var response = (HttpWebResponse)request.GetResponse())
            using (Stream stream = response.GetResponseStream())
            using (var memory = new MemoryStream())
            {
                stream.CopyTo(memory);
                if (memory.Length == 0) throw new InvalidDataException("Пустой файл: " + url);
                return memory.ToArray();
            }
        }

        // Сначала скачивается всё: при обрыве связи на диске не окажется смесь старых и новых файлов.
        public static void Install(string tag, string kit, string running, Action<string> say)
        {
            var files = new List<KeyValuePair<string, byte[]>>();
            string list = Encoding.UTF8.GetString(Download(RawUrl + tag + "/kit/files.txt"));
            foreach (string line in list.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0))
            {
                string[] part = line.Split('\t');
                say("Скачиваю " + part[0]);
                files.Add(new KeyValuePair<string, byte[]>(Path.Combine(kit, part[1]), Download(RawUrl + tag + "/" + part[0])));
            }
            say("Скачиваю Otbor.exe");
            byte[] exe = Download(AssetUrl + tag + "/Otbor.exe");
            if (exe.Length < 2 || exe[0] != 'M' || exe[1] != 'Z') throw new InvalidDataException("Скачанный Otbor.exe повреждён.");

            foreach (KeyValuePair<string, byte[]> file in files)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file.Key));
                File.WriteAllBytes(file.Key + ".new", file.Value);
            }
            foreach (KeyValuePair<string, byte[]> file in files)
            {
                if (File.Exists(file.Key)) File.Delete(file.Key);
                File.Move(file.Key + ".new", file.Key);
            }
            foreach (string copy in Copies(kit, running))
            {
                try
                {
                    PutExe(copy, exe, running);
                }
                catch (Exception e)
                {
                    if (e is IOException || e is UnauthorizedAccessException) say("Не обновилась копия " + copy + ": закройте её и обновите ещё раз.");
                    else throw;
                }
            }
            say("Обновлено до " + tag);
        }

        static List<string> Copies(string kit, string running)
        {
            var paths = new List<string> { Path.GetFullPath(running) };
            foreach (string path in new[] { Path.Combine(kit, "Otbor.exe"), Path.Combine(kit, "results", "Otbor.exe") })
            {
                string full = Path.GetFullPath(path);
                if (File.Exists(full) && !paths.Contains(full, StringComparer.OrdinalIgnoreCase)) paths.Add(full);
            }
            return paths;
        }

        // Работающий exe нельзя заменить, но можно переименовать; старый файл удаляется при следующем запуске.
        static void PutExe(string target, byte[] data, string running)
        {
            string fresh = target + ".new";
            File.WriteAllBytes(fresh, data);
            if (string.Equals(Path.GetFullPath(target), Path.GetFullPath(running), StringComparison.OrdinalIgnoreCase))
            {
                string old = target + ".old";
                if (File.Exists(old)) File.Delete(old);
                File.Move(target, old);
            }
            else if (File.Exists(target)) File.Delete(target);
            File.Move(fresh, target);
        }

        public static void Cleanup(string running)
        {
            try
            {
                string old = running + ".old";
                if (File.Exists(old)) File.Delete(old);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
