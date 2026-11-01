using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace Otbor
{
    public class Variant
    {
        public string Name;
        public string Raw;
        public string Cut;

        public string Show { get { return Cut ?? Raw; } }

        public string Label
        {
            get
            {
                int i = Name.LastIndexOf("__", StringComparison.Ordinal);
                return i >= 0 ? Name.Substring(i + 2) : Name;
            }
        }

        public IEnumerable<string> Files
        {
            get
            {
                if (Raw != null) yield return Raw;
                if (Cut != null) yield return Cut;
            }
        }
    }

    public class Job
    {
        public string Group;
        public string Id;
        public string Ru;
        public string Where;
        public string Asset;
        public string Goes;
        public Section Section = SectionMap.Other;
        public int SectionOrder;
        public int Order = int.MaxValue;
        public List<Variant> Variants = new List<Variant>();
    }

    public class Section
    {
        public string Kind;
        public string Name;
        public int Index;
    }

    public class SectionStat
    {
        public Section Section;
        public int Total;
        public int Left;

        public int Done
        {
            get { return Total - Left; }
        }
    }

    // Формат data/sections.json: файл собирает tools/build_sections.py, в программу он вшит ресурсом.
    public class SectionFile
    {
        public List<SectionEntry> sections;
    }

    public class SectionEntry
    {
        public string kind;
        public string name;
        public List<SectionJob> jobs;
    }

    public class SectionJob
    {
        public string id;
        public string goes;
    }

    public class Placement
    {
        public Section Section = SectionMap.Other;
        public int Order;
        public string Goes;
    }

    public static class SectionMap
    {
        public static readonly Section Other = new Section { Kind = "Прочее", Name = "Без раздела", Index = int.MaxValue };

        static readonly Dictionary<string, Placement> places = Load();

        static Dictionary<string, Placement> Load()
        {
            var map = new Dictionary<string, Placement>(StringComparer.OrdinalIgnoreCase);
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("sections.json"))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                var file = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<SectionFile>(reader.ReadToEnd());
                int index = 0;
                foreach (SectionEntry entry in file.sections)
                {
                    var section = new Section { Kind = entry.kind, Name = entry.name, Index = index++ };
                    int order = 0;
                    foreach (SectionJob job in entry.jobs)
                        map[job.id] = new Placement { Section = section, Order = order++, Goes = job.goes };
                }
            }
            return map;
        }

        public static Placement Of(string id)
        {
            Placement place;
            return places.TryGetValue(id, out place) ? place : new Placement();
        }
    }

    public class FileMove
    {
        public string From;
        public string To;
    }

    public class Decision
    {
        public string Group;
        public string Id;
        public List<string> Picked;
        public bool None;
        public List<FileMove> Moves;
        public string At;
        public string Note;
    }

    // Плитка обзора: выбранный вариант задания. Variant == null — ни один не подошёл.
    public class ReviewItem
    {
        public Decision Decision;
        public string Ru;
        public Variant Variant;
    }

    // Имена полей — формат status/status.json: его читают generate.py и Claude.
    public class StatusRow
    {
        public const string Ready = "ready";
        public const string Regen = "regen";

        public string id;
        public string group;
        public string ru;
        public string state;
        public List<string> picked;
        public List<string> tried;
        public string note;
        public string at;
        public string pc;
    }

    public class StatusDoc
    {
        public Dictionary<string, int> summary;
        public List<StatusRow> jobs;

        static string KeyOf(StatusRow row)
        {
            return row.group + "/" + row.id;
        }

        public static List<StatusRow> Parse(string text)
        {
            var doc = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<StatusDoc>(text);
            return doc != null && doc.jobs != null ? doc.jobs : new List<StatusRow>();
        }

        // По строке на задание: иначе в истории репозитория каждая отправка выглядела бы как замена всего файла.
        public static string Format(IEnumerable<StatusRow> rows, int left)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            List<StatusRow> ordered = rows
                .OrderBy(r => r.group, StringComparer.Ordinal)
                .ThenBy(r => r.id, StringComparer.Ordinal)
                .ToList();
            var summary = new Dictionary<string, int>
            {
                { "ready", ordered.Count(r => r.state == StatusRow.Ready) },
                { "regen", ordered.Count(r => r.state == StatusRow.Regen) },
                { "left", left }
            };
            return "{\n \"summary\": " + json.Serialize(summary) + ",\n \"jobs\": [\n" +
                   string.Join(",\n", ordered.Select(r => "  " + json.Serialize(r))) + "\n ]\n}\n";
        }

        // У каждого ПК своя папка results: чужие записи сохраняются, а свои берутся из журнала, поэтому «Назад»
        // убирает запись и из репозитория. Пустой журнал ничего не удаляет: это новая папка, а не отмена всего разбора.
        // Если задание разобрано на двух ПК, верна более свежая запись.
        public static List<StatusRow> Merge(List<StatusRow> local, List<StatusRow> remote, string pc)
        {
            Dictionary<string, StatusRow> merged = local.ToDictionary(KeyOf);
            foreach (StatusRow row in remote)
            {
                StatusRow mine;
                if (merged.TryGetValue(KeyOf(row), out mine))
                {
                    if (row.pc != pc && string.CompareOrdinal(row.at, mine.at) > 0) merged[KeyOf(row)] = row;
                }
                else if (row.pc != pc || local.Count == 0) merged[KeyOf(row)] = row;
            }
            return merged.Values.ToList();
        }
    }

    public class Session
    {
        public const string NeedDir = "_Нужные";
        public const string TrashDir = "_Ненужные";
        public const string JournalFile = "_отбор.json";
        public const string StatusFile = "_статус.json";
        public const string ChoiceFile = "выбор.csv";
        public const string RegenFile = "перегенерировать.txt";

        // Репозиторий публичный, поэтому имя компьютера туда не попадает.
        public static readonly string PcId = MakePcId();

        static string MakePcId()
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(Environment.MachineName));
                return BitConverter.ToString(hash, 0, 4).Replace("-", "").ToLowerInvariant();
            }
        }

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        static extern int StrCmpLogicalW(string a, string b);

        class Info
        {
            public string Ru, Where, Asset;
            public int Order;
        }

        public readonly string Root;
        public readonly List<Job> Queue = new List<Job>();
        public List<Decision> Done = new List<Decision>();
        public string Warning;

        readonly Dictionary<string, Info> info = new Dictionary<string, Info>(StringComparer.OrdinalIgnoreCase);
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        public Session(string root)
        {
            Root = Path.GetFullPath(root);
            // Обучающие наборы в datasets и refs выглядят как задания, и отбор разнёс бы их по папкам.
            if (IsKit(Root)) throw new InvalidOperationException("Это папка FluxKit, а варианты лежат в её подпапке results.");
            LoadInfo();
            string journal = Abs(JournalFile);
            if (File.Exists(journal))
                Done = json.Deserialize<List<Decision>>(File.ReadAllText(journal, Encoding.UTF8)) ?? new List<Decision>();
            Scan();
            if (Done.Count > 0) WriteOutputs();
        }

        public string Abs(string rel)
        {
            return Path.Combine(Root, rel);
        }

        public int Total
        {
            get { return Done.Count + Queue.Count; }
        }

        // После перегенерации у задания в журнале несколько решений, действует последнее.
        public List<Decision> Latest()
        {
            var last = new Dictionary<string, Decision>();
            foreach (Decision d in Done) last[d.Group + "/" + d.Id] = d;
            return last.Values.ToList();
        }

        public int RegenCount
        {
            get { return Latest().Count(d => d.None); }
        }

        public List<StatusRow> BuildStatus()
        {
            var tried = new Dictionary<string, SortedSet<string>>();
            foreach (Decision d in Done)
            {
                string key = d.Group + "/" + d.Id;
                SortedSet<string> names;
                if (!tried.TryGetValue(key, out names))
                    tried[key] = names = new SortedSet<string>(Comparer<string>.Create(StrCmpLogicalW));
                foreach (FileMove m in d.Moves) names.Add(VariantName(m.From));
            }
            return Latest().Select(d =>
            {
                Info i;
                info.TryGetValue(d.Id, out i);
                return new StatusRow
                {
                    id = d.Id,
                    group = d.Group,
                    ru = i != null ? i.Ru : null,
                    state = d.None ? StatusRow.Regen : StatusRow.Ready,
                    picked = d.Picked,
                    tried = tried[d.Group + "/" + d.Id].ToList(),
                    note = d.Note,
                    at = d.At,
                    pc = PcId
                };
            }).ToList();
        }

        public static bool IsKit(string dir)
        {
            return File.Exists(Path.Combine(dir, "jobs.json")) ||
                   (Directory.Exists(Path.Combine(dir, "datasets")) && Directory.Exists(Path.Combine(dir, "scripts")));
        }

        // Папка результатов для того, что запустили или выбрали: она сама или results внутри неё.
        public static string ResultsOf(string dir)
        {
            if (!IsKit(dir) && LooksLikeResults(dir)) return dir;
            string nested = Path.Combine(dir, "results");
            return LooksLikeResults(nested) ? nested : null;
        }

        public static bool LooksLikeResults(string dir)
        {
            if (File.Exists(Path.Combine(dir, JournalFile))) return true;
            if (!Directory.Exists(dir)) return false;
            return Directory.GetDirectories(dir)
                .Where(g => !IsServiceDir(Path.GetFileName(g)))
                .SelectMany(Directory.GetDirectories)
                .Any(j => Directory.GetFiles(j, "*.png").Length > 0);
        }

        static bool IsServiceDir(string name)
        {
            return name.StartsWith("_", StringComparison.Ordinal)
                || name.Equals("compare", StringComparison.OrdinalIgnoreCase)
                || name.Equals("logs", StringComparison.OrdinalIgnoreCase);
        }

        void LoadInfo()
        {
            string path = Path.Combine(Path.GetDirectoryName(Root.TrimEnd('\\')) ?? Root, "jobs.json");
            if (!File.Exists(path)) return;
            try
            {
                var root = (Dictionary<string, object>)json.DeserializeObject(File.ReadAllText(path, Encoding.UTF8));
                int order = 0;
                foreach (Dictionary<string, object> j in (object[])root["jobs"])
                {
                    info[Text(j, "id")] = new Info
                    {
                        Ru = Text(j, "ru"),
                        Where = Text(j, "where"),
                        Asset = Text(j, "asset"),
                        Order = order++
                    };
                }
            }
            catch (Exception e)
            {
                Warning = "Не прочитан jobs.json, подписи заданий не показаны: " + e.Message;
            }
        }

        static string Text(Dictionary<string, object> j, string key)
        {
            object v;
            return j.TryGetValue(key, out v) && v != null ? v.ToString() : null;
        }

        void Scan()
        {
            foreach (string g in Directory.GetDirectories(Root))
            {
                string group = Path.GetFileName(g);
                if (IsServiceDir(group)) continue;
                foreach (string d in Directory.GetDirectories(g))
                {
                    Job job = BuildJob(group, Path.GetFileName(d));
                    if (job.Variants.Count > 0) Queue.Add(job);
                }
            }
            Queue.Sort(CompareJobs);
        }

        static int CompareJobs(Job a, Job b)
        {
            int c = a.Section.Index.CompareTo(b.Section.Index);
            if (c == 0) c = a.SectionOrder.CompareTo(b.SectionOrder);
            if (c == 0) c = StrCmpLogicalW(a.Group, b.Group);
            if (c == 0) c = a.Order.CompareTo(b.Order);
            return c != 0 ? c : StrCmpLogicalW(a.Id, b.Id);
        }

        Job BuildJob(string group, string id)
        {
            Placement place = SectionMap.Of(id);
            var job = new Job { Group = group, Id = id, Section = place.Section, SectionOrder = place.Order, Goes = place.Goes };
            Info i;
            if (info.TryGetValue(id, out i))
            {
                job.Ru = i.Ru;
                job.Where = i.Where;
                job.Asset = i.Asset;
                job.Order = i.Order;
                if (job.Goes == null) job.Goes = i.Where;
            }
            string dir = Abs(Path.Combine(group, id));
            if (!Directory.Exists(dir)) return job;
            var byName = new Dictionary<string, Variant>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in Directory.GetFiles(dir, "*.png"))
            {
                string key = VariantName(file);
                Variant v;
                if (!byName.TryGetValue(key, out v)) byName[key] = v = new Variant { Name = key };
                string rel = Path.Combine(group, id, Path.GetFileName(file));
                if (IsCut(file)) v.Cut = rel; else v.Raw = rel;
            }
            job.Variants = byName.Values.ToList();
            job.Variants.Sort((a, b) => StrCmpLogicalW(a.Name, b.Name));
            return job;
        }

        static bool IsCut(string file)
        {
            return VariantName(file).Length < Path.GetFileNameWithoutExtension(file).Length;
        }

        static string VariantName(string file)
        {
            string name = Path.GetFileNameWithoutExtension(file);
            return name.EndsWith("_alpha", StringComparison.OrdinalIgnoreCase)
                ? name.Substring(0, name.Length - "_alpha".Length)
                : name;
        }

        // Отмеченные варианты — в _Нужные, остальные — в _Ненужные. Пустой picked значит «ни один не подошёл».
        // Файлы не перезаписываются: при совпадении имени новый получает суффикс. При ошибке всё возвращается назад.
        public void Decide(Job job, ICollection<string> picked, string note = null)
        {
            var moves = new List<FileMove>();
            var decision = new Decision
            {
                Group = job.Group,
                Id = job.Id,
                Picked = picked.ToList(),
                None = picked.Count == 0,
                Moves = moves,
                At = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Note = string.IsNullOrEmpty(note) ? null : note
            };
            try
            {
                foreach (Variant v in job.Variants)
                {
                    string target = picked.Contains(v.Name) ? NeedDir : TrashDir;
                    foreach (string rel in v.Files)
                    {
                        string to = FreeName(Path.Combine(target, job.Group, job.Id, Path.GetFileName(rel)));
                        Directory.CreateDirectory(Path.GetDirectoryName(Abs(to)));
                        Move(Abs(rel), Abs(to));
                        moves.Add(new FileMove { From = rel, To = to });
                    }
                }
                Done.Add(decision);
                SaveJournal();
            }
            catch
            {
                Done.Remove(decision);
                MoveBack(moves);
                throw;
            }
            Queue.Remove(job);
            WriteOutputs();
        }

        public Decision Undo()
        {
            return Done.Count == 0 ? null : Reopen(Done[Done.Count - 1]);
        }

        // Отменяет решение из любого места журнала: файлы возвращаются в папку задания, оно становится первым в очереди.
        public Decision Reopen(Decision d)
        {
            int index = Done.IndexOf(d);
            var restored = new List<FileMove>();
            try
            {
                foreach (FileMove m in Enumerable.Reverse(d.Moves))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Abs(m.From)));
                    Move(Abs(m.To), Abs(m.From));
                    restored.Add(m);
                }
                Done.RemoveAt(index);
                SaveJournal();
            }
            catch
            {
                if (!Done.Contains(d)) Done.Insert(index, d);
                foreach (FileMove m in restored) Move(Abs(m.From), Abs(m.To));
                throw;
            }
            foreach (FileMove m in d.Moves) RemoveEmptyDirs(Path.GetDirectoryName(m.To));
            // После перегенерации в очереди уже могут ждать новые варианты этого задания.
            Queue.RemoveAll(j => j.Group == d.Group && j.Id == d.Id);
            Queue.Insert(0, BuildJob(d.Group, d.Id));
            WriteOutputs();
            return d;
        }

        // Отложенное задание ждёт конца своего раздела, а если оно в разделе последнее — конца очереди.
        public void Skip()
        {
            if (Queue.Count < 2) return;
            Job job = Queue[0];
            Queue.RemoveAt(0);
            int last = Queue.FindLastIndex(j => j.Section == job.Section);
            Queue.Insert(last >= 0 ? last + 1 : Queue.Count, job);
        }

        // Очередь сдвигается по кругу, поэтому после выбранного раздела идут следующие, а затем пропущенные.
        public bool JumpTo(Section section)
        {
            Queue.Sort(CompareJobs);
            int first = Queue.FindIndex(j => j.Section == section);
            if (first < 0) return false;
            List<Job> before = Queue.GetRange(0, first);
            Queue.RemoveRange(0, first);
            Queue.AddRange(before);
            return true;
        }

        // Решения раздела в порядке очереди. Файлы берутся из журнала: имя могло получить суффикс при совпадении.
        public List<ReviewItem> Review(Section section)
        {
            var items = new List<ReviewItem>();
            foreach (Decision d in Ordered().Where(x => SectionMap.Of(x.Id).Section == section))
            {
                Info i;
                info.TryGetValue(d.Id, out i);
                string ru = i != null ? i.Ru : null;
                if (d.None) items.Add(new ReviewItem { Decision = d, Ru = ru });
                foreach (string name in d.Picked)
                {
                    var variant = new Variant { Name = name };
                    foreach (FileMove m in d.Moves.Where(x => VariantName(x.From).Equals(name, StringComparison.OrdinalIgnoreCase)))
                    {
                        if (IsCut(m.From)) variant.Cut = m.To; else variant.Raw = m.To;
                    }
                    items.Add(new ReviewItem { Decision = d, Ru = ru, Variant = variant });
                }
            }
            return items;
        }

        public List<SectionStat> Stats()
        {
            var stats = new Dictionary<Section, SectionStat>();
            Func<Section, SectionStat> stat = section =>
            {
                SectionStat found;
                if (!stats.TryGetValue(section, out found)) stats[section] = found = new SectionStat { Section = section };
                return found;
            };
            var waiting = new HashSet<string>();
            foreach (Job job in Queue)
            {
                waiting.Add(job.Group + "/" + job.Id);
                SectionStat row = stat(job.Section);
                row.Total++;
                row.Left++;
            }
            foreach (Decision d in Latest())
                if (!waiting.Contains(d.Group + "/" + d.Id)) stat(SectionMap.Of(d.Id).Section).Total++;
            return stats.Values.OrderBy(s => s.Section.Index).ToList();
        }

        void MoveBack(List<FileMove> moves)
        {
            foreach (FileMove m in Enumerable.Reverse(moves))
            {
                Move(Abs(m.To), Abs(m.From));
                RemoveEmptyDirs(Path.GetDirectoryName(m.To));
            }
            moves.Clear();
        }

        void RemoveEmptyDirs(string rel)
        {
            while (!string.IsNullOrEmpty(rel))
            {
                string dir = Abs(rel);
                if (!Directory.Exists(dir)) { rel = Path.GetDirectoryName(rel); continue; }
                if (Directory.EnumerateFileSystemEntries(dir).Any()) return;
                Directory.Delete(dir);
                rel = Path.GetDirectoryName(rel);
            }
        }

        string FreeName(string rel)
        {
            if (!File.Exists(Abs(rel))) return rel;
            string dir = Path.GetDirectoryName(rel);
            string name = Path.GetFileNameWithoutExtension(rel);
            string ext = Path.GetExtension(rel);
            for (int n = 2; ; n++)
            {
                string candidate = Path.Combine(dir, name + " (" + n + ")" + ext);
                if (!File.Exists(Abs(candidate))) return candidate;
            }
        }

        // Антивирус и превью проводника иногда держат файл доли секунды.
        static void Retry(Action action)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    action();
                    return;
                }
                catch (FileNotFoundException) { throw; }
                catch (DirectoryNotFoundException) { throw; }
                catch (IOException)
                {
                    if (attempt >= 4) throw;
                }
                catch (UnauthorizedAccessException)
                {
                    if (attempt >= 4) throw;
                }
                Thread.Sleep(100);
            }
        }

        static void Move(string from, string to)
        {
            Retry(() => File.Move(from, to));
        }

        void SaveJournal()
        {
            string path = Abs(JournalFile);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, json.Serialize(Done), new UTF8Encoding(false));
            Retry(() =>
            {
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            });
        }

        // Эти файлы могут быть открыты в Excel или Блокноте: сортировку это не останавливает.
        void WriteOutputs()
        {
            try
            {
                List<Decision> ordered = Ordered();

                var choice = new StringBuilder("Раздел;Что это;Куда идёт;Ассет;Группа;Задание;Выбранные варианты\r\n");
                foreach (Decision d in ordered.Where(x => !x.None))
                {
                    Info i;
                    info.TryGetValue(d.Id, out i);
                    Placement place = SectionMap.Of(d.Id);
                    choice.Append(string.Join(";", new[]
                    {
                        Csv(place.Section.Kind + ": " + place.Section.Name), Csv(i != null ? i.Ru : ""),
                        Csv(place.Goes ?? (i != null ? i.Where : "")), Csv(i != null ? i.Asset : ""),
                        Csv(d.Group), Csv(d.Id), Csv(string.Join(", ", d.Picked))
                    })).Append("\r\n");
                }
                File.WriteAllText(Abs(ChoiceFile), choice.ToString(), new UTF8Encoding(true));

                var regen = new StringBuilder();
                foreach (Decision d in ordered.Where(x => x.None))
                {
                    Info i;
                    info.TryGetValue(d.Id, out i);
                    regen.Append(d.Id).Append('\t').Append(i != null ? i.Ru : "").Append('\t').Append(d.Note).Append("\r\n");
                }
                File.WriteAllText(Abs(RegenFile), regen.ToString(), new UTF8Encoding(true));
                File.WriteAllText(Abs(StatusFile), StatusDoc.Format(BuildStatus(), Queue.Count), new UTF8Encoding(false));
                Warning = null;
            }
            catch (IOException e)
            {
                Warning = "Списки не обновились (файл открыт в другой программе?): " + e.Message;
            }
            catch (UnauthorizedAccessException e)
            {
                Warning = "Списки не обновились: " + e.Message;
            }
        }

        List<Decision> Ordered()
        {
            Comparer<string> logical = Comparer<string>.Create(StrCmpLogicalW);
            return Latest()
                .OrderBy(d => SectionMap.Of(d.Id).Section.Index)
                .ThenBy(d => SectionMap.Of(d.Id).Order)
                .ThenBy(d => d.Group, logical)
                .ThenBy(d => OrderOf(d.Id))
                .ThenBy(d => d.Id, logical)
                .ToList();
        }

        int OrderOf(string id)
        {
            Info i;
            return info.TryGetValue(id, out i) ? i.Order : int.MaxValue;
        }

        static string Csv(string s)
        {
            return "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
        }
    }
}
