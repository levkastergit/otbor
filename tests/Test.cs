using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace Otbor
{
    static class SelfTest
    {
        const int WM_KEYDOWN = 0x100, WM_KEYUP = 0x101;
        const int WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_LBUTTONDBLCLK = 0x203, WM_RBUTTONDOWN = 0x204, WM_RBUTTONUP = 0x205;
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, int m, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int m, IntPtr w, IntPtr l);

        static int passed, failed;
        static StreamWriter log;

        static void Say(string s) { log.WriteLine(s); log.Flush(); }
        static void Check(bool ok, string name)
        {
            if (ok) passed++; else failed++;
            Say((ok ? "ok    " : "FAIL  ") + name);
        }

        static void Png(string path, int w, int h, Color c)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var b = new Bitmap(w, h))
            using (var g = Graphics.FromImage(b))
            {
                g.Clear(c);
                b.Save(path, ImageFormat.Png);
            }
        }

        // Те же имена, что делает generate.py: <id>__<mode>_<n>.png и *_alpha.png, рядом meta.json
        static void MakeJob(string root, string group, string id, bool alpha, int w = 64, int h = 64)
        {
            string dir = Path.Combine(root, group, id);
            int k = 0;
            foreach (string mode in new[] { "ref", "t2i" })
                for (int n = 1; n <= 4; n++, k++)
                {
                    Color c = Color.FromArgb(40 + k * 20, 200 - k * 15, 90);
                    Png(Path.Combine(dir, id + "__" + mode + "_" + n + ".png"), w, h, c);
                    if (alpha) Png(Path.Combine(dir, id + "__" + mode + "_" + n + "_alpha.png"), w / 2, h / 2, c);
                }
            File.WriteAllText(Path.Combine(dir, "meta.json"), "{}");
        }

        static string Fresh(string name)
        {
            string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "work", name);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            string results = Path.Combine(dir, "results");
            Directory.CreateDirectory(results);
            File.WriteAllText(Path.Combine(dir, "jobs.json"),
                "{\"note\":\"x\",\"jobs\":[" +
                "{\"id\":\"zz_first\",\"ru\":\"Папа \\\"стоит\\\"; базовый\",\"where\":\"П0\",\"asset\":\"T_First\",\"group\":\"30_a\"}," +
                "{\"id\":\"aa_second\",\"ru\":\"Второй\",\"asset\":\"T_Second\",\"group\":\"30_a\"}]}", new UTF8Encoding(false));
            return results;
        }

        static int CountPng(string dir)
        {
            return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.png", SearchOption.AllDirectories).Length : 0;
        }

        static string Snapshot(string root)
        {
            return string.Join("\n", Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".png") || f.EndsWith(".json"))
                .Select(f => f.Substring(root.Length) + "=" + new FileInfo(f).Length)
                .OrderBy(x => x, StringComparer.Ordinal));
        }

        static void TestCore()
        {
            string root = Fresh("core");
            MakeJob(root, "30_a", "aa_second", true);
            MakeJob(root, "30_a", "zz_first", true);
            MakeJob(root, "20_bg", "bg_room", false);
            MakeJob(root, "01_old", "not_in_jobs", true);
            Png(Path.Combine(root, "compare", "x", "skip.png"), 8, 8, Color.Red);
            Png(Path.Combine(root, "logs", "x", "skip.png"), 8, 8, Color.Red);
            Png(Path.Combine(root, "_Нужные", "99_g", "old", "skip.png"), 8, 8, Color.Red);
            File.WriteAllText(Path.Combine(root, "index.html"), "x");
            string snapshotAll = Snapshot(root);

            Check(Session.LooksLikeResults(root), "папка распознана как results");
            Check(!Session.LooksLikeResults(Path.GetDirectoryName(root)), "родительская папка не принята за results");

            var s = new Session(root + "\\");
            Check(s.Warning == null, "jobs.json прочитан при пути с \\ на конце");
            Check(s.Queue.Count == 4, "найдено 4 задания, служебные папки пропущены: " + s.Queue.Count);
            Check(string.Join(",", s.Queue.Select(j => j.Id)) == "not_in_jobs,bg_room,zz_first,aa_second",
                "порядок: группы, внутри — как в jobs.json: " + string.Join(",", s.Queue.Select(j => j.Id)));
            Job first = s.Queue.First(j => j.Id == "zz_first");
            Check(first.Variants.Count == 8, "8 вариантов, а не 16 файлов");
            Check(first.Variants.All(v => v.Raw != null && v.Cut != null), "сырой и вырезанный файлы в паре");
            Check(first.Variants[0].Label == "ref_1" && first.Variants[7].Label == "t2i_4", "варианты по порядку");
            Check(first.Ru.Contains("стоит") && first.Asset == "T_First", "подпись из jobs.json");
            Check(s.Queue.First(j => j.Id == "bg_room").Variants.All(v => v.Cut == null && v.Raw != null), "фон без alpha");

            string dir = Path.Combine(root, "30_a", "zz_first");
            s.Decide(first, new[] { "zz_first__ref_2" });
            Check(CountPng(dir) == 0, "из папки задания убраны все картинки");
            Check(File.Exists(Path.Combine(dir, "meta.json")), "meta.json остался на месте");
            string need = Path.Combine(root, Session.NeedDir, "30_a", "zz_first");
            string trash = Path.Combine(root, Session.TrashDir, "30_a", "zz_first");
            Check(CountPng(need) == 2 && File.Exists(Path.Combine(need, "zz_first__ref_2.png")) &&
                  File.Exists(Path.Combine(need, "zz_first__ref_2_alpha.png")), "нужный вариант с alpha в _Нужные");
            Check(CountPng(trash) == 14, "7 ненужных вариантов в _Ненужные");

            s = new Session(root);
            Check(s.Queue.All(j => j.Id != "zz_first") && s.Done.Count == 1, "после перезапуска разобранное не возвращается");
            string csv = File.ReadAllText(Path.Combine(root, Session.ChoiceFile), Encoding.UTF8);
            byte[] bytes = File.ReadAllBytes(Path.Combine(root, Session.ChoiceFile));
            Check(bytes[0] == 0xEF && bytes[1] == 0xBB, "выбор.csv с BOM для Excel");
            Check(csv.Contains("\"zz_first__ref_2\"") && csv.Contains("\"Папа \"\"стоит\"\"; базовый\"") && csv.Contains("T_First"),
                "выбор.csv: вариант, кавычки и ; экранированы, ассет есть");

            Decision d = s.Undo();
            Check(d != null && d.Id == "zz_first" && s.Queue[0].Id == "zz_first", "«Назад» работает после перезапуска");
            Check(string.Join("\n", Snapshot(root).Split('\n').Where(x => !x.StartsWith("\\_отбор.json") && !x.StartsWith("\\_статус.json")))
                  == snapshotAll.Replace("\r", ""), "после «Назад» файлы ровно как были");
            Check(!Directory.Exists(need) && !Directory.Exists(trash), "пустые папки убраны");
            Check(Directory.Exists(Path.Combine(root, "_Нужные", "99_g", "old")), "чужие файлы в _Нужные не тронуты");

            Job bg = s.Queue.First(j => j.Id == "bg_room");
            s.Decide(bg, new string[0]);
            Check(CountPng(Path.Combine(root, Session.TrashDir, "20_bg", "bg_room")) == 8, "«ни один»: всё в _Ненужные");
            Check(File.ReadAllText(Path.Combine(root, Session.RegenFile), Encoding.UTF8).Contains("bg_room"), "задание в перегенерировать.txt");
            Check(!File.ReadAllText(Path.Combine(root, Session.ChoiceFile), Encoding.UTF8).Contains("bg_room"), "его нет в выбор.csv");

            // совпадение имён: существующий файл не затирается
            root = Fresh("collision");
            MakeJob(root, "30_a", "zz_first", true);
            string clash = Path.Combine(root, Session.NeedDir, "30_a", "zz_first", "zz_first__ref_1.png");
            Directory.CreateDirectory(Path.GetDirectoryName(clash));
            File.WriteAllText(clash, "ОРИГИНАЛ");
            s = new Session(root);
            s.Decide(s.Queue[0], new[] { "zz_first__ref_1" });
            Check(File.ReadAllText(clash) == "ОРИГИНАЛ", "файл с тем же именем не перезаписан");
            Check(File.Exists(Path.Combine(Path.GetDirectoryName(clash), "zz_first__ref_1 (2).png")), "новый получил суффикс (2)");
            s.Undo();
            Check(File.ReadAllText(clash) == "ОРИГИНАЛ" && CountPng(Path.Combine(root, "30_a", "zz_first")) == 16, "«Назад» вернул только своё");

            // файл занят: ничего не теряется
            root = Fresh("locked");
            MakeJob(root, "30_a", "zz_first", true);
            string before = Snapshot(root);
            s = new Session(root);
            Job job = s.Queue[0];
            using (new FileStream(Path.Combine(root, "30_a", "zz_first", "zz_first__t2i_3_alpha.png"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                bool threw = false;
                try { s.Decide(job, new[] { "zz_first__ref_1" }); }
                catch (IOException) { threw = true; }
                Check(threw, "занятый файл даёт ошибку");
            }
            Check(Snapshot(root) == before, "при ошибке всё откатилось на исходные места");
            Check(s.Done.Count == 0 && s.Queue.Contains(job), "задание осталось в очереди");
            Check(!Directory.Exists(Path.Combine(root, Session.NeedDir)), "не осталось пустых папок");
            s.Decide(job, new[] { "zz_first__ref_1" });
            Check(s.Done.Count == 1, "после освобождения файла всё проходит");

            // «Назад» при занятом месте: выбор сохраняется
            string blocker = Path.Combine(root, "30_a", "zz_first", "zz_first__t2i_1.png");
            File.WriteAllText(blocker, "новая генерация");
            bool failedUndo = false;
            try { s.Undo(); }
            catch (IOException) { failedUndo = true; }
            Check(failedUndo && s.Done.Count == 1, "«Назад» не затирает появившийся файл");
            Check(CountPng(Path.Combine(root, Session.NeedDir, "30_a", "zz_first")) == 2 &&
                  CountPng(Path.Combine(root, Session.TrashDir, "30_a", "zz_first")) == 14, "после неудачного «Назад» разбор цел");

            // выбор.csv открыт в Excel: сортировка не останавливается
            root = Fresh("excel");
            MakeJob(root, "30_a", "zz_first", true);
            MakeJob(root, "30_a", "aa_second", true);
            s = new Session(root);
            s.Decide(s.Queue[0], new[] { "zz_first__ref_1" });
            using (new FileStream(Path.Combine(root, Session.ChoiceFile), FileMode.Open, FileAccess.Read, FileShare.None))
                s.Decide(s.Queue[0], new[] { "aa_second__ref_1" });
            Check(s.Done.Count == 2 && s.Warning != null, "занятый выбор.csv — только предупреждение");
            s.Skip();
            s = new Session(root);
            Check(s.Done.Count == 2, "журнал записан");

            // порча журнала не должна молча обнулять разбор
            root = Fresh("badjournal");
            MakeJob(root, "30_a", "zz_first", true);
            File.WriteAllText(Path.Combine(root, Session.JournalFile), "{битый");
            bool refused = false;
            try { new Session(root); } catch (Exception) { refused = true; }
            Check(refused && File.ReadAllText(Path.Combine(root, Session.JournalFile)) == "{битый", "битый журнал не перезаписан");
        }

        static IEnumerable<T> Find<T>(Control c) where T : Control
        {
            foreach (Control x in c.Controls)
            {
                if (x is T) yield return (T)x;
                foreach (T y in Find<T>(x)) yield return y;
            }
        }

        static List<Tile> Tiles(Form f) { return Find<Tile>(f).OrderBy(t => t.Number).ToList(); }

        static void Pump(int ms)
        {
            var sw = Stopwatch.StartNew();
            do { Application.DoEvents(); Thread.Sleep(2); } while (sw.ElapsedMilliseconds < ms);
        }

        static void WaitImages(Form f)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 8000)
            {
                Application.DoEvents();
                if (Tiles(f).All(t => t.Image != null)) return;
                Thread.Sleep(5);
            }
            throw new Exception("картинки не загрузились");
        }

        static void Key(Form f, Keys k, long lparam = 1)
        {
            PostMessage(f.Handle, WM_KEYDOWN, (IntPtr)(int)k, (IntPtr)lparam);
            PostMessage(f.Handle, WM_KEYUP, (IntPtr)(int)k, (IntPtr)0xC0000001L);
            Pump(25);
        }

        static void Mouse(Control c, int down, int up)
        {
            var at = (IntPtr)((20 << 16) | 20);
            SendMessage(c.Handle, down, (IntPtr)1, at);
            SendMessage(c.Handle, up, IntPtr.Zero, at);
        }

        [DllImport("user32.dll")] static extern void mouse_event(int flags, int dx, int dy, int data, IntPtr extra);

        // Те же сообщения, что шлёт настоящий клик, с координатами центра элемента.
        static void RealClick(Control c, int times = 1)
        {
            c.FindForm().TopMost = true;
            Pump(60);
            var at = (IntPtr)(((c.Height / 2) << 16) | (c.Width / 2));
            for (int i = 0; i < times && !c.IsDisposed; i++)
            {
                SendMessage(c.Handle, i == 0 ? WM_LBUTTONDOWN : WM_LBUTTONDBLCLK, (IntPtr)1, at);
                SendMessage(c.Handle, WM_LBUTTONUP, IntPtr.Zero, at);
            }
            Pump(60);
        }

        static Label Title(Form f)
        {
            return Find<Label>(f).First(l => l.Font.Size > 15 && l.Dock == DockStyle.Top);
        }

        static void TestUi()
        {
            string root = Fresh("ui");
            for (int i = 0; i < 60; i++) MakeJob(root, "40_props", "job_" + i.ToString("00"), true, 400, 400);
            var s = new Session(root);
            var form = new MainForm(s, Offline());
            string askedFor = null;
            form.AskNote = j => { askedFor = j.Id; return "лицо не то"; };
            form.WindowState = FormWindowState.Normal;
            form.Size = new Size(1280, 800);
            form.Show();
            Pump(100);
            WaitImages(form);

            Check(Tiles(form).Count == 8, "окно показывает 8 плиток");
            Check(Tiles(form).All(t => t.Transparent), "по умолчанию вырезанные на шахматке");
            var r = Tiles(form).Select(t => t.Bounds).ToList();
            var grid = Tiles(form)[0].Parent.ClientRectangle;
            Check(r.All(x => grid.Contains(x)), "все плитки внутри окна");
            Check(!r.Any(a => r.Any(b => a != b && a.IntersectsWith(b))), "плитки не перекрываются");

            Key(form, Keys.Enter);
            Check(s.Done.Count == 0, "Enter без отметки ничего не переносит");
            Key(form, Keys.Space);
            Check(s.Done.Count == 0, "пробел без отметки ничего не переносит");

            Key(form, Keys.D2);
            Check(Tiles(form)[1].Marked, "цифра 2 отмечает вариант 2");
            Key(form, Keys.Enter);
            Check(s.Done.Count == 1 && s.Done[0].Picked.SequenceEqual(new[] { "job_00__ref_2" }), "Enter переносит и идёт дальше");
            Pump(20);
            WaitImages(form);
            Check(Tiles(form)[0].V.Name.StartsWith("job_01"), "показано следующее задание");

            Mouse(Tiles(form)[2], WM_LBUTTONDOWN, WM_LBUTTONUP);
            Check(Tiles(form)[2].Marked, "клик отмечает");
            Mouse(Tiles(form)[2], WM_LBUTTONDOWN, WM_LBUTTONUP);
            Check(!Tiles(form)[2].Marked, "повторный клик снимает");
            Mouse(Tiles(form)[0], WM_LBUTTONDOWN, WM_LBUTTONUP);
            Mouse(Tiles(form)[5], WM_LBUTTONDOWN, WM_LBUTTONUP);
            RealClick(Find<FlatButton>(form).First(b => b.Text.StartsWith("Далее")));
            Check(s.Done.Count == 2 && s.Done[1].Picked.Count == 2, "можно выбрать два варианта, кнопка «Далее»");
            WaitImages(form);

            RealClick(Tiles(form)[3], 2);
            Pump(40);
            Check(s.Done.Count == 3 && s.Done[2].Picked.SequenceEqual(new[] { "job_02__ref_4" }), "двойной клик выбирает и идёт дальше: " + string.Join(",", s.Done[s.Done.Count - 1].Picked));
            Check(Tiles(form)[0].V.Name.StartsWith("job_03"), "двойной клик продвинул ровно на одно задание");
            WaitImages(form);

            Key(form, Keys.Back);
            Pump(20);
            Check(s.Done.Count == 2 && Tiles(form)[3].Marked && Tiles(form)[3].V.Name.StartsWith("job_02"), "Backspace вернул задание с отметкой");
            Check(CountPng(Path.Combine(root, "40_props", "job_02")) == 16, "файлы вернулись");
            Key(form, Keys.Back, 0x40000001L);
            Check(s.Done.Count == 2, "зажатый Backspace не отматывает пачкой");
            Key(form, Keys.Enter);
            Pump(20);
            Check(s.Done.Count == 3, "Enter после «Назад» подтверждает отметку");
            WaitImages(form);

            string titleBefore = Title(form).Text;
            Key(form, Keys.D0);
            Check(s.Done.Count == 4 && s.Done[3].None, "0 — ни один не подходит");
            Check(askedFor == "job_03" && s.Done[3].Note == "лицо не то", "0 спрашивает, что не так, и сохраняет ответ");
            Check(CountPng(Path.Combine(root, Session.NeedDir, "40_props", "job_03")) == 0, "в _Нужные ничего не попало");
            WaitImages(form);

            form.AskNote = j => null;
            Key(form, Keys.D0);
            Check(s.Done.Count == 4 && s.Queue[0].Id == "job_04", "отмена в окне вопроса оставляет задание на месте");
            form.AskNote = j => "";
            WaitImages(form);

            Job before = s.Queue[0];
            Key(form, Keys.S);
            Check(s.Done.Count == 4 && s.Queue[0] != before && s.Queue.Last() == before, "S откладывает в конец");
            Check(CountPng(Path.Combine(root, "40_props", before.Id)) == 16, "отложенное не тронуто");
            WaitImages(form);

            Key(form, Keys.A);
            WaitImages(form);
            Pump(120);
            Check(Tiles(form).All(t => !t.Transparent), "A показывает файлы как есть");
            Key(form, Keys.A);
            Pump(120);
            WaitImages(form);
            Check(Tiles(form).All(t => t.Transparent), "A возвращает вырезанные");

            ZoomView zoom = Find<ZoomView>(form).First();
            Mouse(Tiles(form)[1], WM_RBUTTONDOWN, WM_RBUTTONUP);
            Pump(20);
            Check(zoom.Visible && zoom.Shown == Tiles(form)[1], "правый клик открывает крупно");
            Key(form, Keys.Right);
            Check(zoom.Shown == Tiles(form)[2], "стрелка листает варианты");
            Key(form, Keys.D3);
            Check(Tiles(form)[2].Marked, "цифра работает в крупном просмотре");
            Key(form, Keys.Escape);
            Check(!zoom.Visible, "Esc закрывает просмотр");
            Key(form, Keys.D3);
            Check(!Tiles(form).Any(t => t.Marked), "отметка снята");

            // быстрый разбор, пока картинки ещё грузятся: окно с ошибкой здесь повесит тест, и его снимет сторож
            int start = s.Done.Count;
            for (int i = 0; i < 40 && s.Queue.Count > 1; i++)
            {
                PostMessage(form.Handle, WM_KEYDOWN, (IntPtr)(int)Keys.D1, (IntPtr)1);
                PostMessage(form.Handle, WM_KEYDOWN, (IntPtr)(int)Keys.Enter, (IntPtr)1);
                Pump(1);
            }
            Pump(100);
            Check(s.Done.Count == start + 40, "40 заданий подряд без ожидания загрузки: " + (s.Done.Count - start));

            while (s.Queue.Count > 0)
            {
                WaitImages(form);
                Key(form, Keys.D1);
                Key(form, Keys.Enter);
                Pump(10);
            }
            Label done = Find<Label>(form).First(l => l.Dock == DockStyle.Fill);
            Check(done.Visible && done.Text.Contains("Всё разобрано"), "экран завершения");
            Check(CountPng(Path.Combine(root, "40_props")) == 0, "неразобранных файлов не осталось");
            Check(CountPng(Path.Combine(root, Session.NeedDir)) + CountPng(Path.Combine(root, Session.TrashDir)) == 60 * 16,
                "ни один файл не потерян: " + (CountPng(Path.Combine(root, Session.NeedDir)) + CountPng(Path.Combine(root, Session.TrashDir))));
            Key(form, Keys.Enter);
            Key(form, Keys.D1);
            RealClick(Find<FlatButton>(form).First(b => b.Text.StartsWith("←")));
            Check(s.Queue.Count == 1 && Tiles(form).Count == 8, "с экрана завершения можно вернуться назад");
            form.Close();
            Pump(50);
        }

        // Картинки успевают загрузиться раньше, чем у окна появится handle: сон это гарантирует.
        static void TestFirstLoad()
        {
            string root = Fresh("firstload");
            MakeJob(root, "40_props", "job_0", true, 64, 64);
            var form = new MainForm(new Session(root), Offline());
            Thread.Sleep(400);
            form.WindowState = FormWindowState.Normal;
            form.Show();
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 2000 && !Tiles(form).All(t => t.Image != null)) Pump(5);
            Check(Tiles(form).All(t => t.Image != null), "первый экран: картинки видны, даже если загрузились раньше окна");
            form.Close();
            Pump(20);
        }

        static void TestButtons()
        {
            string root = Fresh("buttons");
            MakeJob(root, "40_props", "job_0", true, 64, 64);
            MakeJob(root, "40_props", "job_1", true, 64, 64);
            foreach (int width in new[] { 900, 1024, 1280, 1366, 1920 })
            {
                var form = new MainForm(new Session(root), Offline());
                form.WindowState = FormWindowState.Normal;
                form.StartPosition = FormStartPosition.Manual;
                form.Location = Point.Empty;
                form.Size = new Size(width, 700);
                form.TopMost = true;
                form.Show();
                Pump(80);
                var all = Find<FlatButton>(form).ToList();
                bool ok = all.All(b =>
                {
                    Rectangle r = b.RectangleToScreen(b.ClientRectangle);
                    var center = new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
                    return b.Visible && form.ClientRectangle.Contains(form.RectangleToClient(r)) &&
                           (!b.Enabled || WindowFromPoint(center) == b.Handle);
                });
                Check(ok, "окно " + width + ": все кнопки целиком видны и нажимаются");
                if (!ok)
                    foreach (FlatButton b in all)
                        Say("      " + b.Text + " " + form.RectangleToClient(b.RectangleToScreen(b.ClientRectangle)) + " попадание=" +
                            (WindowFromPoint(b.PointToScreen(new Point(b.Width / 2, b.Height / 2))) == b.Handle));
                form.Close();
                Pump(20);
            }
        }

        static GitHubSync Offline()
        {
            var sync = new GitHubSync(null, "test0000");
            sync.Start();
            return sync;
        }

        static void WaitFor(Func<bool> done, int ms = 5000)
        {
            var sw = Stopwatch.StartNew();
            while (!done() && sw.ElapsedMilliseconds < ms) Thread.Sleep(5);
        }

        static StatusRow Row(string id, string state, string pc, string at, string group = "30_a")
        {
            return new StatusRow
            {
                id = id, group = group, state = state, pc = pc, at = at, ru = "Папа «стоит» <&>",
                picked = new List<string>(), tried = new List<string> { id + "__ref_1" }, note = "лицо \"не то\"\nвторая строка"
            };
        }

        // Ответы GitHub Contents API по документации: 404 для нового файла, sha и base64 с переносами строк для имеющегося.
        class FakeGitHub : IDisposable
        {
            readonly HttpListener listener = new HttpListener();
            public readonly string Url;
            public readonly List<string> Calls = new List<string>();
            public readonly List<string> Commits = new List<string>();
            public readonly Queue<Action<HttpListenerResponse>> Faults = new Queue<Action<HttpListenerResponse>>();
            public volatile string Text;
            public string Auth;
            public string Message;
            int version = 1;

            public FakeGitHub()
            {
                var probe = new TcpListener(IPAddress.Loopback, 0);
                probe.Start();
                int port = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
                listener.Prefixes.Add("http://localhost:" + port + "/");
                listener.Start();
                Url = "http://localhost:" + port + "/status.json";
                new Thread(Serve) { IsBackground = true }.Start();
            }

            string Sha { get { return "sha" + version; } }

            void Serve()
            {
                while (true)
                {
                    HttpListenerContext c;
                    try { c = listener.GetContext(); }
                    catch (Exception) { return; }
                    try { Handle(c); }
                    catch (Exception) { }
                }
            }

            static void Reply(HttpListenerResponse r, int status, string body)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(body);
                r.StatusCode = status;
                r.OutputStream.Write(bytes, 0, bytes.Length);
                r.Close();
            }

            void Handle(HttpListenerContext c)
            {
                string method = c.Request.HttpMethod;
                string body;
                using (var reader = new StreamReader(c.Request.InputStream, Encoding.UTF8)) body = reader.ReadToEnd();
                lock (Calls)
                {
                    Calls.Add(method);
                    Auth = c.Request.Headers["Authorization"];
                }
                if (Faults.Count > 0)
                {
                    Faults.Dequeue()(c.Response);
                    return;
                }
                var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                if (method == "GET")
                {
                    if (Text == null)
                    {
                        Reply(c.Response, 404, "{\"message\":\"Not Found\"}");
                        return;
                    }
                    string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(Text), Base64FormattingOptions.InsertLineBreaks).Replace("\r", "");
                    Reply(c.Response, 200, json.Serialize(new Dictionary<string, object> { { "sha", Sha }, { "content", b64 }, { "encoding", "base64" } }));
                    return;
                }
                var put = json.Deserialize<Dictionary<string, object>>(body);
                object sha;
                put.TryGetValue("sha", out sha);
                if (Text != null && (string)sha != Sha)
                {
                    Reply(c.Response, 409, "{\"message\":\"does not match\"}");
                    return;
                }
                Text = Encoding.UTF8.GetString(Convert.FromBase64String((string)put["content"]));
                Message = (string)put["message"];
                version++;
                lock (Calls) Commits.Add(Text);
                Reply(c.Response, 200, "{}");
            }

            public void Dispose()
            {
                listener.Close();
            }
        }

        static GitHubSync Sync(FakeGitHub server, string token = "good-key", string pc = "mine", int quiet = 40, int gap = 60)
        {
            var sync = new GitHubSync(token, pc, server.Url, quiet, gap);
            sync.BackoffBaseSeconds = 1;
            sync.Start();
            return sync;
        }

        static void TestStatus()
        {
            string root = Fresh("status");
            MakeJob(root, "30_a", "zz_first", true);
            MakeJob(root, "30_a", "aa_second", true);
            MakeJob(root, "20_bg", "bg_room", false);
            var s = new Session(root);
            Job bg = s.Queue.First(j => j.Id == "bg_room");
            Job first = s.Queue.First(j => j.Id == "zz_first");
            s.Decide(bg, new string[0], "стены кривые");
            s.Decide(first, new[] { "zz_first__ref_2", "zz_first__t2i_1" });
            List<StatusRow> rows = s.BuildStatus();
            StatusRow regen = rows.First(r => r.id == "bg_room");
            StatusRow ready = rows.First(r => r.id == "zz_first");
            Check(regen.state == StatusRow.Regen && regen.note == "стены кривые", "«ни один» даёт state=regen и комментарий");
            Check(ready.state == StatusRow.Ready && ready.picked.Count == 2 && ready.ru.Contains("стоит"), "выбор даёт state=ready и имена вариантов");
            Check(regen.tried.Count == 8 && regen.tried[0] == "bg_room__ref_1" && regen.tried.Last() == "bg_room__t2i_4",
                "tried — все варианты без _alpha: " + string.Join(",", regen.tried));
            Check(ready.tried.Count == 8 && ready.tried.All(x => !x.EndsWith("_alpha")), "пары файлов считаются одним вариантом");
            Check(ready.pc.Length == 8 && !ready.pc.Contains(Environment.MachineName), "имя компьютера не публикуется: " + ready.pc);

            string local = File.ReadAllText(Path.Combine(root, Session.StatusFile), Encoding.UTF8);
            Check(local.Contains("\"ready\":1") && local.Contains("\"regen\":1") && local.Contains("\"left\":1"), "локальный _статус.json со сводкой");
            Check(StatusDoc.Parse(local).Count == 2, "_статус.json читается обратно");
            Check(!local.Contains(root) && !local.Contains("Users"), "в статусе нет путей ПК");
            Check(local.Split('\n').Count(x => x.StartsWith("  {")) == 2, "по строке на задание");

            string regenText = File.ReadAllText(Path.Combine(root, Session.RegenFile), Encoding.UTF8);
            Check(regenText.Contains("bg_room") && regenText.Contains("стены кривые"), "комментарий в перегенерировать.txt");

            // Перегенерация: новые файлы появились, решение пересмотрено
            string dir = Path.Combine(root, "20_bg", "bg_room");
            Png(Path.Combine(dir, "bg_room__ref_5.png"), 64, 64, Color.Red);
            Png(Path.Combine(dir, "bg_room__ref_6.png"), 64, 64, Color.Red);
            s = new Session(root);
            Job again = s.Queue.First(j => j.Id == "bg_room");
            Check(again.Variants.Count == 2, "после перегенерации задание снова в очереди, только новые варианты");
            Check(s.BuildStatus().First(r => r.id == "bg_room").state == StatusRow.Regen, "пока не разобрано, задание остаётся regen");
            s.Decide(again, new[] { "bg_room__ref_6" });
            StatusRow fixedRow = s.BuildStatus().First(r => r.id == "bg_room");
            Check(fixedRow.state == StatusRow.Ready && fixedRow.note == null, "после нового выбора задание ready");
            Check(fixedRow.tried.Count == 10, "tried включает все раунды: " + fixedRow.tried.Count);
            Check(s.RegenCount == 0, "счётчик перегенерации считает последнее решение");
            Check(!File.ReadAllText(Path.Combine(root, Session.RegenFile), Encoding.UTF8).Contains("bg_room"), "задание ушло из перегенерировать.txt");
            Check(File.ReadAllText(Path.Combine(root, Session.ChoiceFile), Encoding.UTF8).Contains("bg_room__ref_6"), "задание появилось в выбор.csv");
            s.Undo();
            Check(s.BuildStatus().First(r => r.id == "bg_room").state == StatusRow.Regen, "«Назад» возвращает прошлое решение");

            // Журнал старой версии программы, без комментариев
            root = Fresh("oldjournal");
            MakeJob(root, "30_a", "aa_second", true);
            File.WriteAllText(Path.Combine(root, Session.JournalFile),
                "[{\"Group\":\"20_bg\",\"Id\":\"old\",\"Picked\":[],\"None\":true,\"Moves\":[{\"From\":\"20_bg\\\\old\\\\old__ref_1.png\",\"To\":\"_Ненужные\\\\20_bg\\\\old\\\\old__ref_1.png\"}],\"At\":\"2026-11-01 09:00:00\"}]",
                new UTF8Encoding(false));
            s = new Session(root);
            Check(s.BuildStatus().Single().id == "old" && File.Exists(Path.Combine(root, Session.StatusFile)), "журнал старой версии подхватывается, статус создаётся при запуске");

            // Объединение записей разных ПК
            var mine = new List<StatusRow> { Row("a", "ready", "me", "2026-11-01 10:00:00"), Row("b", "regen", "me", "2026-11-01 10:00:00") };
            var theirs = new List<StatusRow>
            {
                Row("b", "ready", "pc2", "2026-11-01 11:00:00"),
                Row("c", "ready", "pc2", "2026-11-01 09:00:00"),
                Row("d", "ready", "me", "2026-11-01 09:00:00"),
                Row("a", "regen", "pc2", "2026-11-01 09:00:00")
            };
            List<StatusRow> merged = StatusDoc.Merge(mine, theirs, "me");
            Func<string, StatusRow> m = id => merged.FirstOrDefault(r => r.id == id);
            Check(m("a").state == "ready", "своя свежая запись остаётся");
            Check(m("b").state == "ready" && m("b").pc == "pc2", "более свежая запись другого ПК важнее");
            Check(m("c") != null, "записи других ПК не теряются");
            Check(m("d") == null, "своя запись, отменённая «Назад», удаляется");
            Check(StatusDoc.Merge(new List<StatusRow>(), theirs, "me").Count == 4, "пустой журнал ничего не удаляет");
            Check(StatusDoc.Merge(new List<StatusRow> { Row("a", "ready", "me", "2026-11-01 08:00:00") },
                new List<StatusRow> { Row("a", "regen", "me", "2026-11-01 10:00:00") }, "me").Single().state == "ready", "свой журнал важнее своей старой записи");
            Check(StatusDoc.Merge(new List<StatusRow>(), new List<StatusRow> { Row("x", "ready", "p", "t", "g1"), Row("x", "regen", "p", "t", "g2") }, "me").Count == 2,
                "одинаковые id в разных группах — разные задания");
            string text = StatusDoc.Format(new[] { Row("z", "ready", "p", "t"), Row("a", "regen", "p", "t") }, 7);
            Check(StatusDoc.Parse(text).First().id == "a" && StatusDoc.Parse(text).First().ru == "Папа «стоит» <&>" && StatusDoc.Parse(text).First().note.Contains("\n"),
                "кириллица, спецсимволы и переносы строк переживают запись и чтение; порядок стабильный");

            // Ключ
            string keyDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "work", "key");
            if (Directory.Exists(keyDir)) Directory.Delete(keyDir, true);
            string savedDir = TokenStore.Dir;
            TokenStore.Dir = keyDir;
            Check(TokenStore.Load() == null, "без файла ключа нет");
            TokenStore.Save("github_pat_ключ");
            Check(TokenStore.Load() == "github_pat_ключ", "ключ сохраняется и читается");
            Check(!File.ReadAllText(Path.Combine(keyDir, "github.key"), Encoding.UTF8).Contains("github_pat"), "на диске ключа в открытом виде нет");
            File.WriteAllText(Path.Combine(keyDir, "github.key"), "мусор");
            Check(TokenStore.Load() == null, "испорченный файл ключа не роняет программу");
            TokenStore.Dir = savedDir;
        }

        static void TestSync()
        {
            var rows = new List<StatusRow> { Row("a", "ready", "mine", "2026-11-01 10:00:00") };
            using (var server = new FakeGitHub())
            {
                GitHubSync sync = Sync(server);
                Check(sync.Label == "GitHub: всё на месте", "начальное состояние");
                sync.Request(rows, 5);
                WaitFor(() => server.Commits.Count == 1);
                Check(server.Commits.Count == 1 && StatusDoc.Parse(server.Text).Single().id == "a", "новый файл создан");
                Check(server.Message == "Отбор: готово 1, перегенерировать 0, осталось 5", "описание записи: " + server.Message);
                Check(server.Auth == "Bearer good-key", "ключ передан в заголовке");
                WaitFor(() => !sync.HasUnsent);
                Check(sync.State == SyncState.Sent && sync.Label.StartsWith("GitHub: отправлено в "), "состояние: " + sync.Label);

                sync.Request(rows, 5);
                WaitFor(() => !sync.HasUnsent);
                Thread.Sleep(30);
                Check(server.Commits.Count == 1, "без изменений новая запись не создаётся");

                // Пачка быстрых решений — одна запись
                int commits = server.Commits.Count;
                Thread.Sleep(100);
                for (int i = 0; i < 20; i++)
                {
                    rows = new List<StatusRow>(rows) { Row("n" + i, "ready", "mine", "2026-11-01 10:00:00") };
                    sync.Request(rows, 5);
                    Thread.Sleep(5);
                }
                WaitFor(() => !sync.HasUnsent && server.Commits.Count > commits);
                Check(server.Commits.Count == commits + 1 && StatusDoc.Parse(server.Text).Count == 21,
                    "20 быстрых решений — одна запись: " + (server.Commits.Count - commits));

                // Кто-то изменил файл между чтением и записью
                Thread.Sleep(100);
                string other = StatusDoc.Format(new[] { Row("foreign", "ready", "pc2", "2026-11-01 09:00:00") }, 0);
                server.Faults.Enqueue(r => { server.Text = other; Reply409(r); });
                rows = new List<StatusRow>(rows) { Row("after", "ready", "mine", "2026-11-01 10:00:00") };
                sync.Request(rows, 5);
                WaitFor(() => !sync.HasUnsent);
                Thread.Sleep(50);
                List<StatusRow> now = StatusDoc.Parse(server.Text);
                Check(now.Any(r => r.id == "foreign") && now.Any(r => r.id == "after"), "конфликт записи: файл перечитан, ничего не потеряно");

                // Лимит GitHub: выполняется Retry-After
                Thread.Sleep(100);
                server.Faults.Enqueue(r =>
                {
                    r.Headers["Retry-After"] = "2";
                    r.StatusCode = 403;
                    byte[] b = Encoding.UTF8.GetBytes("{\"message\":\"You have exceeded a secondary rate limit\"}");
                    r.OutputStream.Write(b, 0, b.Length);
                    r.Close();
                });
                rows = new List<StatusRow>(rows) { Row("limited", "ready", "mine", "2026-11-01 10:00:00") };
                lock (server.Calls) server.Calls.Clear();
                var clock = Stopwatch.StartNew();
                sync.Request(rows, 5);
                WaitFor(() => sync.State == SyncState.Retrying);
                Check(sync.Detail != null && sync.Detail.Contains("подождать"), "лимит показан пользователю: " + sync.Detail);
                sync.SendNow();
                Thread.Sleep(300);
                lock (server.Calls) Check(server.Calls.Count == 1, "«Отправить сейчас» не обходит лимит GitHub");
                WaitFor(() => StatusDoc.Parse(server.Text).Any(r => r.id == "limited"), 6000);
                Check(StatusDoc.Parse(server.Text).Any(r => r.id == "limited") && clock.ElapsedMilliseconds >= 1900,
                    "после паузы данные ушли, прошло мс: " + clock.ElapsedMilliseconds);

                // Закрытие программы: паузы не ждём
                WaitFor(() => !sync.HasUnsent);
                GitHubSync slow = Sync(server, quiet: 600000, gap: 600000);
                rows = new List<StatusRow>(rows) { Row("closing", "ready", "mine", "2026-11-01 10:00:00") };
                slow.Request(rows, 5);
                Check(slow.Flush(5000) && StatusDoc.Parse(server.Text).Any(r => r.id == "closing"), "при закрытии данные уходят сразу");

                // Сбой посреди отправки: данные не теряются, новые решения не затираются
                server.Faults.Enqueue(r => { r.StatusCode = 500; r.Close(); });
                GitHubSync flaky = Sync(server);
                rows = new List<StatusRow>(rows) { Row("flaky", "ready", "mine", "2026-11-01 10:00:00") };
                flaky.Request(rows, 5);
                WaitFor(() => flaky.State == SyncState.Retrying);
                Check(flaky.State == SyncState.Retrying && flaky.HasUnsent, "сбой GitHub: данные ждут повтора");
                WaitFor(() => StatusDoc.Parse(server.Text).Any(r => r.id == "flaky"), 6000);
                Check(StatusDoc.Parse(server.Text).Any(r => r.id == "flaky"), "повтор удался");

                // Отказ в ключе
                server.Faults.Enqueue(r => { r.StatusCode = 401; r.Close(); });
                GitHubSync bad = Sync(server, "expired");
                bad.Request(rows, 5);
                WaitFor(() => bad.State == SyncState.BadKey);
                Check(bad.State == SyncState.BadKey && bad.NeedsKey, "неверный ключ: просит новый: " + bad.Detail);
                lock (server.Calls) server.Calls.Clear();
                Thread.Sleep(200);
                lock (server.Calls) Check(server.Calls.Count == 0, "с неверным ключом запросы прекращаются");
                rows = new List<StatusRow>(rows) { Row("rekey", "ready", "mine", "2026-11-01 10:00:00") };
                bad.Request(rows, 5);
                bad.SetToken("new-key");
                WaitFor(() => StatusDoc.Parse(server.Text).Any(r => r.id == "rekey"));
                Check(server.Auth == "Bearer new-key" && StatusDoc.Parse(server.Text).Any(r => r.id == "rekey"), "новый ключ: отправка возобновилась");

                server.Faults.Enqueue(r => { r.StatusCode = 404; r.Close(); });
                server.Faults.Enqueue(r => { r.StatusCode = 404; r.Close(); });
                GitHubSync readOnly = Sync(server, "read-only");
                rows = new List<StatusRow>(rows) { Row("ro", "ready", "mine", "2026-11-01 10:00:00") };
                readOnly.Request(rows, 5);
                WaitFor(() => readOnly.State == SyncState.BadKey);
                Check(readOnly.Detail != null && readOnly.Detail.Contains("нет права"), "ключ без записи: " + readOnly.Detail);

                // Испорченный файл на GitHub не затирается
                string broken = "{битый";
                server.Text = broken;
                GitHubSync careful = Sync(server);
                careful.Request(rows, 5);
                WaitFor(() => careful.State == SyncState.Retrying);
                Check(careful.State == SyncState.Retrying && server.Text == broken, "испорченный status.json не перезаписан: " + careful.Detail);
            }

            // Нет связи
            GitHubSync offline = new GitHubSync("key", "mine", "http://localhost:1/none", 10, 10);
            offline.BackoffBaseSeconds = 1;
            offline.Start();
            offline.Request(new List<StatusRow> { Row("a", "ready", "mine", "t") }, 0);
            WaitFor(() => offline.State == SyncState.Retrying, 8000);
            Check(offline.State == SyncState.Retrying && offline.HasUnsent, "нет связи: данные ждут, программа работает: " + offline.Detail);
            Check(!offline.Flush(300), "закрытие без связи не виснет");

            GitHubSync noKey = Offline();
            noKey.Request(new List<StatusRow> { Row("a", "ready", "mine", "t") }, 0);
            Check(noKey.State == SyncState.NoKey && noKey.Label == "GitHub: подключить" && noKey.Flush(50) == false && !noKey.HasUnsent, "без ключа окно работает как раньше");
        }

        static void Reply409(HttpListenerResponse r)
        {
            r.StatusCode = 409;
            r.Close();
        }

        static void TestUiSync()
        {
            string root = Fresh("uisync");
            for (int i = 0; i < 4; i++) MakeJob(root, "40_props", "job_" + i, true, 200, 200);
            using (var server = new FakeGitHub())
            {
                var s = new Session(root);
                GitHubSync sync = Sync(server, pc: Session.PcId, quiet: 30, gap: 30);
                var form = new MainForm(s, sync);
                form.AskNote = j => "лицо не то";
                form.WindowState = FormWindowState.Normal;
                form.Size = new Size(1280, 800);
                form.Show();
                Pump(80);
                WaitImages(form);
                FlatButton git = Find<FlatButton>(form).First(b => b.Text.StartsWith("GitHub"));
                Check(git.Bounds.Right <= git.Parent.ClientSize.Width, "кнопка GitHub видна в окне 1280: " + git.Bounds);

                Key(form, Keys.D1);
                Key(form, Keys.Enter);
                Pump(20);
                WaitImages(form);
                Key(form, Keys.D0);
                var clock = Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < 3000 && (server.Text == null || StatusDoc.Parse(server.Text).Count < 2)) Pump(10);
                List<StatusRow> rows = StatusDoc.Parse(server.Text);
                Check(rows.Count == 2 && rows.Any(r => r.state == "ready") && rows.Any(r => r.state == "regen" && r.note == "лицо не то"),
                    "отметки из окна дошли до GitHub: " + rows.Count);
                Pump(100);
                Check(git.Text.StartsWith("GitHub: отправлено в "), "кнопка показывает отправку: " + git.Text);

                WaitImages(form);
                Key(form, Keys.Back);
                clock.Restart();
                while (clock.ElapsedMilliseconds < 3000 && StatusDoc.Parse(server.Text).Count != 1) Pump(10);
                Check(StatusDoc.Parse(server.Text).Single().state == "ready", "«Назад» убирает запись и с GitHub");

                WaitImages(form);
                Key(form, Keys.D0);
                form.Close();
                Pump(30);
                Check(StatusDoc.Parse(server.Text).Count == 2, "последнее решение уходит при закрытии окна");
            }

            // Настоящие диалоги
            using (var note = new NoteDialog("Папа стоит"))
            {
                var t = new System.Windows.Forms.Timer { Interval = 60 };
                t.Tick += (a, b) =>
                {
                    t.Stop();
                    note.Box.Text = "поза не та";
                    PostMessage(note.Box.Handle, WM_KEYDOWN, (IntPtr)(int)Keys.Enter, (IntPtr)1);
                };
                t.Start();
                DialogResult r = note.ShowDialog();
                Check(r == DialogResult.OK && note.Note == "поза не та", "окно комментария: Enter подтверждает");
            }
            using (var note = new NoteDialog("Папа стоит"))
            {
                var t = new System.Windows.Forms.Timer { Interval = 60 };
                t.Tick += (a, b) =>
                {
                    t.Stop();
                    PostMessage(note.Box.Handle, WM_KEYDOWN, (IntPtr)(int)Keys.Escape, (IntPtr)1);
                };
                t.Start();
                Check(note.ShowDialog() == DialogResult.Cancel, "окно комментария: Esc отменяет");
            }
            using (var key = new KeyDialog("GitHub не принял ключ."))
            {
                var t = new System.Windows.Forms.Timer { Interval = 60 };
                bool blocked = false;
                t.Tick += (a, b) =>
                {
                    t.Stop();
                    blocked = !key.Save.Enabled;
                    key.Box.Text = "  github_pat_x  ";
                    PostMessage(key.Box.Handle, WM_KEYDOWN, (IntPtr)(int)Keys.Enter, (IntPtr)1);
                };
                t.Start();
                DialogResult r = key.ShowDialog();
                Check(blocked && r == DialogResult.OK && key.Token == "github_pat_x", "окно ключа: пустой ключ не принимается, пробелы убираются");
            }
        }

        // Запросы к настоящему GitHub. Записи нет: PUT идёт с заведомо неверным ключом.
        static void TestLive()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            string baseUrl = "https://api.github.com/repos/" + GitHubSync.Repo + "/contents/";
            bool notFound = false;
            try { GitHubSync.Call("GET", baseUrl + "no/such/file.json?ref=main", null, null); }
            catch (ApiError e) { notFound = e.Status == 404; }
            Check(notFound, "живой GitHub: нет файла — 404");

            var file = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(
                GitHubSync.Call("GET", baseUrl + "README.md?ref=main", null, null));
            string text = Encoding.UTF8.GetString(Convert.FromBase64String((string)file["content"]));
            Check(((string)file["sha"]).Length == 40 && text.Contains("Otbor"), "живой GitHub: sha и base64 читаются, символов: " + text.Length);

            bool rejected = false;
            string body = "{\"message\":\"test\",\"content\":\"e30=\",\"branch\":\"main\"}";
            try { GitHubSync.Call("PUT", baseUrl + "status/_never.json", "invalid-test-key", body); }
            catch (ApiError e)
            {
                rejected = e.Status == 401;
                Say("      ответ на PUT: " + e.Status + " " + e.Message.Replace("\n", " "));
            }
            Check(rejected, "живой GitHub: PUT с неверным ключом — 401, путь записи рабочий");

            var sync = new GitHubSync("invalid-test-key", "live", GitHubSync.ApiUrl, 10, 10);
            sync.Start();
            sync.Request(new List<StatusRow> { Row("a", "ready", "live", "t") }, 0);
            WaitFor(() => sync.State == SyncState.BadKey, 20000);
            Check(sync.State == SyncState.BadKey, "живой GitHub: программа просит новый ключ: " + sync.Detail);
        }

        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point p);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);

        static void Diag()
        {
            string root = Fresh("diag");
            for (int i = 0; i < 5; i++) MakeJob(root, "40_props", "job_" + i, true, 400, 400);
            var s = new Session(root);
            var form = new MainForm(s, Offline());
            form.WindowState = FormWindowState.Normal;
            form.Size = new Size(1280, 800);
            form.TopMost = true;
            form.Show();
            Pump(200);
            WaitImages(form);
            Say("screen=" + Screen.PrimaryScreen.Bounds + " form=" + form.Bounds);
            Say("foreground=form? " + (GetForegroundWindow() == form.Handle));
            SetForegroundWindow(form.Handle); form.Activate(); Pump(200);
            Say("после Activate foreground=form? " + (GetForegroundWindow() == form.Handle));
            Tile t = Tiles(form)[0];
            Point c = t.PointToScreen(new Point(t.Width / 2, t.Height / 2));
            Cursor.Position = c; Pump(60);
            Say("хотели курсор " + c + ", реально " + Cursor.Position);
            Say("WindowFromPoint=плитка? " + (WindowFromPoint(c) == t.Handle));
            bool before = t.Marked;
            mouse_event(2, 0, 0, 0, IntPtr.Zero); Pump(30); mouse_event(4, 0, 0, 0, IntPtr.Zero); Pump(200);
            Say("реальный клик по плитке: Marked " + before + " -> " + t.Marked);
            var btn = Find<FlatButton>(form).First(b => b.Text.StartsWith("Далее"));
            Point bc = btn.PointToScreen(new Point(btn.Width / 2, btn.Height / 2));
            Say("кнопка на экране " + btn.RectangleToScreen(btn.ClientRectangle) + ", видима=" + btn.Visible + ", enabled=" + btn.Enabled);
            Cursor.Position = bc; Pump(60);
            Say("WindowFromPoint=кнопка? " + (WindowFromPoint(bc) == btn.Handle));
            int done = s.Done.Count;
            mouse_event(2, 0, 0, 0, IntPtr.Zero); Pump(30); mouse_event(4, 0, 0, 0, IntPtr.Zero); Pump(200);
            Say("реальный клик по кнопке: Done " + done + " -> " + s.Done.Count);
            form.Close();
            Pump(30);
        }

        [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr dc, int flags);

        static void Shots(string root, string outDir)
        {
            var s = new Session(root);
            var form = new MainForm(s, Offline());
            form.WindowState = FormWindowState.Normal;
            form.StartPosition = FormStartPosition.Manual;
            form.Location = Point.Empty;
            form.Size = new Size(1920, 1040);
            form.Show();
            Pump(150);
            for (int i = 0; s.Queue.Count > 0; i++)
            {
                WaitImages(form);
                Pump(150);
                if (i == 0) { Key(form, Keys.D2); Pump(60); }
                using (var b = new Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(b, new Rectangle(0, 0, form.Width, form.Height));
                    b.Save(Path.Combine(outDir, "shot_" + i + ".png"), ImageFormat.Png);
                }
                if (i == 0)
                {
                    Key(form, Keys.A);
                    Pump(300);
                    WaitImages(form);
                    Pump(150);
                    using (var b = new Bitmap(form.Width, form.Height))
                    {
                        form.DrawToBitmap(b, new Rectangle(0, 0, form.Width, form.Height));
                        b.Save(Path.Combine(outDir, "shot_raw.png"), ImageFormat.Png);
                    }
                    Key(form, Keys.A);
                    Pump(200);
                    WaitImages(form);
                    Mouse(Tiles(form)[1], WM_RBUTTONDOWN, WM_RBUTTONUP);
                    Pump(300);
                    using (var b = new Bitmap(form.Width, form.Height))
                    {
                        using (var g = Graphics.FromImage(b))
                        {
                            IntPtr dc = g.GetHdc();
                            bool ok = PrintWindow(form.Handle, dc, 2);
                            g.ReleaseHdc(dc);
                            Say("PrintWindow=" + ok);
                        }
                        b.Save(Path.Combine(outDir, "shot_zoom.png"), ImageFormat.Png);
                    }
                    Key(form, Keys.Escape);
                }
                Key(form, Keys.D1);
                Key(form, Keys.Enter);
                Pump(50);
            }
            form.Close();
            Pump(30);
        }

        static void Save(Form f, string path)
        {
            using (var b = new Bitmap(f.Width, f.Height))
            {
                using (var g = Graphics.FromImage(b))
                {
                    IntPtr dc = g.GetHdc();
                    PrintWindow(f.Handle, dc, 2);
                    g.ReleaseHdc(dc);
                }
                b.Save(path, ImageFormat.Png);
            }
        }

        static void Dialogs(string outDir)
        {
            string root = Fresh("dialogs");
            MakeJob(root, "40_props", "job_0", true, 400, 400);
            var form = new MainForm(new Session(root), Offline());
            form.WindowState = FormWindowState.Normal;
            form.Size = new Size(1280, 800);
            form.Show();
            Pump(150);
            WaitImages(form);
            Pump(150);
            Save(form, Path.Combine(outDir, "dlg_main.png"));
            form.Close();
            using (var note = new NoteDialog("Папа стоит, базовая поза"))
            {
                note.StartPosition = FormStartPosition.CenterScreen;
                note.Show();
                Pump(200);
                Save(note, Path.Combine(outDir, "dlg_note.png"));
                note.Close();
            }
            using (var key = new KeyDialog(null))
            {
                key.StartPosition = FormStartPosition.CenterScreen;
                key.Show();
                Pump(200);
                Save(key, Path.Combine(outDir, "dlg_key.png"));
                key.Close();
            }
        }

        [STAThread]
        static int Main(string[] args)
        {
            string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test_log.txt");
            log = new StreamWriter(logPath, false, new UTF8Encoding(false));
            var watchdog = new Thread(() =>
            {
                Thread.Sleep(240000);
                Say("FAIL  сторож: тест завис (скорее всего, окно с ошибкой)");
                Environment.Exit(3);
            });
            watchdog.IsBackground = true;
            watchdog.Start();
            Application.EnableVisualStyles();
            if (args.Length > 0 && args[0] == "diag") { Diag(); return 0; }
            if (args.Length > 2 && args[0] == "shots") { Shots(args[1], args[2]); return 0; }
            if (args.Length > 1 && args[0] == "dialogs") { Dialogs(args[1]); return 0; }
            try
            {
                if (args.Length > 0 && args[0] == "live")
                {
                    Say("--- настоящий GitHub");
                    TestLive();
                }
                else
                {
                    Say("--- логика");
                    TestCore();
                    Say("--- статус");
                    TestStatus();
                    Say("--- отправка");
                    TestSync();
                    if (args.Length == 0)
                    {
                        Say("--- первый экран и кнопки");
                        TestFirstLoad();
                        TestButtons();
                    }
                    Say("--- окно");
                    TestUi();
                    Say("--- окно и отправка");
                    TestUiSync();
                }
            }
            catch (Exception e)
            {
                failed++;
                Say("FAIL  исключение: " + e);
            }
            Say("\nИТОГО: прошло " + passed + ", упало " + failed);
            return failed == 0 ? 0 : 1;
        }
    }
}
