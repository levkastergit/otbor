using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
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
            RealClick(Find<FlatButton>(form).First(b => b.Text.StartsWith("← Назад")));
            Check(s.Queue.Count == 1 && Tiles(form).Count == 8,
                "с экрана завершения можно вернуться назад: очередь " + s.Queue.Count + ", плиток " + Tiles(form).Count);
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
            MakeJob(root, "40_props", "job_2", true, 64, 64);
            var decided = new Session(root);
            decided.Decide(decided.Queue[0], new[] { decided.Queue[0].Id + "__ref_1" });
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
                Action<string> checkButtons = mode =>
                {
                    List<FlatButton> all = Find<FlatButton>(form).Where(b => b.Visible).ToList();
                    bool ok = all.Count >= 5 && all.All(b =>
                    {
                        Rectangle r = b.RectangleToScreen(b.ClientRectangle);
                        var center = new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
                        return form.ClientRectangle.Contains(form.RectangleToClient(r)) &&
                               (!b.Enabled || WindowFromPoint(center) == b.Handle);
                    });
                    Check(ok, "окно " + width + ", " + mode + ": все кнопки целиком видны и нажимаются");
                    if (!ok)
                        foreach (FlatButton b in all)
                            Say("      " + b.Text + " " + form.RectangleToClient(b.RectangleToScreen(b.ClientRectangle)) + " попадание=" +
                                (WindowFromPoint(b.PointToScreen(new Point(b.Width / 2, b.Height / 2))) == b.Handle));
                };
                checkButtons("отбор");
                Key(form, Keys.V);
                Pump(40);
                Check(Title(form).Text.StartsWith("Выбранное"), "окно " + width + ": обзор открыт");
                checkButtons("обзор");
                form.Close();
                Pump(20);
            }
        }

        // Кит как на ПК 3: обучающие наборы лежат точно как задания (папка/папка/*.png).
        static void TestRoots()
        {
            string results = Fresh("kit");
            string kit = Path.GetDirectoryName(results);
            Directory.CreateDirectory(Path.Combine(kit, "scripts"));
            foreach (string sub in new[] { "datasets\\char", "datasets\\prop", "datasets\\bg", "refs\\bg", "refs\\props", "refs\\ui" })
                Png(Path.Combine(kit, sub, "000_x.png"), 8, 8, Color.Red);
            MakeJob(results, "30_char_p0", "char_dasha_hub", true);
            string before = Snapshot(kit);

            Check(Session.IsKit(kit), "корень кита распознан");
            Check(Session.ResultsOf(kit) == results, "запуск из корня кита открывает results: " + Session.ResultsOf(kit));
            Check(Session.ResultsOf(kit + "\\") == Path.Combine(kit + "\\", "results"), "то же при пути с \\ на конце");
            Check(Session.ResultsOf(results) == results, "запуск из results открывает results");
            bool refused = false;
            try { new Session(kit); }
            catch (InvalidOperationException) { refused = true; }
            Check(refused, "корень кита нельзя открыть как папку отбора");
            Check(Snapshot(kit) == before, "обучающие наборы и results не тронуты");

            var s = new Session(Session.ResultsOf(kit));
            Check(s.Queue.Count == 1 && s.Queue[0].Id == "char_dasha_hub", "в очереди только задания из results");

            Directory.Delete(Path.Combine(results, "30_char_p0"), true);
            Check(Session.ResultsOf(kit) == null, "кит без результатов не принят за results");
            File.Delete(Path.Combine(kit, "jobs.json"));
            Check(Session.IsKit(kit), "кит распознан и без jobs.json");
            Check(Session.ResultsOf(kit) == null, "без jobs.json обучающие наборы тоже не открываются");
        }

        static readonly string[] MixedJobs =
        {
            "85_mg/mg_sea_sheet", "10_ui_search/ui_panel_bottom", "61_p1/p1_tights", "80_chars2/dasha_candle",
            "78_decor/dec_kids_drawer_closed", "31_portraits/port_dasha", "99_old/unknown_job", "60_bg_rooms/bg_kids_room",
            "30_char_p0/char_dasha_hub", "80_chars2/dasha_flashlight", "30_char_p0/char_papa_stand"
        };

        static string Ids(Session s)
        {
            return string.Join(",", s.Queue.Select(j => j.Id));
        }

        static void TestSections()
        {
            Check(SectionMap.Of("char_dasha_hub").Section.Name == "Даша", "char_dasha_hub в разделе «Даша»");
            Check(SectionMap.Of("p1_tights").Section.Kind == "Локации" && SectionMap.Of("p1_tights").Section.Name == "Детская",
                "предмет П1 в локации «Детская»");
            Check(SectionMap.Of("p1_tights").Goes.Contains("Костюм принцессы"), "у предмета указан список поиска");
            Check(SectionMap.Of("char_dasha_hub").Goes == "Семейный совет и хаб", "у позы указано, куда она идёт");
            Check(SectionMap.Of("нет такого").Section == SectionMap.Other, "неизвестное задание попадает в «Без раздела»");

            string root = Fresh("sections");
            foreach (string job in MixedJobs)
            {
                string[] part = job.Split('/');
                MakeJob(root, part[0], part[1], true);
            }
            var s = new Session(root);
            Check(Ids(s) == "char_papa_stand,char_dasha_hub,dasha_flashlight,dasha_candle,port_dasha," +
                            "bg_kids_room,dec_kids_drawer_closed,p1_tights,mg_sea_sheet,ui_panel_bottom,unknown_job",
                "очередь по разделам: персонажи подряд, фон, декор, предметы: " + Ids(s));
            Job dasha = s.Queue[1];
            Check(dasha.Goes == "Семейный совет и хаб" && dasha.Section.Name == "Даша", "раздел и «куда идёт» попали в задание");
            Check(s.Queue.Last().Goes == null, "у неизвестного задания без jobs.json нет строки «куда идёт»");

            List<SectionStat> stats = s.Stats();
            SectionStat row = stats.First(x => x.Section.Name == "Даша");
            Check(row.Total == 4 && row.Left == 4, "счётчик раздела: всего 4");
            Check(stats.Select(x => x.Section.Index).SequenceEqual(stats.Select(x => x.Section.Index).OrderBy(x => x)), "разделы в порядке игры");

            s.Skip();
            Check(s.Queue[0].Id == "char_dasha_hub" && s.Queue[1].Id == "dasha_flashlight", "последний в разделе откладывается в конец очереди");
            Check(s.Queue.Last().Id == "char_papa_stand", "папа ушёл в конец");
            s.JumpTo(s.Queue.First(j => j.Id == "char_papa_stand").Section);
            Check(s.Queue[0].Id == "char_papa_stand" && s.Queue.Count == MixedJobs.Length, "переход к разделу, задания не потеряны");

            s.JumpTo(s.Queue.First(j => j.Id == "char_dasha_hub").Section);
            Check(s.Queue[0].Id == "char_dasha_hub", "переход к «Даша»");
            s.Skip();
            Check(string.Join(",", s.Queue.Take(4).Select(j => j.Id)) == "dasha_flashlight,dasha_candle,port_dasha,char_dasha_hub",
                "отложенное задание ждёт конца своего раздела: " + Ids(s));

            Section kids = s.Queue.First(j => j.Id == "p1_tights").Section;
            Check(s.JumpTo(kids) && s.Queue[0].Id == "bg_kids_room", "переход к локации начинается с фона");
            Check(Ids(s).EndsWith("char_papa_stand,char_dasha_hub,dasha_flashlight,dasha_candle,port_dasha"),
                "пропущенные разделы идут после остальных: " + Ids(s));
            Check(s.Queue.Count == MixedJobs.Length, "после переходов очередь полная");
            s.JumpTo(s.Queue.First(j => j.Id == "dasha_candle").Section);
            Check(s.Queue[0].Id == "char_dasha_hub", "после перехода порядок раздела исходный");

            Section sectionDasha = s.Queue[0].Section;
            while (s.Queue[0].Section == sectionDasha) s.Decide(s.Queue[0], new[] { s.Queue[0].Id + "__ref_1" });
            Check(s.Queue[0].Id == "bg_kids_room", "раздел закончился, очередь идёт к следующему");
            Check(!s.JumpTo(sectionDasha), "к разобранному разделу перейти нельзя");
            row = s.Stats().First(x => x.Section.Name == "Даша");
            Check(row.Total == 4 && row.Left == 0 && row.Done == 4, "разобранный раздел посчитан: 4 из 4");
            string csv = File.ReadAllText(Path.Combine(root, Session.ChoiceFile), Encoding.UTF8);
            string[] lines = csv.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            Check(lines[0].Contains("Раздел") && lines[0].Contains("Куда идёт"), "в выбор.csv есть раздел и назначение");
            Check(lines[1].Contains("Персонажи: Даша") && lines[1].Contains("Семейный совет и хаб"), "строка выбор.csv: " + lines[1]);

            Decision undone = s.Undo();
            Check(undone.Id == "port_dasha" && s.Queue[0].Id == "port_dasha", "«Назад» возвращает задание в начало");
            row = s.Stats().First(x => x.Section.Name == "Даша");
            Check(row.Total == 4 && row.Left == 1, "после «Назад» счётчик верный");

            // перегенерация: задание и в журнале, и снова в очереди, в счётчике оно одно
            s.Decide(s.Queue[0], new string[0], "лицо не то");
            MakeJob(root, "31_portraits", "port_dasha", true);
            s = new Session(root);
            row = s.Stats().First(x => x.Section.Name == "Даша");
            Check(row.Total == 4 && row.Left == 1, "новые варианты после перегенерации не удваивают задание");
        }

        static void TestSectionUi()
        {
            string root = Fresh("sectionui");
            foreach (string job in MixedJobs)
            {
                string[] part = job.Split('/');
                MakeJob(root, part[0], part[1], true, 200, 300);
            }
            var s = new Session(root);
            var form = new MainForm(s, Offline());
            form.WindowState = FormWindowState.Normal;
            form.Size = new Size(1280, 800);
            form.Show();
            Pump(100);
            WaitImages(form);
            SectionList list = Find<SectionList>(form).Single();
            Label goes = Find<Label>(form).First(l => l.Text.StartsWith("Куда идёт"));
            Check(list.Visible && list.Left == 0 && list.Width < 400, "панель разделов слева");
            Check(goes.Text == "Куда идёт: П0, обе сцены", "в шапке написано, куда идёт: " + goes.Text);
            Check(Find<Label>(form).Any(l => l.Text.StartsWith("Персонажи: Папа, 1 из 1")), "в шапке раздел и номер в нём");

            int height = Theme.Small.Height + 8;
            // строки: ПЕРСОНАЖИ, Папа, Даша, ЛОКАЦИИ, Детская, МИНИ-ИГРЫ, Морской бой, ИНТЕРФЕЙС, Экран поиска, ПРОЧЕЕ, Без раздела
            Action<int> clickRow = row =>
            {
                var at = (IntPtr)(((row * height + height / 2) << 16) | 40);
                SendMessage(list.Handle, WM_LBUTTONDOWN, (IntPtr)1, at);
                SendMessage(list.Handle, WM_LBUTTONUP, IntPtr.Zero, at);
                Pump(40);
            };
            clickRow(4);
            Check(s.Queue[0].Id == "bg_kids_room", "клик по «Детская» открывает её фон");
            WaitImages(form);
            Check(goes.Text == "Куда идёт: Фон: П1, Ф1", "строка обновилась: " + goes.Text);
            clickRow(0);
            Check(s.Queue[0].Id == "bg_kids_room", "клик по заголовку ничего не делает");
            Tiles(form)[0].Toggle();
            clickRow(4);
            Check(Tiles(form)[0].Marked, "клик по текущему разделу не сбрасывает отметки");
            Key(form, Keys.Enter);
            Check(s.Done.Count == 1 && s.Queue[0].Id == "dec_kids_drawer_closed", "после клика по панели Enter выбирает, а очередь идёт по разделу");
            WaitImages(form);
            Key(form, Keys.D2);
            Check(Tiles(form)[1].Marked, "цифры работают после клика по панели");
            clickRow(2);
            Check(s.Queue[0].Id == "char_dasha_hub", "клик по «Даша»");
            WaitImages(form);
            form.Close();
            Pump(20);

            // Все разделы игры не помещаются в окно: по одному заданию из каждого.
            root = Fresh("allsections");
            SectionFile file;
            using (Stream stream = typeof(SectionMap).Assembly.GetManifestResourceStream("sections.json"))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
                file = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<SectionFile>(reader.ReadToEnd());
            foreach (SectionEntry entry in file.sections) MakeJob(root, "50_x", entry.jobs[0].id, true, 32, 32);
            s = new Session(root);
            Check(s.Stats().Count == file.sections.Count, "разделов в окне столько же, сколько в файле: " + s.Stats().Count);
            form = new MainForm(s, Offline());
            form.WindowState = FormWindowState.Normal;
            form.Size = new Size(1280, 500);
            form.Show();
            Pump(100);
            list = Find<SectionList>(form).Single();
            FieldInfo scrollField = typeof(SectionList).GetField("scroll", BindingFlags.NonPublic | BindingFlags.Instance);
            Func<int> scroll = () => (int)scrollField.GetValue(list);
            int rows = s.Stats().Count + s.Stats().Select(x => x.Section.Kind).Distinct().Count();
            int most = rows * height - list.Height;
            Check(most > 0 && scroll() == 0, "список длиннее окна, сначала виден верх");
            list.Wheel(-120);
            Check(scroll() == height * 3, "колесо вниз сдвигает на три строки: " + scroll());
            list.Wheel(-1200000);
            Check(scroll() == most, "вниз не дальше конца: " + scroll() + " из " + most);
            list.Wheel(1200000);
            Check(scroll() == 0, "вверх не дальше начала");

            Section lastSection = s.Stats().Last().Section;
            s.JumpTo(lastSection);
            list.SetData(s.Stats(), s.Queue[0].Section);
            Check(scroll() == most, "текущий последний раздел прокручен в видимую часть: " + scroll());
            clickRow = row =>
            {
                var at = (IntPtr)(((row * height - scroll() + height / 2) << 16) | 40);
                SendMessage(list.Handle, WM_LBUTTONDOWN, (IntPtr)1, at);
                SendMessage(list.Handle, WM_LBUTTONUP, IntPtr.Zero, at);
                Pump(40);
            };
            clickRow(rows - 2);
            Check(s.Queue[0].Section == s.Stats()[s.Stats().Count - 2].Section, "клик в прокрученном списке попадает в свою строку");
            Section chosen = s.Queue[0].Section;
            var bar = (IntPtr)((2 << 16) | (list.Width - 3));
            SendMessage(list.Handle, WM_LBUTTONDOWN, (IntPtr)1, bar);
            SendMessage(list.Handle, WM_LBUTTONUP, IntPtr.Zero, bar);
            Pump(20);
            Check(s.Queue[0].Section == chosen && scroll() < height, "полоса справа прокручивает и не переключает раздел: " + scroll());
            form.Close();
            Pump(20);
        }

        static void TestLevels()
        {
            Func<string, string> at = id => SectionMap.Level(id).Section.Kind + " / " + SectionMap.Level(id).Section.Name;
            Check(at("bg_p0_reception") == "П0 «Пустая папка» / Сцена 1: приёмная клиники", "первая сцена пролога: " + at("bg_p0_reception"));
            Check(at("bg_p0_office") == "П0 «Пустая папка» / Сцена 2: кабинет Папы", "вторая сцена пролога: " + at("bg_p0_office"));
            Check(at("mg_diary_card") == "П0 «Пустая папка» / Мини-игра «Ежедневник»", "мини-игра в своём уровне: " + at("mg_diary_card"));
            Check(at("p1_tights") == "П1 «Утро» / Сцена 1: детская", "предмет в своей сцене: " + at("p1_tights"));
            Check(at("masha_sleep_bed") == "Д1 «Чёрная комната» / Персонажи", "поза на своём уровне: " + at("masha_sleep_bed"));
            Check(at("dec_bed_red") == "Д1 «Чёрная комната» / Сцена: спальня ночью", "кровать там же: " + at("dec_bed_red"));
            Check(at("port_dedushka") == "М1 «Двор» / Персонажи", "портрет на уровне, где персонаж появляется: " + at("port_dedushka"));
            Check(at("hub_das_sleep") == "Хаб / Дочери засыпают", "хаб отдельно: " + at("hub_das_sleep"));
            Check(at("ui_panel_bottom") == "Интерфейс / Экран поиска", "интерфейс вне уровней: " + at("ui_panel_bottom"));
            Check(SectionMap.Level("p1_tights").Goes == SectionMap.Of("p1_tights").Goes, "«Куда идёт» одна для обоих видов");
            Check(SectionMap.Level("dec_dentist_chair").Section.Index < SectionMap.Level("dedushka_chess").Section.Index, "М2 идёт раньше М1, как в игре");
            Check(SectionMap.Level("нет такого").Section == SectionMap.Other, "неизвестное задание в конце");

            string root = Fresh("levels");
            foreach (string job in MixedJobs)
            {
                string[] part = job.Split('/');
                MakeJob(root, part[0], part[1], true);
            }
            var s = new Session(root);
            string bySections = Ids(s);
            Check(!s.Levels, "сначала вид по разделам");
            s.SetLevels(true);
            Check(Ids(s) == "char_papa_stand,char_dasha_hub,port_dasha,bg_kids_room,dec_kids_drawer_closed,p1_tights," +
                            "mg_sea_sheet,dasha_flashlight,dasha_candle,ui_panel_bottom,unknown_job", "порядок прохождения: " + Ids(s));
            Check(s.Queue[0].Section.Kind == "П0 «Пустая папка»" && s.Queue[0].Goes == "П0, обе сцены", "у задания раздел уровня и строка «Куда идёт»");
            Check(s.Stats().First().Section.Name == "Персонажи" && s.Stats().First().Total == 3, "счётчик по строке уровня");

            s.Decide(s.Queue[0], new[] { s.Queue[0].Id + "__ref_1" });
            s = new Session(root);
            Check(s.Levels && s.Queue[0].Id == "char_dasha_hub", "вид запоминается после перезапуска");
            Section first = s.Queue[0].Section;
            Check(s.Review(first).Count == 1 && s.Review(first)[0].Decision.Id == "char_papa_stand", "обзор работает в виде по уровням");
            string[] lines = File.ReadAllText(Path.Combine(root, Session.ChoiceFile), Encoding.UTF8).Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            Check(lines[0].StartsWith("Раздел;Уровень;") && lines[1].Contains("Персонажи: Папа") && lines[1].Contains("П0 «Пустая папка»: Персонажи"),
                "в выбор.csv есть и раздел, и уровень: " + lines[1]);
            s.Skip();
            Check(s.Queue[0].Id == "port_dasha" && s.Queue[1].Id == "char_dasha_hub", "«Пропустить» работает внутри строки уровня");
            Decision undone = s.Undo();
            Check(undone.Id == "char_papa_stand" && s.Queue[0].Section.Kind == "П0 «Пустая папка»", "«Назад» возвращает задание с разделом уровня");
            s.SetLevels(false);
            Check(Ids(s) == bySections, "обратно к разделам: прежний порядок");
            Check(!new Session(root).Levels, "выбор разделов тоже запоминается");
        }

        static void TestLevelsUi()
        {
            string root = Fresh("levelsui");
            foreach (string job in MixedJobs)
            {
                string[] part = job.Split('/');
                MakeJob(root, part[0], part[1], true, 64, 64);
            }
            var s = new Session(root);
            var form = new MainForm(s, Offline());
            form.WindowState = FormWindowState.Normal;
            form.StartPosition = FormStartPosition.Manual;
            form.Location = Point.Empty;
            form.Size = new Size(1280, 800);
            form.Show();
            Pump(100);
            WaitImages(form);
            FlatButton bySection = Find<FlatButton>(form).First(b => b.Text == "Разделы");
            FlatButton byLevel = Find<FlatButton>(form).First(b => b.Text.StartsWith("Уровни"));
            Check(bySection.BackColor == Theme.Accent && byLevel.BackColor == Theme.Panel, "подсвечен текущий вид");
            Key(form, Keys.D1);
            RealClick(byLevel);
            WaitImages(form);
            Check(s.Levels && byLevel.BackColor == Theme.Accent && bySection.BackColor == Theme.Panel, "клик включает вид по уровням");
            Check(s.Queue[0].Id == "char_papa_stand" && Tiles(form).All(t => !t.Marked), "очередь начинается с пролога");
            Check(Find<Label>(form).Any(l => l.Text.StartsWith("П0 «Пустая папка»: Персонажи, 1 из 3")), "в шапке уровень и строка");

            SectionList list = Find<SectionList>(form).Single();
            int height = Theme.Small.Height + 8;
            // строки: П0, Персонажи, П1, Сцена 1: детская, Ж1, Морской бой
            var at = (IntPtr)(((3 * height + height / 2) << 16) | 40);
            SendMessage(list.Handle, WM_LBUTTONDOWN, (IntPtr)1, at);
            SendMessage(list.Handle, WM_LBUTTONUP, IntPtr.Zero, at);
            Pump(40);
            Check(s.Queue[0].Id == "bg_kids_room", "клик по строке уровня открывает её: " + s.Queue[0].Id);
            WaitImages(form);

            Key(form, Keys.D1);
            Key(form, Keys.Enter);
            Pump(40);
            WaitImages(form);
            Key(form, Keys.D1);
            Key(form, Keys.Enter);
            Pump(40);
            WaitImages(form);
            Key(form, Keys.D1);
            Key(form, Keys.Enter);
            Pump(40);
            Check(s.Queue[0].Id == "mg_sea_sheet", "строка закончилась, очередь идёт к следующему уровню: " + s.Queue[0].Id);
            Label hint = Find<Label>(form).First(l => l.Dock == DockStyle.Bottom);
            Check(hint.Text.Contains("«Сцена 1: детская» закончен"), "подсказка про конец строки: " + hint.Text);
            WaitImages(form);
            Key(form, Keys.V);
            Pump(120);
            Check(Title(form).Text == "Выбранное: Сцена 1: детская" && Tiles(form).Count == 3, "обзор строки уровня: " + Title(form).Text);
            Key(form, Keys.L);
            Pump(40);
            WaitImages(form);
            Check(!s.Levels && Find<FlatButton>(form).Any(b => b.Visible && b.Text.StartsWith("Далее")), "L из обзора возвращает к отбору в другом виде");
            Check(s.Queue[0].Id == "char_papa_stand", "разделы снова с начала");
            RealClick(bySection);
            Check(!s.Levels, "клик по текущему виду ничего не меняет");
            form.Close();
            Pump(30);
        }

        static string[] DriveNames(string folder)
        {
            return Directory.Exists(folder)
                ? Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Select(f => f.Substring(folder.Length + 1)).OrderBy(x => x, StringComparer.Ordinal).ToArray()
                : new string[0];
        }

        static void TestDrive()
        {
            int quiet = DriveSync.QuietMs, retry = DriveSync.RetryMs;
            DriveSync.QuietMs = 20;
            DriveSync.RetryMs = 100;
            string keys = TokenStore.Dir;
            string root = Fresh("drive");
            string base_ = Path.GetDirectoryName(root);
            TokenStore.Dir = Path.Combine(base_, "appdata");
            try
            {
                MakeJob(root, "30_char_p0", "char_dasha_hub", true);
                MakeJob(root, "60_bg_rooms", "bg_kids_room", false);
                MakeJob(root, "80_chars2", "dasha_candle", true);
                MakeJob(root, "99_old", "unknown_job", true);
                var s = new Session(root);
                s.Decide(JobOf(s, "char_dasha_hub"), new[] { "char_dasha_hub__ref_1", "char_dasha_hub__ref_3" });
                s.Decide(JobOf(s, "bg_kids_room"), new[] { "bg_kids_room__ref_2" });
                s.Decide(JobOf(s, "dasha_candle"), new string[0], "нет");
                s.Decide(JobOf(s, "unknown_job"), new[] { "unknown_job__ref_1" });
                List<DriveFile> files = s.DriveFiles();
                string[] targets = files.Select(f => f.Target).OrderBy(x => x, StringComparer.Ordinal).ToArray();
                Check(string.Join(" | ", targets) == @"Локации\Детская\bg_kids_room__ref_2.png | Персонажи\Даша\char_dasha_hub__ref_1_alpha.png | " +
                      @"Персонажи\Даша\char_dasha_hub__ref_3_alpha.png | Прочее\Без раздела\unknown_job__ref_1_alpha.png",
                    "вырезанные спрайты и фоны по разделам, отказов нет: " + string.Join(" | ", targets));
                Check(files.All(f => File.Exists(f.Source)), "источники на месте");

                string myDrive = Path.Combine(base_, "My Drive");
                string folder = Path.Combine(myDrive, DriveSync.FolderName);
                string manifest = s.Abs(Session.DriveFileList);
                var drive = new DriveSync(manifest, null);
                drive.Start();
                Check(drive.Label == "Не подключён", "сначала не подключён");
                drive.Request(files);
                Thread.Sleep(80);
                Check(!File.Exists(manifest), "без папки ничего не выкладывается");

                drive.SetFolder(folder);
                WaitFor(() => drive.Problem != null);
                Check(drive.Problem != null && drive.Problem.Contains("Google Диск") && !Directory.Exists(myDrive), "приложение закрыто: папка не создаётся, причина написана: " + drive.Problem);
                Check(DriveSync.LoadFolder() == folder, "папка запомнена");
                Directory.CreateDirectory(myDrive);
                string mine = Path.Combine(folder, "Персонажи", "моё.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(mine));
                File.WriteAllText(mine, "своё");
                WaitFor(() => drive.Count == 4);
                Check(drive.Problem == null && drive.Count == 4, "приложение запущено: выкладка повторилась сама: " + drive.Label);
                Check(DriveNames(folder).Length == 5 && DriveNames(folder).Contains(@"Персонажи\Даша\char_dasha_hub__ref_3_alpha.png"), "файлы на месте: " + DriveNames(folder).Length);
                DriveFile hub = files.First(f => f.Target.Contains("__ref_3"));
                Check(File.ReadAllBytes(hub.Source).SequenceEqual(File.ReadAllBytes(Path.Combine(folder, hub.Target))), "содержимое совпадает");

                // Повторная выкладка ничего не копирует заново
                string copy = Path.Combine(folder, hub.Target);
                File.SetLastWriteTimeUtc(copy, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
                drive.Request(s.DriveFiles());
                Check(drive.Flush(3000), "выкладка закончена");
                Check(File.GetLastWriteTimeUtc(copy).Year == 2020, "неизменённый файл заново не копируется");
                File.Delete(copy);
                drive.Request(s.DriveFiles());
                drive.Flush(3000);
                Check(File.Exists(copy), "файл, пропавший с Диска, выкладывается снова");

                // Выбор заново: прежняя картинка уходит, чужой файл остаётся
                s.Reopen(s.Done.First(d => d.Id == "char_dasha_hub"));
                drive.Request(s.DriveFiles());
                drive.Flush(3000);
                Check(!DriveNames(folder).Any(n => n.Contains("char_dasha_hub")), "отменённый выбор убран с Диска");
                Check(!Directory.Exists(Path.Combine(folder, "Персонажи", "Даша")), "пустая папка убрана");
                Check(File.Exists(mine), "файлы, которые программа не выкладывала, не тронуты");
                s.Decide(s.Queue[0], new[] { "char_dasha_hub__ref_2" });
                var next = new DriveSync(manifest, folder);
                next.Start();
                next.Request(s.DriveFiles());
                next.Flush(3000);
                Check(DriveNames(folder).Contains(@"Персонажи\Даша\char_dasha_hub__ref_2_alpha.png"), "после перезапуска выкладка продолжается");

                // Новая папка results не знает прошлых файлов и ничего на Диске не удаляет
                string other = Fresh("drive2");
                var empty = new DriveSync(Path.Combine(other, Session.DriveFileList), folder);
                empty.Start();
                int before = DriveNames(folder).Length;
                empty.Request(new List<DriveFile>());
                empty.Flush(3000);
                Check(DriveNames(folder).Length == before, "пустой разбор ничего не удаляет");
                Check(DriveSync.FindRoots() != null, "поиск диска не падает");
            }
            finally
            {
                TokenStore.Dir = keys;
                DriveSync.QuietMs = quiet;
                DriveSync.RetryMs = retry;
            }
        }

        static void TestDriveUi()
        {
            int quiet = DriveSync.QuietMs;
            DriveSync.QuietMs = 20;
            string keys = TokenStore.Dir;
            string root = Fresh("driveui");
            string base_ = Path.GetDirectoryName(root);
            TokenStore.Dir = Path.Combine(base_, "appdata");
            Func<IWin32Window, string> realPick = ControlForm.PickDriveFolder;
            string myDrive = Path.Combine(base_, "My Drive");
            Directory.CreateDirectory(myDrive);
            string folder = Path.Combine(myDrive, DriveSync.FolderName);
            try
            {
                MakeJob(root, "30_char_p0", "char_dasha_hub", true, 64, 64);
                MakeJob(root, "80_chars2", "dasha_candle", true, 64, 64);
                var s = new Session(root);
                s.Decide(JobOf(s, "char_dasha_hub"), new[] { "char_dasha_hub__ref_1" });
                var form = new MainForm(s, Offline());
                form.WindowState = FormWindowState.Normal;
                form.StartPosition = FormStartPosition.Manual;
                form.Location = Point.Empty;
                form.Size = new Size(1280, 800);
                form.Show();
                Pump(100);
                WaitImages(form);
                Key(form, Keys.G);
                Pump(120);
                ControlForm panel = Application.OpenForms.OfType<ControlForm>().Single();
                Func<string, FlatButton> button = text => Find<FlatButton>(panel).First(b => b.Text.StartsWith(text));
                FlatButton connect = button("Подключить Google");
                Check(!button("Выложить сейчас").Enabled && !button("Открыть папку на Диске").Enabled, "без подключения выкладывать нечем");
                Check(Find<Label>(panel).Any(l => l.Text == "Не подключён"), "в пульте написано, что не подключён");

                ControlForm.PickDriveFolder = owner => null;
                RealClick(connect);
                Check(connect.Text.StartsWith("Подключить") && !Directory.Exists(folder), "отмена в окне ничего не меняет");
                ControlForm.PickDriveFolder = owner => folder;
                RealClick(connect);
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 3000 && DriveNames(folder).Length == 0) Pump(20);
                Pump(60);
                Check(DriveNames(folder).SequenceEqual(new[] { @"Персонажи\Даша\char_dasha_hub__ref_1_alpha.png" }), "после подключения уже выбранное выложено сразу: " + string.Join(",", DriveNames(folder)));
                Check(connect.Text == "Сменить папку Диска" && button("Выложить сейчас").Enabled, "кнопки после подключения");
                Check(Find<Label>(panel).Any(l => l.Text.StartsWith("Файлов на Диске: 1")), "счётчик файлов виден");

                Key(form, Keys.D2);
                Key(form, Keys.Enter);
                sw.Restart();
                while (sw.ElapsedMilliseconds < 3000 && DriveNames(folder).Length < 2) Pump(20);
                Check(DriveNames(folder).Contains(@"Персонажи\Даша\dasha_candle__ref_2_alpha.png"), "новое решение выкладывается само: " + string.Join(",", DriveNames(folder)));
                Key(form, Keys.Back);
                sw.Restart();
                while (sw.ElapsedMilliseconds < 3000 && DriveNames(folder).Length > 1) Pump(20);
                Check(DriveNames(folder).Length == 1, "«Назад» убирает картинку с Диска");
                form.Close();
                Pump(40);
            }
            finally
            {
                ControlForm.PickDriveFolder = realPick;
                TokenStore.Dir = keys;
                DriveSync.QuietMs = quiet;
            }
        }

        static Session Dasha(string root)
        {
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(root), "jobs.json"),
                "{\"note\":\"x\",\"jobs\":[{\"id\":\"char_dasha_hub\",\"ru\":\"Даша стоит, скрестив руки (совет и хаб)\"," +
                "\"asset\":\"T_Char_Dasha_Hub\",\"group\":\"30_char_p0\"}]}", new UTF8Encoding(false));
            foreach (string job in new[] { "30_char_p0/char_dasha_hub", "80_chars2/dasha_flashlight", "80_chars2/dasha_candle",
                                           "31_portraits/port_dasha", "60_bg_rooms/bg_kids_room" })
            {
                string[] part = job.Split('/');
                MakeJob(root, part[0], part[1], true, 200, 300);
            }
            return new Session(root);
        }

        static Job JobOf(Session s, string id)
        {
            return s.Queue.First(j => j.Id == id);
        }

        static void TestReview()
        {
            string root = Fresh("review");
            var s = Dasha(root);
            Section dasha = JobOf(s, "char_dasha_hub").Section;
            Check(s.Review(dasha).Count == 0, "до разбора обзор пуст");

            // Совпадение имён: выбранный файл получит суффикс, обзор должен показать именно его.
            Png(Path.Combine(root, Session.NeedDir, "80_chars2", "dasha_flashlight", "dasha_flashlight__t2i_3.png"), 8, 8, Color.Red);
            s.Decide(JobOf(s, "dasha_candle"), new string[0], "свеча не та");
            s.Decide(JobOf(s, "char_dasha_hub"), new[] { "char_dasha_hub__ref_1", "char_dasha_hub__ref_3" });
            s.Decide(JobOf(s, "dasha_flashlight"), new[] { "dasha_flashlight__t2i_3" });
            s.Decide(JobOf(s, "bg_kids_room"), new[] { "bg_kids_room__ref_2" });

            Action<Session, string> checkItems = (x, when) =>
            {
                List<ReviewItem> items = x.Review(dasha);
                Check(string.Join(",", items.Select(i => i.Decision.Id)) == "char_dasha_hub,char_dasha_hub,dasha_flashlight,dasha_candle",
                    when + ": порядок как в очереди, по плитке на вариант: " + string.Join(",", items.Select(i => i.Decision.Id)));
                Check(items[0].Variant.Name == "char_dasha_hub__ref_1" && items[1].Variant.Name == "char_dasha_hub__ref_3", when + ": оба выбранных варианта");
                Check(items.Take(3).All(i => File.Exists(x.Abs(i.Variant.Raw)) && File.Exists(x.Abs(i.Variant.Cut))),
                    when + ": файлы выбранного находятся в _Нужные");
                Check(items[2].Variant.Raw.EndsWith(" (2).png"), when + ": при совпадении имён взят настоящий файл: " + items[2].Variant.Raw);
                Check(items[0].Variant.Show.EndsWith("_alpha.png"), when + ": показывается вырезанный");
                Check(items[3].Variant == null && items[3].Decision.Note == "свеча не та", when + ": отвергнутое задание без картинки, с причиной");
            };
            checkItems(s, "сразу");
            checkItems(new Session(root), "после перезапуска");

            // Отмена решения из середины журнала
            s = new Session(root);
            Decision flashlight = s.Done.First(d => d.Id == "dasha_flashlight");
            string others = string.Join(",", s.Done.Where(d => d != flashlight).Select(d => d.Id));
            Decision back = s.Reopen(flashlight);
            Check(back == flashlight && s.Queue[0].Id == "dasha_flashlight", "заново выбранное задание первое в очереди");
            Check(s.Queue[0].Variants.Count == 8, "вернулись все 8 вариантов");
            Check(string.Join(",", s.Done.Select(d => d.Id)) == others, "остальные решения не тронуты: " + string.Join(",", s.Done.Select(d => d.Id)));
            Check(s.BuildStatus().All(r => r.id != "dasha_flashlight"), "запись убрана из статуса");
            Check(File.Exists(Path.Combine(root, Session.NeedDir, "80_chars2", "dasha_flashlight", "dasha_flashlight__t2i_3.png")),
                "чужой файл с тем же именем остался");
            s = new Session(root);
            Check(s.Done.Count == 3 && s.Queue.Count(j => j.Id == "dasha_flashlight") == 1, "после перезапуска состояние то же");
            s.Decide(JobOf(s, "dasha_flashlight"), new[] { "dasha_flashlight__ref_1" });
            Check(s.Review(dasha).Count(i => i.Decision.Id == "dasha_flashlight") == 1, "новый выбор виден в обзоре");

            // Перегенерация: новые варианты уже в очереди, а отказ отменяют из обзора
            Png(Path.Combine(root, "80_chars2", "dasha_candle", "dasha_candle__ref_5.png"), 8, 8, Color.Green);
            s = new Session(root);
            s.Reopen(s.Done.First(d => d.Id == "dasha_candle"));
            Check(s.Queue.Count(j => j.Id == "dasha_candle") == 1 && s.Queue[0].Variants.Count == 9,
                "задание в очереди одно, в нём старые и новые варианты: " + s.Queue[0].Variants.Count);

            // Сбой: файл не вернуть, журнал и файлы остаются как были
            s.Decide(s.Queue[0], new[] { "dasha_candle__ref_5" });
            Decision hub = s.Done.First(d => d.Id == "char_dasha_hub");
            int place = s.Done.IndexOf(hub);
            int queued = s.Queue.Count;
            File.WriteAllText(s.Abs(hub.Moves[0].From), "мешает");
            bool failed = false;
            try { s.Reopen(hub); }
            catch (IOException) { failed = true; }
            Check(failed, "при занятом имени отмена сообщает об ошибке");
            Check(s.Done.IndexOf(hub) == place && s.Queue.Count == queued, "решение осталось на своём месте");
            Check(hub.Moves.All(m => File.Exists(s.Abs(m.To))), "файлы вернулись в _Нужные и _Ненужные");
        }

        static string HeaderTitle(Form f) { return Title(f).Text; }

        static void TestReviewUi()
        {
            string root = Fresh("reviewui");
            var s = Dasha(root);
            Section dasha = JobOf(s, "char_dasha_hub").Section;
            var form = new MainForm(s, Offline());
            string asked = null;
            form.AskNote = j => { asked = j.Id; return "нет"; };
            form.WindowState = FormWindowState.Normal;
            form.Size = new Size(1280, 800);
            form.Show();
            Pump(100);
            WaitImages(form);
            FlatButton btnReview = Find<FlatButton>(form).First(b => b.Text.StartsWith("Выбранное"));
            Check(!btnReview.Enabled, "пока ничего не разобрано, смотреть нечего");
            Key(form, Keys.V);
            Check(!Find<FlatButton>(form).Any(b => b.Visible && b.Text.StartsWith("Назад")), "V без решений обзор не открывает");

            Key(form, Keys.D1);
            Key(form, Keys.D3);
            Key(form, Keys.Enter);
            WaitImages(form);
            Key(form, Keys.D2);
            Key(form, Keys.Enter);
            WaitImages(form);
            Key(form, Keys.D0);
            Check(asked == "dasha_candle", "отказ в очереди: " + asked);
            Pump(40);
            WaitImages(form);
            Key(form, Keys.D4);
            Key(form, Keys.Enter);
            Pump(40);
            Check(s.Done.Count == 4 && s.Queue[0].Id == "bg_kids_room", "раздел разобран, очередь ушла к следующему");
            Label hint = Find<Label>(form).First(l => l.Dock == DockStyle.Bottom);
            Check(hint.Text.Contains("«Даша» закончен"), "подсказка, что раздел закончен: " + hint.Text);
            WaitImages(form);

            Key(form, Keys.V);
            Pump(40);
            Check(HeaderTitle(form) == "Выбранное: Даша", "V сразу после конца раздела открывает этот раздел: " + HeaderTitle(form));
            Check(Tiles(form).Count == 5, "плиток столько, сколько выбрано, плюс отказ: " + Tiles(form).Count);
            Func<Tile, bool> withPicture = t => t.V.Show != null;
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 5000 && !Tiles(form).Where(withPicture).All(t => t.Image != null)) Pump(5);
            Check(Tiles(form).Where(withPicture).All(t => t.Image != null) && Tiles(form).Count(withPicture) == 4, "выбранные картинки загрузились");
            Check(Tiles(form)[0].Caption.Contains("ref_1") && Tiles(form)[0].Caption.Contains("стоит") && !Tiles(form)[0].Caption.Contains("("),
                "подпись: вариант и задание: " + Tiles(form)[0].Caption);
            Check(Tiles(form)[3].V.Show == null && Tiles(form)[4].V.Show != null, "отказ показан плиткой без картинки на своём месте");
            Key(form, Keys.Escape);
            WaitImages(form);
            Check(Find<FlatButton>(form).Any(b => b.Visible && b.Text.StartsWith("Далее")) && s.Queue[0].Id == "bg_kids_room", "Esc возвращает к отбору");

            // Клик по разобранному разделу в панели
            SectionList list = Find<SectionList>(form).Single();
            int height = Theme.Small.Height + 8;
            Action<int> clickRow = row =>
            {
                var at = (IntPtr)(((row * height + height / 2) << 16) | 40);
                SendMessage(list.Handle, WM_LBUTTONDOWN, (IntPtr)1, at);
                SendMessage(list.Handle, WM_LBUTTONUP, IntPtr.Zero, at);
                Pump(40);
            };
            clickRow(1);
            Check(HeaderTitle(form) == "Выбранное: Даша", "клик по разобранному разделу открывает обзор");
            Pump(120);

            // Клавиши отбора в обзоре ничего не решают
            int before = s.Done.Count;
            asked = null;
            Key(form, Keys.D0);
            Key(form, Keys.S);
            Key(form, Keys.Back);
            Check(asked == null && s.Done.Count == before && s.Queue[0].Id == "bg_kids_room" && HeaderTitle(form) == "Выбранное: Даша",
                "0, S и Backspace в обзоре ничего не меняют");

            // Сравнение
            Key(form, Keys.Enter);
            Check(Tiles(form).Count == 5 && hint.Text.Contains("хотя бы два"), "Enter без отметок подсказывает: " + hint.Text);
            Key(form, Keys.D1);
            Key(form, Keys.Enter);
            Check(Tiles(form).Count == 5, "одной отметки мало");
            Key(form, Keys.D3);
            Key(form, Keys.Enter);
            Pump(120);
            Check(Tiles(form).Count == 2 && HeaderTitle(form) == "Сравнение: Даша", "сравнение показывает две отмеченные: " + Tiles(form).Count);
            Check(Tiles(form)[0].Bounds.Width > 300 && Tiles(form)[1].Image != null, "в сравнении картинки крупные и загружены: " + Tiles(form)[0].Bounds.Width);
            Mouse(Tiles(form)[0], WM_RBUTTONDOWN, WM_RBUTTONUP);
            Pump(30);
            ZoomView zoom = Find<ZoomView>(form).Single();
            Check(zoom.Visible, "правый клик в сравнении открывает крупно");
            Key(form, Keys.Escape);
            Check(!zoom.Visible && HeaderTitle(form) == "Сравнение: Даша", "Esc закрывает только крупный просмотр");
            Key(form, Keys.Escape);
            Pump(40);
            Check(Tiles(form).Count == 5 && Tiles(form)[0].Marked && Tiles(form)[2].Marked, "из сравнения обратно ко всем, отметки на месте");

            // Выбрать заново: двух заданий мало
            Key(form, Keys.R);
            Check(HeaderTitle(form) == "Выбранное: Даша" && s.Done.Count == before && hint.Text.Contains("одного задания"), "R с двумя заданиями не срабатывает");
            Key(form, Keys.D1);
            Key(form, Keys.R);
            Pump(40);
            WaitImages(form);
            Check(s.Queue[0].Id == "dasha_flashlight" && s.Done.Count == before - 1, "R возвращает отмеченное задание: " + s.Queue[0].Id);
            Check(Tiles(form).Count == 8 && Tiles(form).Count(t => t.Marked) == 1, "открыты все варианты, прежний выбор отмечен");
            Check(hint.Text.Contains("Выберите заново"), "подсказка на повторный выбор");
            Key(form, Keys.D2);
            Key(form, Keys.D3);
            Key(form, Keys.Enter);
            Pump(120);
            Check(HeaderTitle(form) == "Выбранное: Даша" && s.Done.Count == before, "после выбора возвращается обзор раздела: " + HeaderTitle(form));
            List<ReviewItem> again = s.Review(dasha);
            Check(again.Count == 5 && again[2].Variant.Name == "dasha_flashlight__ref_3", "в обзоре новый выбор вместо прежнего: " + again[2].Variant.Name);
            Key(form, Keys.D4);
            Key(form, Keys.R);
            Pump(40);
            WaitImages(form);
            Check(s.Queue[0].Id == "dasha_candle" && Tiles(form).Count(t => t.Marked) == 0, "отказ тоже можно открыть заново");
            Key(form, Keys.S);
            Pump(40);
            Key(form, Keys.V);
            Pump(40);
            Key(form, Keys.Escape);
            Pump(40);
            form.Close();
            Pump(20);

            // Страницы: у раздела больше 12 выбранных
            root = Fresh("reviewpages");
            s = Dasha(root);
            s.Decide(JobOf(s, "char_dasha_hub"), JobOf(s, "char_dasha_hub").Variants.Select(v => v.Name).ToList());
            s.Decide(JobOf(s, "dasha_candle"), JobOf(s, "dasha_candle").Variants.Select(v => v.Name).ToList());
            s.Decide(JobOf(s, "dasha_flashlight"), new[] { "dasha_flashlight__ref_1" });
            s.Decide(JobOf(s, "port_dasha"), new[] { "port_dasha__ref_1" });
            form = new MainForm(s, Offline());
            form.WindowState = FormWindowState.Normal;
            form.Size = new Size(1280, 800);
            form.Show();
            Pump(100);
            WaitImages(form);
            list = Find<SectionList>(form).Single();
            clickRow(1);
            FlatButton next = Find<FlatButton>(form).First(b => b.Text.StartsWith("Страница"));
            Check(Tiles(form).Count == 12 && next.Visible && next.Enabled, "первая страница: 12 плиток");
            Key(form, Keys.D1);
            Key(form, Keys.Right);
            Pump(40);
            Check(Tiles(form).Count == 6 && !next.Enabled, "вторая страница: 6 плиток, дальше некуда");
            Key(form, Keys.D6);
            Key(form, Keys.Right);
            Check(Tiles(form).Count == 6, "за последней страницей ничего нет");
            Key(form, Keys.Enter);
            Pump(120);
            Check(Tiles(form).Count == 2, "отмеченные с разных страниц сравниваются вместе: " + Tiles(form).Count);
            Key(form, Keys.Enter);
            Pump(40);
            Check(Tiles(form).Count == 6, "из сравнения обратно на ту же страницу");
            Key(form, Keys.PageUp);
            Pump(40);
            Check(Tiles(form).Count == 12 && Tiles(form)[0].Marked, "PageUp, отметка на первой странице цела");
            form.Close();
            Pump(20);

            // Всё разобрано: обзор доступен и закрывается на итоговый экран
            root = Fresh("reviewdone");
            MakeJob(root, "30_char_p0", "char_dasha_hub", true, 64, 64);
            s = new Session(root);
            form = new MainForm(s, Offline());
            form.WindowState = FormWindowState.Normal;
            form.Size = new Size(1280, 800);
            form.Show();
            Pump(100);
            WaitImages(form);
            Key(form, Keys.D1);
            Key(form, Keys.Enter);
            Pump(40);
            Check(s.Queue.Count == 0 && HeaderTitle(form) == "Готово", "всё разобрано");
            Key(form, Keys.V);
            Pump(120);
            Check(HeaderTitle(form) == "Выбранное: Даша" && Tiles(form).Count == 1, "на итоговом экране V открывает обзор");
            Key(form, Keys.A);
            Pump(120);
            Check(Tiles(form)[0].Image != null, "A работает и на итоговом обзоре");
            Key(form, Keys.Escape);
            Pump(40);
            Check(HeaderTitle(form) == "Готово" && Tiles(form).Count == 0, "Esc возвращает итоговый экран");
            form.Close();
            Pump(20);
        }

        const string DemoScript = @"
param([string]$Mode = 'ok')
Write-Host '=== Русский текст из PowerShell'
[Console]::Out.WriteLine('[3/10] job_x  1.0 с')
foreach ($part in ""шаг"", ""`r 40%| 400/1000 [00:10<00:15]"", ""`r 90%| 900/1000 [00:20<00:02]`n"") {
    [Console]::Out.Write($part)
    [Console]::Out.Flush()
    Start-Sleep -Milliseconds 250
}
[Console]::Error.WriteLine('ошибка в stderr')
if ($Mode -eq 'fail') { exit 7 }
if ($Mode -eq 'hang') {
    $p = Start-Process ping -ArgumentList '-n','60','127.0.0.1' -NoNewWindow -PassThru
    Write-Host ""child=$($p.Id)""
    $p.WaitForExit()
}
Write-Host ""OTBOR=$env:OTBOR""
exit 0
";

        static string MakeKit(string name, string script)
        {
            string results = Fresh(name);
            string kit = Path.GetDirectoryName(results);
            Directory.CreateDirectory(Path.Combine(kit, "scripts"));
            foreach (string file in new[] { "generate.ps1", "train.ps1", "install.ps1" })
                File.WriteAllText(Path.Combine(kit, "scripts", file), script, new UTF8Encoding(true));
            return results;
        }

        static void RunTask(KitTask task, ProcessStartInfo info, List<string> lines, List<string> live, Action during = null)
        {
            int code = -1;
            bool over = false;
            Action<string> line = t => { lock (lines) lines.Add(t); };
            Action<string> partial = t => { lock (live) live.Add(t); };
            Action<int> finished = c => { code = c; over = true; };
            task.Line += line;
            task.Live += partial;
            task.Finished += finished;
            task.Start(info);
            if (during != null) during();
            WaitFor(() => over, 30000);
            task.Line -= line;
            task.Live -= partial;
            task.Finished -= finished;
            lines.Add("код=" + code);
        }

        static void TestKitTask()
        {
            int done, total;
            Check(KitTask.TryProgress("[12/4900] char_x  3.1 с", out done, out total) && done == 12 && total == 4900, "прогресс генерации");
            Check(KitTask.TryProgress("pd_char_v2:  10%|█ | 300/3000 [06:24<57:40, 1.28s/it, lr: 1e-04]", out done, out total) && done == 300 && total == 3000, "прогресс обучения");
            Check(!KitTask.TryProgress("Модель загружена за 12 с, видеопамять 15.9 ГБ", out done, out total), "обычная строка не прогресс");

            string results = MakeKit("kittask", DemoScript);
            string kit = Path.GetDirectoryName(results);
            var task = new KitTask();
            var lines = new List<string>();
            var live = new List<string>();
            RunTask(task, KitTask.PowerShell(kit, "generate.ps1", ""), lines, live);
            Say("      вывод: " + string.Join(" | ", lines));
            Check(lines.Contains("=== Русский текст из PowerShell"), "русский текст PowerShell не искажён");
            Check(lines.Contains("[3/10] job_x  1.0 с"), "строка прогресса дошла");
            Check(lines.Contains("ошибка в stderr"), "поток ошибок тоже показан");
            Check(lines.Contains("OTBOR=1"), "скрипт знает, что запущен из программы");
            Check(lines.Contains(" 90%| 900/1000 [00:20<00:02]") && !lines.Any(l => l.Contains("40%")), "в журнал попадает только итог строки с \\r");
            Check(live.Any(l => l.Contains("40%")), "промежуточные значения идут в живую строку");
            Check(lines.Last() == "код=0" && !task.Running, "код 0, задача закончена");

            lines.Clear();
            RunTask(task, KitTask.PowerShell(kit, "generate.ps1", "-Mode fail"), lines, live);
            Check(lines.Last() == "код=7", "код ошибки скрипта дошёл: " + lines.Last());

            lines.Clear();
            bool twice = false;
            RunTask(task, KitTask.PowerShell(kit, "generate.ps1", "-Mode hang"), lines, live, () =>
            {
                WaitFor(() => { lock (lines) return lines.Any(l => l.StartsWith("child=")); }, 15000);
                try { task.Start(KitTask.PowerShell(kit, "generate.ps1", "")); }
                catch (InvalidOperationException) { twice = true; }
                task.Stop();
            });
            Check(twice, "вторая задача не запускается, пока идёт первая");
            Check(task.WasStopped && !task.Running, "задача остановлена");
            int child = int.Parse(lines.First(l => l.StartsWith("child=")).Substring(6));
            bool alive;
            try { Process.GetProcessById(child); alive = true; }
            catch (ArgumentException) { alive = false; }
            Check(!alive, "дочерний процесс тоже остановлен");
        }

        // Ответы релиза GitHub: JSON последней версии, список файлов и сами файлы.
        class FakeRelease : IDisposable
        {
            readonly HttpListener listener = new HttpListener();
            public readonly string Url;
            public readonly Dictionary<string, byte[]> Files = new Dictionary<string, byte[]>();

            public FakeRelease()
            {
                var probe = new TcpListener(IPAddress.Loopback, 0);
                probe.Start();
                int port = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
                Url = "http://127.0.0.1:" + port + "/";
                listener.Prefixes.Add(Url);
                listener.Start();
                new Thread(Serve) { IsBackground = true }.Start();
            }

            void Serve()
            {
                while (listener.IsListening)
                {
                    HttpListenerContext c;
                    try { c = listener.GetContext(); }
                    catch (HttpListenerException) { return; }
                    catch (ObjectDisposedException) { return; }
                    byte[] body;
                    if (Files.TryGetValue(c.Request.Url.AbsolutePath, out body))
                    {
                        c.Response.ContentLength64 = body.Length;
                        c.Response.OutputStream.Write(body, 0, body.Length);
                    }
                    else c.Response.StatusCode = 404;
                    c.Response.Close();
                }
            }

            public void Dispose() { listener.Close(); }
        }

        static void TestUpdate()
        {
            Check(KitUpdate.IsNewer("v99.0.0") && !KitUpdate.IsNewer("v0.1.0") && !KitUpdate.IsNewer("v" + KitUpdate.Current), "сравнение версий");
            string results = Fresh("update");
            string kit = Path.GetDirectoryName(results);
            string scripts = Path.Combine(kit, "scripts");
            Directory.CreateDirectory(scripts);
            string gen = Path.Combine(scripts, "generate.py");
            string running = Path.Combine(results, "Otbor.exe");
            string rootCopy = Path.Combine(kit, "Otbor.exe");
            File.WriteAllText(gen, "старый");
            File.WriteAllText(running, "старый exe");
            File.WriteAllText(rootCopy, "старый exe");
            byte[] exe = Encoding.ASCII.GetBytes("MZ новая программа");
            using (var server = new FakeRelease())
            {
                string apiUrl = KitUpdate.ApiUrl, assetUrl = KitUpdate.AssetUrl, rawUrl = KitUpdate.RawUrl;
                KitUpdate.ApiUrl = server.Url + "latest";
                KitUpdate.AssetUrl = server.Url + "asset/";
                KitUpdate.RawUrl = server.Url + "raw/";
                try
                {
                    server.Files["/latest"] = Encoding.UTF8.GetBytes("{\"tag_name\":\"v9.9.9\",\"name\":\"x\"}");
                    server.Files["/raw/v9.9.9/kit/files.txt"] = Encoding.UTF8.GetBytes("kit/generate.py\tscripts\\generate.py\r\nkit/new.py\tscripts\\new.py\r\n");
                    server.Files["/raw/v9.9.9/kit/generate.py"] = Encoding.UTF8.GetBytes("новый");
                    Check(KitUpdate.LatestTag() == "v9.9.9", "последняя версия прочитана");

                    var said = new List<string>();
                    bool failed = false;
                    try { KitUpdate.Install("v9.9.9", kit, running, said.Add); }
                    catch (WebException) { failed = true; }
                    Check(failed && File.ReadAllText(gen) == "старый" && !Directory.GetFiles(scripts, "*.new").Any(), "файла нет на сервере: всё осталось как было");

                    server.Files["/raw/v9.9.9/kit/new.py"] = Encoding.UTF8.GetBytes("новый файл");
                    server.Files["/asset/v9.9.9/Otbor.exe"] = Encoding.ASCII.GetBytes("не программа");
                    failed = false;
                    try { KitUpdate.Install("v9.9.9", kit, running, said.Add); }
                    catch (InvalidDataException) { failed = true; }
                    Check(failed && File.ReadAllText(gen) == "старый" && File.ReadAllText(running) == "старый exe", "повреждённый exe не ставится");

                    server.Files["/asset/v9.9.9/Otbor.exe"] = exe;
                    KitUpdate.Install("v9.9.9", kit, running, said.Add);
                    Check(File.ReadAllText(gen) == "новый" && File.ReadAllText(Path.Combine(scripts, "new.py")) == "новый файл", "файлы кита обновлены, новый файл добавлен");
                    Check(File.ReadAllBytes(running).SequenceEqual(exe) && File.ReadAllBytes(rootCopy).SequenceEqual(exe), "обновлены обе копии программы");
                    Check(File.ReadAllText(running + ".old") == "старый exe", "работающая копия отложена рядом");
                    Check(!Directory.GetFiles(kit, "*.new", SearchOption.AllDirectories).Any(), "временных файлов не осталось");
                    KitUpdate.Cleanup(running);
                    Check(!File.Exists(running + ".old"), "старая копия удаляется при запуске");
                }
                finally
                {
                    KitUpdate.ApiUrl = apiUrl;
                    KitUpdate.AssetUrl = assetUrl;
                    KitUpdate.RawUrl = rawUrl;
                }
            }
        }

        static void TestPanelUi()
        {
            // Генерация кладёт новое задание, очередь должна увидеть его после конца задачи.
            string script = DemoScript.Replace("if ($Mode -eq 'hang')", @"
$png = Join-Path $PSScriptRoot '..\source.png'
$dir = Join-Path $PSScriptRoot '..\results\80_chars2\dasha_candle'
if ($Mode -eq 'ok' -and (Test-Path $png)) { New-Item -ItemType Directory -Force $dir | Out-Null; Copy-Item $png (Join-Path $dir 'dasha_candle__ref_1.png') }
if ($Mode -eq 'hang')");
            string root = MakeKit("panel", script);
            string kit = Path.GetDirectoryName(root);
            Png(Path.Combine(kit, "source.png"), 64, 64, Color.Green);
            MakeJob(root, "30_char_p0", "char_dasha_hub", true, 64, 64);
            var s = new Session(root);
            var form = new MainForm(s, Offline());
            form.WindowState = FormWindowState.Normal;
            form.Size = new Size(1280, 800);
            form.StartPosition = FormStartPosition.Manual;
            form.Location = Point.Empty;
            form.Show();
            Pump(100);
            WaitImages(form);
            Key(form, Keys.D2);
            FlatButton btnPanel = Find<FlatButton>(form).First(b => b.Text.StartsWith("Пульт"));
            Key(form, Keys.G);
            Pump(100);
            ControlForm panel = Application.OpenForms.OfType<ControlForm>().Single();
            panel.StartPosition = FormStartPosition.Manual;
            panel.Location = new Point(100, 50);
            Pump(50);
            Func<string, FlatButton> button = text => Find<FlatButton>(panel).First(b => b.Text.StartsWith(text));
            FlatButton generate = button("Сгенерировать");
            TextBox logBox = Find<TextBox>(panel).Single();
            Check(panel.Visible && generate.Enabled, "G открывает пульт, кнопки доступны");
            Check(button("Открыть сравнение").Enabled == false, "сравнения пока нет");

            var sw = Stopwatch.StartNew();
            RealClick(generate);
            Check(!generate.Enabled || logBox.Text.Contains("готово"), "во время задачи кнопки выключены");
            Check(btnPanel.Text.StartsWith("Пульт: ") || logBox.Text.Contains("готово"), "в главном окне видно, что идёт задача");
            while (sw.ElapsedMilliseconds < 20000 && !logBox.Text.Contains("Генерация: готово")) Pump(20);
            Say("      журнал: " + logBox.Text.Replace("\r\n", " | "));
            Check(logBox.Text.Contains("Русский текст") && logBox.Text.Contains("Генерация: готово"), "вывод задачи и итог в журнале");
            Pump(60);
            Check(generate.Enabled && btnPanel.Text == "Пульт  [G]", "после конца кнопки снова доступны");
            Check(s.Queue.Any(j => j.Id == "dasha_candle"), "новое задание появилось в очереди без перезапуска");
            Check(s.Queue[0].Id == "char_dasha_hub" && Tiles(form)[1].Marked, "текущее задание и отметки на месте");
            Check(Find<Label>(panel).Any(l => l.Text.StartsWith("На перегенерацию отмечено: 0")), "счётчик перегенерации виден");

            // Ошибка скрипта
            string errors = Path.Combine(kit, "scripts", "generate.ps1");
            File.WriteAllText(errors, "exit 3", new UTF8Encoding(true));
            RealClick(button("Перегенерировать"));
            sw.Restart();
            while (sw.ElapsedMilliseconds < 15000 && !logBox.Text.Contains("ошибка, код 3")) Pump(20);
            Check(logBox.Text.Contains("Перегенерация: ошибка, код 3"), "ошибка задачи показана");

            // Дообучение и закрытие при идущей задаче
            string asked = null;
            bool answer = false;
            Func<string, bool> realAsk = ControlForm.Ask;
            ControlForm.Ask = text => { asked = text; return answer; };
            File.WriteAllText(Path.Combine(kit, "scripts", "train.ps1"), DemoScript.Replace("$Mode = 'ok'", "$Mode = 'hang'").Replace("param([string]$Mode = 'hang')", "param([string]$Mode = 'hang', [switch]$Retrain)"), new UTF8Encoding(true));
            RealClick(button("Дообучить"));
            Check(asked == null && Find<Label>(panel).Any(l => l.Text.Contains("Обновить программу")), "без retrain.py дообучение просит обновиться");
            File.WriteAllText(Path.Combine(kit, "scripts", "retrain.py"), "");
            RealClick(button("Дообучить"));
            Check(asked != null && asked.Contains("65 минут") && !task(panel), "перед дообучением программа спрашивает");
            answer = true;
            RealClick(button("Дообучить"));
            sw.Restart();
            while (sw.ElapsedMilliseconds < 15000 && !logBox.Text.Contains("child=")) Pump(20);
            Check(logBox.Text.Contains("child="), "дообучение запущено");
            answer = false;
            form.Close();
            Pump(40);
            Check(form.Visible && asked.Contains("Остановить её"), "при идущей задаче окно спрашивает и остаётся");
            answer = true;
            RealClick(button("Остановить"));
            sw.Restart();
            while (sw.ElapsedMilliseconds < 15000 && !logBox.Text.Contains("Дообучение: остановлено")) Pump(20);
            Check(logBox.Text.Contains("Дообучение: остановлено"), "кнопка останавливает задачу");

            // Лист сравнения и выбор модели
            ControlForm.Ask = text => true;
            Png(Path.Combine(root, "compare", "pd_char_v2.png"), 16, 16, Color.Red);
            panel.ShowState();
            CheckBox model = Find<CheckBox>(panel).Single();
            Check(model.Enabled && button("Открыть сравнение").Enabled, "после дообучения доступны сравнение и выбор модели");
            model.Checked = true;
            Check(File.ReadAllText(Path.Combine(kit, "char_lora.txt")) == "pd_char_v2", "выбор модели сохранён");
            model.Checked = false;
            Check(!File.Exists(Path.Combine(kit, "char_lora.txt")), "выбор модели снимается");

            panel.Close();
            Pump(30);
            Check(!panel.Visible && !panel.IsDisposed, "закрытие пульта только прячет его");
            Key(form, Keys.G);
            Pump(40);
            Check(panel.Visible, "пульт открывается снова");
            form.Close();
            Pump(40);
            Check(panel.IsDisposed, "пульт закрывается вместе с программой");
            ControlForm.Ask = realAsk;

            // Без кита кнопки выключены
            root = Fresh("nokit");
            MakeJob(root, "30_char_p0", "char_dasha_hub", true, 64, 64);
            form = new MainForm(new Session(root), Offline());
            form.WindowState = FormWindowState.Normal;
            form.Show();
            Pump(60);
            Key(form, Keys.G);
            Pump(60);
            panel = Application.OpenForms.OfType<ControlForm>().Single();
            Check(!Find<FlatButton>(panel).First(b => b.Text.StartsWith("Сгенерировать")).Enabled, "без FluxKit запуск недоступен");
            Check(Find<Label>(panel).Any(l => l.Text.Contains("скрипты не найдены")), "причина написана");
            form.Close();
            Pump(40);
        }

        static bool task(ControlForm panel) { return Find<Label>(panel).Any(l => l.Text.EndsWith(": идёт")); }

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

        // Нажимает кнопки пульта на папке с настоящими скриптами кита.
        static void Sandbox(string root, string outDir)
        {
            ControlForm.Ask = text => true;
            var s = new Session(root);
            var form = new MainForm(s, Offline());
            form.WindowState = FormWindowState.Normal;
            form.StartPosition = FormStartPosition.Manual;
            form.Location = Point.Empty;
            form.Size = new Size(1280, 800);
            form.Show();
            Pump(200);
            int before = s.Queue.Count;
            form.ShowPanel();
            Pump(200);
            ControlForm panel = Application.OpenForms.OfType<ControlForm>().Single();
            TextBox logBox = Find<TextBox>(panel).Single();
            Action<string, string> press = (button, title) =>
            {
                RealClick(Find<FlatButton>(panel).First(b => b.Text.StartsWith(button)));
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 100000 && !logBox.Text.Contains("=== " + title + ": ")) Pump(50);
                Pump(200);
            };
            press("Дообучить", "Дообучение");
            Save(panel, Path.Combine(outDir, "panel_retrain.png"));
            panel.ShowState();
            Find<CheckBox>(panel).Single().Checked = true;
            press("Перегенерировать", "Перегенерация");
            File.WriteAllText(Path.Combine(outDir, "sandbox_log.txt"), logBox.Text, new UTF8Encoding(false));
            Say("очередь: было " + before + ", стало " + s.Queue.Count + ": " + string.Join(",", s.Queue.Select(j => j.Id + "=" + j.Variants.Count)));
            ControlForm.Ask = text => true;
            form.Close();
            Pump(50);
        }

        // Снимки обзора на настоящей папке: по решению на каждое задание, затем сравнение двух отмеченных.
        static void ReviewShots(string root, string outDir)
        {
            var s = new Session(root);
            while (s.Queue.Count > 0) s.Decide(s.Queue[0], new[] { s.Queue[0].Variants[0].Name });
            var form = new MainForm(s, Offline());
            form.WindowState = FormWindowState.Normal;
            form.Size = new Size(1500, 900);
            form.Show();
            Pump(100);
            Key(form, Keys.V);
            Pump(400);
            Save(form, Path.Combine(outDir, "review_all.png"));
            Key(form, Keys.D1);
            Key(form, Keys.D2);
            Pump(60);
            Save(form, Path.Combine(outDir, "review_marked.png"));
            Key(form, Keys.Enter);
            Pump(400);
            Save(form, Path.Combine(outDir, "review_compare.png"));
            form.Close();
        }

        static void Dialogs(string outDir)
        {
            string root = Fresh("dialogs");
            foreach (string job in MixedJobs)
            {
                string[] part = job.Split('/');
                MakeJob(root, part[0], part[1], true, 400, 600);
            }
            MakeJob(root, "40_props", "job_0", true, 400, 400);
            var session = new Session(root);
            session.Decide(session.Queue[0], new[] { session.Queue[0].Id + "__ref_1" });
            session.Decide(session.Queue[0], new string[0], "лицо не то");
            var form = new MainForm(session, Offline());
            form.WindowState = FormWindowState.Normal;
            form.Size = new Size(1280, 800);
            form.Show();
            Pump(150);
            WaitImages(form);
            Pump(150);
            Save(form, Path.Combine(outDir, "dlg_main.png"));
            Key(form, Keys.L);
            Pump(300);
            Save(form, Path.Combine(outDir, "dlg_levels.png"));
            form.ShowPanel();
            Pump(400);
            Save(Application.OpenForms.OfType<ControlForm>().Single(), Path.Combine(outDir, "dlg_panel.png"));
            form.Close();
            using (var note = new NoteDialog("Папа стоит, базовая поза"))
            {
                note.StartPosition = FormStartPosition.CenterScreen;
                note.Show();
                Pump(200);
                Save(note, Path.Combine(outDir, "dlg_note.png"));
                note.Close();
            }
            using (var disk = new DriveDialog())
            {
                disk.StartPosition = FormStartPosition.CenterScreen;
                disk.Show();
                Pump(200);
                Save(disk, Path.Combine(outDir, "dlg_drive.png"));
                disk.Close();
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
            if (args.Length > 2 && args[0] == "review") { ReviewShots(args[1], args[2]); return 0; }
            if (args.Length > 2 && args[0] == "sandbox") { Sandbox(args[1], args[2]); return 0; }
            try
            {
                if (args.Length > 0 && args[0] == "live")
                {
                    Say("--- настоящий GitHub");
                    TestLive();
                }
                else
                {
                    Say("--- папки и разделы");
                    TestRoots();
                    TestSections();
                    TestLevels();
                    Say("--- обзор выбранного");
                    TestReview();
                    Say("--- задачи и обновление");
                    TestKitTask();
                    TestUpdate();
                    Say("--- Google Диск");
                    TestDrive();
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
                    Say("--- панель разделов");
                    TestSectionUi();
                    Say("--- обзор в окне");
                    TestReviewUi();
                    Say("--- пульт");
                    TestPanelUi();
                    Say("--- вид по уровням");
                    TestLevelsUi();
                    Say("--- Google Диск в окне");
                    TestDriveUi();
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
