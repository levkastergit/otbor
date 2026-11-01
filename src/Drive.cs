using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace Otbor
{
    public class DriveFile
    {
        public string Source;
        public string Target;
    }

    // Выкладка на Google Диск идёт через приложение Google: программа кладёт файлы в его папку, в облако их выгружает оно.
    // Своего подключения нет: общий ключ rclone отключается в 2026 году, а свой ключ требует проекта в Google Cloud.
    // Удаляются только файлы из журнала выкладки, поэтому своё содержимое папки программа не тронет.
    public class DriveSync
    {
        public const string FolderName = "Папины дочки спрайты";
        public const string SetupUrl = "https://dl.google.com/drive-file-stream/GoogleDriveSetup.exe";

        public static int QuietMs = 5000;
        public static int RetryMs = 60000;

        static readonly string[] DriveNames = { "My Drive", "Мой диск" };

        readonly object gate = new object();
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        readonly string manifest;

        string folder;
        List<DriveFile> pending;
        bool busy;
        bool flushing;
        DateTime due = DateTime.MinValue;
        string problem;
        int count = -1;

        public event Action Changed;

        public DriveSync(string manifest, string folder)
        {
            this.manifest = manifest;
            this.folder = folder;
        }

        static string SettingsFile
        {
            get { return Path.Combine(TokenStore.Dir, "drive.txt"); }
        }

        public static string LoadFolder()
        {
            try
            {
                if (!File.Exists(SettingsFile)) return null;
                string text = File.ReadAllText(SettingsFile, Encoding.UTF8).Trim();
                return text.Length > 0 ? text : null;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        // Папки приложения на этом ПК. В самом приложении можно подключить до четырёх аккаунтов, какой из них нужен, знает только пользователь.
        public static List<string> FindRoots()
        {
            var found = new List<string>();
            foreach (DriveInfo disk in DriveInfo.GetDrives().Where(d => d.DriveType != DriveType.Network && d.DriveType != DriveType.CDRom))
            {
                try
                {
                    if (!disk.IsReady) continue;
                    found.AddRange(DriveNames.Select(n => Path.Combine(disk.RootDirectory.FullName, n)).Where(Directory.Exists));
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            found.AddRange(DriveNames.Select(n => Path.Combine(home, n)).Where(Directory.Exists));
            return found;
        }

        public void Start()
        {
            new Thread(Loop) { IsBackground = true, Name = "Drive" }.Start();
        }

        public string Folder
        {
            get { lock (gate) return folder; }
        }

        public string Problem
        {
            get { lock (gate) return problem; }
        }

        public bool Busy
        {
            get { lock (gate) return busy; }
        }

        public int Count
        {
            get { lock (gate) return count; }
        }

        public bool HasUnsent
        {
            get { lock (gate) return folder != null && (pending != null || busy); }
        }

        public string Label
        {
            get
            {
                lock (gate)
                {
                    if (folder == null) return "Не подключён";
                    if (problem != null) return "Не выложено, повторю через минуту. " + problem;
                    if (busy) return "Копирую файлы в папку Диска…";
                    if (pending != null) return "Ждёт выкладки";
                    return count < 0 ? "Подключён: " + folder : "Файлов на Диске: " + count + ". Папка: " + folder;
                }
            }
        }

        public void SetFolder(string path)
        {
            Directory.CreateDirectory(TokenStore.Dir);
            File.WriteAllText(SettingsFile, path, new UTF8Encoding(false));
            lock (gate)
            {
                folder = path;
                problem = null;
                due = DateTime.MinValue;
                Monitor.PulseAll(gate);
            }
            Notify();
        }

        public void Request(List<DriveFile> files)
        {
            lock (gate)
            {
                pending = files;
                due = DateTime.UtcNow.AddMilliseconds(QuietMs);
                Monitor.PulseAll(gate);
            }
            Notify();
        }

        public void SendNow()
        {
            lock (gate)
            {
                flushing = true;
                Monitor.PulseAll(gate);
            }
        }

        public bool Flush(int timeoutMs)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            lock (gate)
            {
                if (folder == null) return pending == null;
                flushing = true;
                Monitor.PulseAll(gate);
                while (pending != null || busy)
                {
                    TimeSpan rest = deadline - DateTime.UtcNow;
                    if (rest <= TimeSpan.Zero || problem != null) break;
                    Monitor.Wait(gate, rest);
                }
                flushing = false;
                return pending == null && !busy;
            }
        }

        void Notify()
        {
            Action handler = Changed;
            if (handler != null) handler();
        }

        TimeSpan TimeToPush()
        {
            if (pending == null || folder == null) return Timeout.InfiniteTimeSpan;
            if (flushing) return TimeSpan.Zero;
            TimeSpan wait = due - DateTime.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        void Loop()
        {
            while (true)
            {
                List<DriveFile> files;
                string target;
                lock (gate)
                {
                    TimeSpan wait;
                    while ((wait = TimeToPush()) != TimeSpan.Zero) Monitor.Wait(gate, wait);
                    files = pending;
                    target = folder;
                    pending = null;
                    busy = true;
                }
                Notify();
                string error = null;
                try
                {
                    Push(files, target);
                }
                catch (IOException e) { error = e.Message; }
                catch (UnauthorizedAccessException e) { error = e.Message; }
                lock (gate)
                {
                    busy = false;
                    problem = error;
                    if (error == null) count = files.Count;
                    else
                    {
                        if (pending == null) pending = files;
                        due = DateTime.UtcNow.AddMilliseconds(RetryMs);
                        flushing = false;
                    }
                    Monitor.PulseAll(gate);
                }
                Notify();
            }
        }

        Dictionary<string, string> ReadManifest()
        {
            if (!File.Exists(manifest)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var saved = json.Deserialize<Dictionary<string, string>>(File.ReadAllText(manifest, Encoding.UTF8));
            return new Dictionary<string, string>(saved, StringComparer.OrdinalIgnoreCase);
        }

        void SaveManifest(Dictionary<string, string> known)
        {
            string tmp = manifest + ".tmp";
            File.WriteAllText(tmp, json.Serialize(known), new UTF8Encoding(false));
            if (File.Exists(manifest)) File.Replace(tmp, manifest, null);
            else File.Move(tmp, manifest);
        }

        void Push(List<DriveFile> files, string target)
        {
            // Если приложение закрыто, диска нет, и создавать папку на его месте нельзя.
            string parent = Path.GetDirectoryName(target);
            if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("Нет папки " + parent + ". Запущено ли приложение Google Диск?");
            Directory.CreateDirectory(target);
            Dictionary<string, string> known = ReadManifest();
            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int copied = 0;
            foreach (DriveFile file in files)
            {
                wanted.Add(file.Target);
                var source = new FileInfo(file.Source);
                if (!source.Exists) continue;
                string to = Path.Combine(target, file.Target);
                string stamp = source.Length + "|" + source.LastWriteTimeUtc.Ticks;
                string old;
                if (known.TryGetValue(file.Target, out old) && old == stamp && File.Exists(to)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(to));
                File.Copy(file.Source, to, true);
                known[file.Target] = stamp;
                if (++copied % 50 == 0) SaveManifest(known);
            }
            foreach (string stale in known.Keys.Where(k => !wanted.Contains(k)).ToList())
            {
                string path = Path.Combine(target, stale);
                if (File.Exists(path)) File.Delete(path);
                known.Remove(stale);
                RemoveEmptyFolders(Path.GetDirectoryName(path), target);
            }
            SaveManifest(known);
        }

        static void RemoveEmptyFolders(string dir, string top)
        {
            while (dir.Length > top.Length && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir);
                dir = Path.GetDirectoryName(dir);
            }
        }
    }
}
