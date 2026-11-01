using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace Otbor
{
    static class Theme
    {
        public static readonly Color Back = Color.FromArgb(32, 33, 36);
        public static readonly Color Panel = Color.FromArgb(43, 45, 49);
        public static readonly Color Line = Color.FromArgb(70, 73, 79);
        public static readonly Color Text = Color.FromArgb(232, 234, 237);
        public static readonly Color Dim = Color.FromArgb(166, 171, 178);
        public static readonly Color Accent = Color.FromArgb(46, 160, 67);
        public static readonly Color Warn = Color.FromArgb(255, 180, 60);

        public static readonly Font Small = new Font("Segoe UI", 10f);
        public static readonly Font Normal = new Font("Segoe UI", 11f);
        public static readonly Font Bold = new Font("Segoe UI", 11f, FontStyle.Bold);
        public static readonly Font Title = new Font("Segoe UI", 18f, FontStyle.Bold);
    }

    static class Painter
    {
        static readonly TextureBrush checker = MakeChecker();

        static TextureBrush MakeChecker()
        {
            const int cell = 14;
            using (var b = new Bitmap(cell * 2, cell * 2))
            using (var g = Graphics.FromImage(b))
            using (var light = new SolidBrush(Color.FromArgb(156, 156, 156)))
            {
                g.Clear(Color.FromArgb(122, 122, 122));
                g.FillRectangle(light, 0, 0, cell, cell);
                g.FillRectangle(light, cell, cell, cell, cell);
                return new TextureBrush(b);
            }
        }

        public static void Background(Graphics g, Rectangle area, bool transparent)
        {
            if (transparent) g.FillRectangle(checker, area);
        }

        public static Rectangle Fit(Size image, Rectangle area)
        {
            float k = Math.Min(area.Width / (float)image.Width, area.Height / (float)image.Height);
            int w = Math.Max(1, (int)Math.Round(image.Width * k));
            int h = Math.Max(1, (int)Math.Round(image.Height * k));
            return new Rectangle(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h);
        }

        public static void DrawScaled(Graphics g, Bitmap image, Rectangle target)
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using (var attributes = new ImageAttributes())
            {
                attributes.SetWrapMode(WrapMode.TileFlipXY);
                g.DrawImage(image, target, 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attributes);
            }
        }
    }

    class Tile : Control
    {
        public readonly Variant V;
        public readonly int Number;
        public bool Marked;
        public Bitmap Image;
        public bool Transparent;
        public string Title;
        public string MarkText = "НУЖНЫЙ";

        public event Action<Tile> Changed;
        public event Action<Tile> Confirmed;
        public event Action<Tile> ZoomRequested;

        Bitmap scaled;
        string error;
        bool hover;

        public static int CaptionHeight { get { return Theme.Bold.Height + 10; } }

        public string Caption
        {
            get { return Number + "   " + (Title ?? V.Label) + (Marked ? "   " + MarkText : ""); }
        }

        // В обзоре подпись длинная: название позы важнее места под картинкой.
        int CaptionSpace
        {
            get { return Title != null ? CaptionHeight * 2 : CaptionHeight; }
        }

        public Tile(Variant v, int number)
        {
            V = v;
            Number = number;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            BackColor = Theme.Panel;
            Cursor = Cursors.Hand;
        }

        public void SetImage(Bitmap image, bool transparent, string loadError)
        {
            DropImages();
            Image = image;
            Transparent = transparent;
            error = loadError;
            Invalidate();
        }

        public void Toggle()
        {
            Marked = !Marked;
            Invalidate();
            if (Changed != null) Changed(this);
        }

        void DropImages()
        {
            if (Image != null) Image.Dispose();
            if (scaled != null) scaled.Dispose();
            Image = null;
            scaled = null;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) DropImages();
            base.Dispose(disposing);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                if (e.Clicks >= 2)
                {
                    Marked = true;
                    Invalidate();
                    if (Confirmed != null) Confirmed(this);
                }
                else Toggle();
            }
            else if (e.Button == MouseButtons.Right && ZoomRequested != null) ZoomRequested(this);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            hover = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hover = false;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            int pad = 6;
            int caption = CaptionSpace;
            var area = new Rectangle(pad, pad, Width - pad * 2, Height - caption - pad);
            if (area.Width <= 0 || area.Height <= 0) return;

            if (Image != null)
            {
                Painter.Background(g, area, Transparent);
                Rectangle target = Painter.Fit(Image.Size, area);
                if (scaled == null || scaled.Size != target.Size)
                {
                    if (scaled != null) scaled.Dispose();
                    scaled = new Bitmap(target.Width, target.Height, PixelFormat.Format32bppPArgb);
                    using (Graphics sg = Graphics.FromImage(scaled))
                    {
                        sg.CompositingMode = CompositingMode.SourceCopy;
                        Painter.DrawScaled(sg, Image, new Rectangle(Point.Empty, target.Size));
                    }
                }
                g.DrawImageUnscaled(scaled, target.Location);
            }
            else
            {
                TextRenderer.DrawText(g, error ?? "загрузка…", Theme.Small, area, Theme.Dim,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            }

            var captionArea = new Rectangle(pad, Height - caption, Width - pad * 2, caption);
            TextRenderer.DrawText(g, Caption, Theme.Bold, captionArea, Marked ? Color.FromArgb(120, 230, 140) : Theme.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak |
                TextFormatFlags.TextBoxControl | TextFormatFlags.EndEllipsis);

            int border = Marked ? 6 : 2;
            using (var pen = new Pen(Marked ? Theme.Accent : hover ? Theme.Dim : Theme.Line, border))
            {
                pen.Alignment = PenAlignment.Inset;
                g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            }
        }
    }

    class ZoomView : Control
    {
        public Tile Shown;

        public ZoomView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            BackColor = Theme.Back;
            Visible = false;
            Dock = DockStyle.Fill;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (Shown == null || Shown.Image == null) return;
            Graphics g = e.Graphics;
            int caption = Tile.CaptionHeight;
            var area = new Rectangle(8, 8, Width - 16, Height - caption - 16);
            if (area.Width <= 0 || area.Height <= 0) return;
            Painter.Background(g, area, Shown.Transparent);
            Painter.DrawScaled(g, Shown.Image, Painter.Fit(Shown.Image.Size, area));
            string text = Shown.Caption + "      ←/→ другой вариант   •   цифра — отметить   •   Esc или клик — закрыть";
            TextRenderer.DrawText(g, text, Theme.Bold, new Rectangle(8, Height - caption - 4, Width - 16, caption),
                Shown.Marked ? Color.FromArgb(120, 230, 140) : Theme.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    class ProgressStrip : Control
    {
        public float Value;

        public ProgressStrip()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            BackColor = Theme.Line;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            using (var brush = new SolidBrush(Theme.Accent))
                e.Graphics.FillRectangle(brush, 0, 0, Width * Value, Height);
        }
    }

    // Список разделов: фокус не забирает, поэтому цифры и Enter по-прежнему управляют отбором.
    class SectionList : Control
    {
        const int BarWidth = 10;

        class Row
        {
            public string Kind;
            public SectionStat Stat;
        }

        public event Action<Section> Picked;

        readonly List<Row> rows = new List<Row>();
        Section current;
        int hover = -1;
        int scroll;
        bool dragging;

        static int RowHeight { get { return Theme.Small.Height + 8; } }
        int MaxScroll { get { return Math.Max(0, rows.Count * RowHeight - Height); } }
        bool HasBar { get { return MaxScroll > 0; } }

        public SectionList()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            BackColor = Theme.Panel;
            Dock = DockStyle.Left;
            Width = Theme.Small.Height * 15;
        }

        public void SetData(List<SectionStat> stats, Section now)
        {
            rows.Clear();
            string kind = null;
            foreach (SectionStat stat in stats)
            {
                if (stat.Section.Kind != kind)
                {
                    kind = stat.Section.Kind;
                    rows.Add(new Row { Kind = kind });
                }
                rows.Add(new Row { Stat = stat });
            }
            current = now;
            Reveal();
            Invalidate();
        }

        // Текущий раздел всегда виден: очередь сама переходит к следующему.
        void Reveal()
        {
            int i = rows.FindIndex(r => r.Stat != null && r.Stat.Section == current);
            if (i >= 0)
            {
                int top = (i > 0 && rows[i - 1].Kind != null ? i - 1 : i) * RowHeight;
                if (top < scroll) scroll = top;
                if ((i + 1) * RowHeight > scroll + Height) scroll = (i + 1) * RowHeight - Height;
            }
            scroll = Math.Max(0, Math.Min(scroll, MaxScroll));
        }

        public void Wheel(int delta)
        {
            scroll = Math.Max(0, Math.Min(scroll - delta / 120 * RowHeight * 3, MaxScroll));
            Invalidate();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Reveal();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            Wheel(e.Delta);
        }

        int RowAt(int y)
        {
            int i = (y + scroll) / RowHeight;
            return y >= 0 && i < rows.Count ? i : -1;
        }

        void Drag(int y)
        {
            scroll = Math.Max(0, Math.Min(y * MaxScroll / Math.Max(1, Height), MaxScroll));
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            if (HasBar && e.X >= Width - BarWidth)
            {
                dragging = true;
                Drag(e.Y);
                return;
            }
            int i = RowAt(e.Y);
            if (i >= 0 && rows[i].Stat != null && Picked != null) Picked(rows[i].Stat.Section);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            dragging = false;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging)
            {
                Drag(e.Y);
                return;
            }
            int i = RowAt(e.Y);
            Cursor = i >= 0 && rows[i].Stat != null ? Cursors.Hand : Cursors.Default;
            if (i != hover)
            {
                hover = i;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hover = -1;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            int height = RowHeight;
            int right = Width - (HasBar ? BarWidth : 0) - 8;
            for (int i = scroll / height; i < rows.Count && i * height - scroll < Height; i++)
            {
                Row row = rows[i];
                int y = i * height - scroll;
                if (row.Kind != null)
                {
                    TextRenderer.DrawText(g, row.Kind.ToUpperInvariant(), Theme.Small, new Rectangle(8, y, right - 8, height),
                        Theme.Warn, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                    continue;
                }
                SectionStat stat = row.Stat;
                bool now = stat.Section == current;
                if (now || i == hover)
                    using (var brush = new SolidBrush(now ? Theme.Accent : Theme.Line))
                        g.FillRectangle(brush, 0, y, Width, height);
                bool finished = stat.Left == 0;
                string count = finished ? "✓ " + stat.Total : stat.Done + " / " + stat.Total;
                Color color = now ? Color.White : finished ? Theme.Dim : Theme.Text;
                int countWidth = TextRenderer.MeasureText(count, Theme.Small).Width;
                TextRenderer.DrawText(g, count, Theme.Small, new Rectangle(right - countWidth, y, countWidth, height),
                    now ? Color.White : Theme.Dim, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                TextRenderer.DrawText(g, stat.Section.Name, Theme.Small, new Rectangle(16, y, right - countWidth - 20, height),
                    color, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
            if (!HasBar) return;
            int thumb = Math.Max(24, Height * Height / (rows.Count * height));
            int thumbTop = (Height - thumb) * scroll / MaxScroll;
            using (var brush = new SolidBrush(Theme.Dim))
                g.FillRectangle(brush, Width - BarWidth + 3, thumbTop, BarWidth - 4, thumb);
        }
    }

    class FlatButton : Button
    {
        // В диалогах кнопка должна быть selectable: иначе PerformClick по Enter и Esc молча не срабатывает.
        public FlatButton(string text, Color back, bool selectable = false)
        {
            SetStyle(ControlStyles.Selectable, selectable);
            Text = text;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderColor = Theme.Line;
            BackColor = back;
            ForeColor = Theme.Text;
            Font = Theme.Normal;
            Padding = new Padding(10, 4, 10, 4);
            Margin = new Padding(4);
            UseVisualStyleBackColor = false;
        }
    }

    class DarkDialog : Form
    {
        readonly TableLayoutPanel body = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Padding = new Padding(16),
            Location = Point.Empty
        };
        readonly int width = Theme.Normal.Height * 34;

        protected DarkDialog(string title)
        {
            Text = title;
            BackColor = Theme.Back;
            ForeColor = Theme.Text;
            Font = Theme.Normal;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Controls.Add(body);
        }

        protected void AddText(string text, Font font, Color color)
        {
            body.Controls.Add(new Label
            {
                Text = text,
                Font = font,
                ForeColor = color,
                AutoSize = true,
                MaximumSize = new Size(width, 0),
                Margin = new Padding(0, 0, 0, 10)
            });
        }

        protected TextBox AddBox(bool secret)
        {
            var box = new TextBox
            {
                Width = width,
                BackColor = Theme.Panel,
                ForeColor = Theme.Text,
                BorderStyle = BorderStyle.FixedSingle,
                UseSystemPasswordChar = secret,
                Margin = new Padding(0, 0, 0, 10)
            };
            body.Controls.Add(box);
            return box;
        }

        protected void AddButtons(params Control[] buttons)
        {
            var row = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = Padding.Empty
            };
            row.Controls.AddRange(buttons);
            body.Controls.Add(row);
        }
    }

    class NoteDialog : DarkDialog
    {
        public readonly TextBox Box;
        public readonly FlatButton Ok = new FlatButton("Перегенерировать  [Enter]", Theme.Accent, true);

        public string Note
        {
            get { return Box.Text.Trim(); }
        }

        public NoteDialog(string job) : base("Ни один не подходит")
        {
            var cancel = new FlatButton("Отмена  [Esc]", Theme.Panel, true);
            AddText(job, Theme.Bold, Theme.Text);
            AddText("Что не так? Это поможет исправить задание. Можно не писать и сразу нажать Enter.", Theme.Small, Theme.Dim);
            Box = AddBox(false);
            AddButtons(Ok, cancel);
            Ok.DialogResult = DialogResult.OK;
            cancel.DialogResult = DialogResult.Cancel;
            AcceptButton = Ok;
            CancelButton = cancel;
            ActiveControl = Box;
        }
    }

    class KeyDialog : DarkDialog
    {
        public readonly TextBox Box;
        public readonly FlatButton Save = new FlatButton("Сохранить  [Enter]", Theme.Accent, true);

        public string Token
        {
            get { return Box.Text.Trim(); }
        }

        public KeyDialog(string problem) : base("Подключение к GitHub")
        {
            var open = new FlatButton("Открыть страницу GitHub", Theme.Panel, true);
            var cancel = new FlatButton("Без GitHub  [Esc]", Theme.Panel, true);
            AddText("Подключение к " + GitHubSync.Repo, Theme.Bold, Theme.Text);
            if (problem != null) AddText(problem, Theme.Small, Theme.Warn);
            AddText("Это нужно один раз на этом ПК. Результаты отбора будут сами уходить в репозиторий, картинки туда не попадают.",
                Theme.Small, Theme.Dim);
            AddText("1. Нажмите «Открыть страницу GitHub» и войдите в аккаунт владельца репозитория. " +
                    "Название, срок и право записи (Contents) уже заполнены.\n" +
                    "2. В разделе Repository access выберите Only select repositories, а в списке Selected repositories — otbor.\n" +
                    "3. Нажмите Generate token, скопируйте ключ и вставьте его сюда.", Theme.Small, Theme.Text);
            Box = AddBox(true);
            AddText("Ключ хранится на этом ПК в зашифрованном виде и отправляется только на GitHub.", Theme.Small, Theme.Dim);
            AddButtons(open, Save, cancel);
            open.Click += (a, b) => OpenPage();
            Save.Enabled = false;
            Box.TextChanged += (a, b) => Save.Enabled = Token.Length > 0;
            Save.DialogResult = DialogResult.OK;
            cancel.DialogResult = DialogResult.Cancel;
            AcceptButton = Save;
            CancelButton = cancel;
            ActiveControl = Box;
        }

        void OpenPage()
        {
            try
            {
                Process.Start(GitHubSync.KeyPage);
            }
            catch (Exception e)
            {
                MessageBox.Show(this, "Браузер не открылся. Откройте адрес вручную:\n\n" + GitHubSync.KeyPage + "\n\n" + e.Message,
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }

    class MainForm : Form
    {
        const string DefaultHint = "Клик или цифра — отметить  •  двойной клик — выбрать и дальше  •  правый клик — посмотреть крупно";
        const string ReviewHint = "Клик или цифра — отметить для сравнения  •  Enter — сравнить отмеченные  •  правый клик — крупно  •  Esc — назад";
        const int ReviewPageSize = 12;

        class ReviewState
        {
            public Section Section;
            public List<ReviewItem> Items;
            public readonly HashSet<ReviewItem> Marked = new HashSet<ReviewItem>();
            public bool Comparing;
            public int Page;
            public int ListPage;

            public List<ReviewItem> Shown
            {
                get { return Comparing ? Items.Where(i => Marked.Contains(i)).ToList() : Items; }
            }

            public int Pages
            {
                get { return Math.Max(1, (Shown.Count + ReviewPageSize - 1) / ReviewPageSize); }
            }
        }

        public Func<Job, string> AskNote;

        readonly Session session;
        readonly GitHubSync sync;
        readonly Panel grid = new Panel();
        readonly ZoomView zoom = new ZoomView();
        readonly ProgressStrip strip = new ProgressStrip();
        readonly SectionList sections = new SectionList();
        readonly Label lblProgress = MakeLabel(Theme.Small, Theme.Dim);
        readonly Label lblTitle = MakeLabel(Theme.Title, Theme.Text);
        readonly Label lblGoes = MakeLabel(Theme.Normal, Color.FromArgb(120, 230, 140));
        readonly Label lblInfo = MakeLabel(Theme.Small, Theme.Dim);
        readonly Label lblHint = MakeLabel(Theme.Small, Theme.Dim);
        readonly Label lblFinished = MakeLabel(Theme.Title, Theme.Text);
        readonly FlatButton btnBack = new FlatButton("← Назад  [Backspace]", Theme.Panel);
        readonly FlatButton btnSkip = new FlatButton("Пропустить  [S]", Theme.Panel);
        readonly FlatButton btnNone = new FlatButton("Ни один не подходит  [0]", Theme.Panel);
        readonly FlatButton btnView = new FlatButton("", Theme.Panel);
        readonly FlatButton btnOpen = new FlatButton("Открыть папку с нужными", Theme.Panel);
        readonly FlatButton btnGit = new FlatButton("", Theme.Panel);
        readonly FlatButton btnNext = new FlatButton("Далее →  [Enter]", Theme.Accent);
        readonly FlatButton btnReview = new FlatButton("Выбранное  [V]", Theme.Panel);
        readonly FlatButton btnPrevPage = new FlatButton("← Страница", Theme.Panel);
        readonly FlatButton btnNextPage = new FlatButton("Страница →", Theme.Panel);
        readonly FlatButton btnRedo = new FlatButton("Выбрать заново  [R]", Theme.Panel);
        readonly FlatButton btnClose = new FlatButton("Назад  [Esc]", Theme.Panel);
        readonly FlatButton btnCompare = new FlatButton("", Theme.Accent);
        readonly List<Tile> tiles = new List<Tile>();

        Size aspect = new Size(1, 1);
        bool showCut = true;
        bool hintHeld;
        int loadToken;
        ReviewState review;
        Section returnTo;

        Job Current
        {
            get { return session.Queue.Count > 0 ? session.Queue[0] : null; }
        }

        static Label MakeLabel(Font font, Color color)
        {
            return new Label
            {
                Font = font,
                ForeColor = color,
                Dock = DockStyle.Top,
                Height = font.Height + 6,
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(12, 0, 12, 0)
            };
        }

        public MainForm(Session session, GitHubSync sync)
        {
            this.session = session;
            this.sync = sync;
            AskNote = ShowNoteDialog;
            Text = "Отбор вариантов — " + session.Root;
            BackColor = Theme.Back;
            ForeColor = Theme.Text;
            Font = Theme.Normal;
            MinimumSize = new Size(900, 600);
            Size = new Size(1280, 800);
            StartPosition = FormStartPosition.CenterScreen;
            WindowState = FormWindowState.Maximized;
            DoubleBuffered = true;

            grid.Dock = DockStyle.Fill;
            grid.BackColor = Theme.Back;
            grid.Resize += (a, b) => LayoutTiles();
            grid.Controls.Add(zoom);
            zoom.MouseDown += (a, b) => CloseZoom();
            lblFinished.Dock = DockStyle.Fill;
            lblFinished.TextAlign = ContentAlignment.MiddleCenter;
            lblFinished.Visible = false;
            grid.Controls.Add(lblFinished);

            strip.Dock = DockStyle.Top;
            strip.Height = 5;
            var header = new Panel { Dock = DockStyle.Top, BackColor = Theme.Panel };
            header.Height = strip.Height + lblProgress.Height + lblTitle.Height + lblGoes.Height + lblInfo.Height + 4;
            header.Controls.Add(lblInfo);
            header.Controls.Add(lblGoes);
            header.Controls.Add(lblTitle);
            header.Controls.Add(lblProgress);
            header.Controls.Add(strip);

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(8, 2, 8, 6),
                BackColor = Theme.Panel
            };
            buttons.Controls.AddRange(new Control[]
            {
                btnBack, btnSkip, btnNone, btnView, btnOpen, btnGit, btnReview, btnNext,
                btnPrevPage, btnNextPage, btnRedo, btnClose, btnCompare
            });
            lblHint.Dock = DockStyle.Bottom;
            lblHint.BackColor = Theme.Panel;

            Controls.Add(grid);
            Controls.Add(sections);
            Controls.Add(lblHint);
            Controls.Add(buttons);
            Controls.Add(header);

            btnNext.Click += (a, b) => Confirm();
            btnNone.Click += (a, b) => NoneFit();
            btnSkip.Click += (a, b) => Skip();
            btnBack.Click += (a, b) => Back();
            btnView.Click += (a, b) => ToggleView();
            btnOpen.Click += (a, b) => OpenNeeded();
            btnGit.Click += (a, b) => GitClicked();
            btnReview.Click += (a, b) => ShowPicked();
            btnPrevPage.Click += (a, b) => TurnPage(-1);
            btnNextPage.Click += (a, b) => TurnPage(1);
            btnRedo.Click += (a, b) => Redo();
            btnClose.Click += (a, b) => CloseReview();
            btnCompare.Click += (a, b) => ToggleCompare();
            sections.Picked += SectionClicked;
            sync.Changed += OnSyncChanged;
            Shown += (a, b) => ShowSync();

            ShowCurrent(null);
            ShowSync();
        }

        void OnSyncChanged()
        {
            try
            {
                if (IsHandleCreated) BeginInvoke(new Action(ShowSync));
            }
            catch (InvalidOperationException)
            {
            }
        }

        void ShowSync()
        {
            btnGit.Text = sync.Label;
            btnGit.ForeColor = sync.Detail != null || sync.NeedsKey ? Theme.Warn : Theme.Text;
            if (session.Warning == null && !hintHeld)
                SetHint(sync.Detail ?? (review != null ? ReviewHint : DefaultHint), sync.Detail != null);
        }

        public void PushStatus()
        {
            sync.Request(session.BuildStatus(), session.Queue.Count);
        }

        public void GitClicked()
        {
            if (!sync.NeedsKey)
            {
                sync.SendNow();
                return;
            }
            using (var dialog = new KeyDialog(sync.Detail))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    TokenStore.Save(dialog.Token);
                }
                catch (Exception e)
                {
                    ShowError("Ключ не сохранён на диск и будет действовать до закрытия программы.", e);
                }
                sync.SetToken(dialog.Token);
                PushStatus();
                sync.SendNow();
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (e.Cancel || !sync.HasUnsent) return;
            Text = "Отправляю разбор на GitHub…";
            Cursor = Cursors.WaitCursor;
            sync.Flush(10000);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            bool repeat = (msg.LParam.ToInt64() & 0x40000000) != 0;
            if (!repeat && HandleKey(keyData)) return true;
            return base.ProcessCmdKey(ref msg, keyData);
        }

        bool HandleKey(Keys key)
        {
            if (zoom.Visible)
            {
                switch (key)
                {
                    case Keys.Escape: CloseZoom(); return true;
                    case Keys.Left: StepZoom(-1); return true;
                    case Keys.Right: StepZoom(1); return true;
                }
            }
            int number = -1;
            if (key >= Keys.D0 && key <= Keys.D9) number = key - Keys.D0;
            if (key >= Keys.NumPad0 && key <= Keys.NumPad9) number = key - Keys.NumPad0;
            if (review != null) return HandleReviewKey(key, number);
            if (number == 0)
            {
                NoneFit();
                return true;
            }
            if (number > 0)
            {
                if (number <= tiles.Count) tiles[number - 1].Toggle();
                return true;
            }
            switch (key)
            {
                case Keys.Enter:
                case Keys.Space: Confirm(); return true;
                case Keys.Back: Back(); return true;
                case Keys.S: Skip(); return true;
                case Keys.A: ToggleView(); return true;
                case Keys.V: ShowPicked(); return true;
            }
            return false;
        }

        bool HandleReviewKey(Keys key, int number)
        {
            if (number >= 0)
            {
                if (number > 0 && number <= tiles.Count) tiles[number - 1].Toggle();
                return true;
            }
            switch (key)
            {
                case Keys.Enter:
                case Keys.Space: ToggleCompare(); return true;
                case Keys.Escape:
                case Keys.V: CloseReview(); return true;
                case Keys.Left:
                case Keys.PageUp: TurnPage(-1); return true;
                case Keys.Right:
                case Keys.PageDown: TurnPage(1); return true;
                case Keys.R: Redo(); return true;
                case Keys.A: ToggleView(); return true;
            }
            return false;
        }

        void ShowCurrent(ICollection<string> marked)
        {
            review = null;
            SetReviewMode(false);
            CloseZoom();
            ClearTiles();
            Job job = Current;
            int done = session.Done.Count;
            int total = session.Total;
            ShowProgress(job, job != null ? job.Section : null);
            hintHeld = false;
            string problem = session.Warning ?? sync.Detail;
            SetHint(problem ?? DefaultHint, problem != null);
            btnBack.Enabled = done > 0;
            btnNext.Enabled = btnSkip.Enabled = btnNone.Enabled = btnView.Enabled = job != null;
            btnOpen.Enabled = Directory.Exists(session.Abs(Session.NeedDir));
            btnReview.Enabled = done > 0;
            btnView.Text = (showCut ? "Показать как есть" : "Показать вырезанные") + "  [A]";

            lblFinished.Visible = job == null;
            if (job == null)
            {
                lblTitle.Text = "Готово";
                lblGoes.Text = "";
                lblInfo.Text = session.Root;
                lblFinished.Text = total == 0
                    ? "В этой папке нет вариантов для отбора."
                    : "Всё разобрано.\nНужные: " + Session.NeedDir + "   •   Ненужные: " + Session.TrashDir +
                      "\nСписок выбора: " + Session.ChoiceFile +
                      "   •   Что перегенерировать: " + Session.RegenFile;
                return;
            }

            lblTitle.Text = string.IsNullOrEmpty(job.Ru) ? job.Id : job.Ru;
            lblGoes.Text = string.IsNullOrEmpty(job.Goes) ? "" : "Куда идёт: " + job.Goes;
            var details = new List<string> { job.Id, job.Group };
            if (!string.IsNullOrEmpty(job.Asset)) details.Add(job.Asset);
            lblInfo.Text = string.Join("  •  ", details);

            aspect = CanvasSize(job.Variants[0]);
            int number = 1;
            foreach (Variant v in job.Variants)
            {
                var tile = new Tile(v, number++);
                tile.Marked = marked != null && marked.Contains(v.Name);
                tile.Changed += t => { if (zoom.Visible) zoom.Invalidate(); };
                tile.Confirmed += t => BeginInvoke(new Action(Confirm));
                tile.ZoomRequested += OpenZoom;
                tiles.Add(tile);
                grid.Controls.Add(tile);
            }
            LayoutTiles();
            StartLoad();
        }

        static string SectionText(Job job, List<SectionStat> stats)
        {
            if (job == null) return "";
            SectionStat stat = stats.First(s => s.Section == job.Section);
            return job.Section.Kind + ": " + job.Section.Name + ", " + (stat.Done + 1) + " из " + stat.Total + "   |   ";
        }

        void ShowProgress(Job job, Section highlighted)
        {
            int done = session.Done.Count;
            int total = session.Total;
            strip.Value = total > 0 ? done / (float)total : 1f;
            strip.Invalidate();
            List<SectionStat> stats = session.Stats();
            sections.SetData(stats, highlighted);
            lblProgress.Text = SectionText(job, stats) + "Разобрано " + done + " из " + total +
                               "  •  осталось " + session.Queue.Count + "  •  на перегенерацию: " + session.RegenCount;
        }

        // В разобранном разделе нечего разбирать, поэтому клик по нему открывает обзор.
        void SectionClicked(Section section)
        {
            if (session.Queue.Any(j => j.Section == section)) JumpTo(section);
            else if (review == null || review.Section != section) OpenReview(section);
        }

        void JumpTo(Section section)
        {
            if (review == null && Current.Section == section) return;
            returnTo = null;
            session.JumpTo(section);
            ShowCurrent(null);
        }

        // Сразу после конца раздела текущим уже стал следующий, поэтому открывается раздел последнего решения.
        void ShowPicked()
        {
            Job job = Current;
            if (job != null && session.Review(job.Section).Count > 0) OpenReview(job.Section);
            else if (session.Done.Count > 0) OpenReview(SectionMap.Of(session.Done[session.Done.Count - 1].Id).Section);
            else
            {
                hintHeld = true;
                SetHint("Пока нечего смотреть: ни одно задание не разобрано.", true);
            }
        }

        void OpenReview(Section section)
        {
            returnTo = null;
            review = new ReviewState { Section = section, Items = session.Review(section) };
            ShowReview();
        }

        void SetReviewMode(bool on)
        {
            foreach (Control c in new Control[] { btnBack, btnSkip, btnNone, btnOpen, btnReview, btnNext }) c.Visible = !on;
            foreach (Control c in new Control[] { btnRedo, btnClose, btnCompare }) c.Visible = on;
            if (!on) btnPrevPage.Visible = btnNextPage.Visible = false;
        }

        void ShowReview()
        {
            CloseZoom();
            ClearTiles();
            SetReviewMode(true);
            lblFinished.Visible = false;
            ShowProgress(null, review.Section);
            hintHeld = false;
            string problem = session.Warning ?? sync.Detail;
            SetHint(problem ?? ReviewHint, problem != null);

            List<ReviewItem> shown = review.Shown;
            int pages = review.Pages;
            review.Page = Math.Min(review.Page, pages - 1);
            btnPrevPage.Visible = btnNextPage.Visible = pages > 1;
            btnPrevPage.Enabled = review.Page > 0;
            btnNextPage.Enabled = review.Page < pages - 1;
            btnCompare.Text = (review.Comparing ? "Показать все" : "Сравнить отмеченные") + "  [Enter]";
            btnView.Enabled = true;
            btnView.Text = (showCut ? "Показать как есть" : "Показать вырезанные") + "  [A]";

            int rejected = review.Items.Count(i => i.Variant == null);
            lblTitle.Text = (review.Comparing ? "Сравнение: " : "Выбранное: ") + review.Section.Name;
            lblGoes.Text = "Выбрано вариантов: " + (review.Items.Count - rejected) + "  •  на перегенерации: " + rejected;
            lblInfo.Text = review.Section.Kind + (pages > 1 ? "  •  страница " + (review.Page + 1) + " из " + pages : "");

            ReviewItem picture = shown.FirstOrDefault(i => i.Variant != null);
            aspect = picture != null ? CanvasSize(picture.Variant) : new Size(1, 1);
            int number = 1;
            foreach (ReviewItem item in shown.Skip(review.Page * ReviewPageSize).Take(ReviewPageSize))
                AddReviewTile(item, number++);
            LayoutTiles();
            StartLoad();
        }

        void AddReviewTile(ReviewItem item, int number)
        {
            Variant variant = item.Variant ?? new Variant { Name = item.Decision.Id };
            string name = Regex.Replace(item.Ru ?? item.Decision.Id, @"\s*\([^)]*\)", "");
            var tile = new Tile(variant, number)
            {
                Title = item.Variant != null ? variant.Label + "   " + name : name,
                MarkText = "✓",
                Marked = review.Marked.Contains(item)
            };
            Action<Tile> remember = t =>
            {
                if (t.Marked) review.Marked.Add(item);
                else review.Marked.Remove(item);
                if (zoom.Visible) zoom.Invalidate();
            };
            tile.Changed += remember;
            tile.Confirmed += remember;
            tile.ZoomRequested += OpenZoom;
            tiles.Add(tile);
            grid.Controls.Add(tile);
            if (item.Variant == null)
            {
                string note = item.Decision.Note;
                tile.SetImage(null, false, "На перегенерации" + (string.IsNullOrEmpty(note) ? "" : ": " + note));
            }
        }

        void ToggleCompare()
        {
            if (!review.Comparing && review.Marked.Count < 2)
            {
                hintHeld = true;
                SetHint("Отметьте хотя бы два варианта: клик по картинке или цифра.", true);
                return;
            }
            if (review.Comparing) review.Page = review.ListPage;
            else
            {
                review.ListPage = review.Page;
                review.Page = 0;
            }
            review.Comparing = !review.Comparing;
            ShowReview();
        }

        void CloseReview()
        {
            if (review.Comparing) ToggleCompare();
            else ShowCurrent(null);
        }

        void TurnPage(int delta)
        {
            int page = review.Page + delta;
            if (page < 0 || page >= review.Pages) return;
            review.Page = page;
            ShowReview();
        }

        void Redo()
        {
            List<Decision> chosen = review.Marked.Select(i => i.Decision).Distinct().ToList();
            if (chosen.Count != 1)
            {
                hintHeld = true;
                SetHint("Отметьте вариант одного задания: оно откроется заново со всеми вариантами.", true);
                return;
            }
            Section section = review.Section;
            try
            {
                session.Reopen(chosen[0]);
            }
            catch (Exception e)
            {
                ShowError("Не удалось вернуть задание.", e);
                return;
            }
            PushStatus();
            ShowCurrent(chosen[0].Picked);
            returnTo = section;
            hintHeld = true;
            SetHint("Выберите заново. После «Далее» откроется обзор раздела.", false);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (sections.Bounds.Contains(PointToClient(Cursor.Position))) sections.Wheel(e.Delta);
        }

        Size CanvasSize(Variant v)
        {
            try
            {
                using (var f = OpenShared(session.Abs(v.Raw ?? v.Show)))
                {
                    var header = new byte[24];
                    if (f.Read(header, 0, 24) == 24)
                    {
                        int w = header[16] << 24 | header[17] << 16 | header[18] << 8 | header[19];
                        int h = header[20] << 24 | header[21] << 16 | header[22] << 8 | header[23];
                        if (w > 0 && h > 0) return new Size(w, h);
                    }
                }
            }
            catch (IOException) { }
            return new Size(1, 1);
        }

        void ClearTiles()
        {
            loadToken++;
            foreach (Tile t in tiles)
            {
                grid.Controls.Remove(t);
                t.Dispose();
            }
            tiles.Clear();
        }

        void LayoutTiles()
        {
            int n = tiles.Count;
            if (n == 0) return;
            const int gap = 10;
            int width = grid.ClientSize.Width;
            int height = grid.ClientSize.Height;
            int caption = Tile.CaptionHeight;
            int columns = 1;
            float best = 0;
            for (int c = 1; c <= n; c++)
            {
                int r = (n + c - 1) / c;
                float w = (width - gap * (c + 1)) / (float)c;
                float h = (height - gap * (r + 1)) / (float)r - caption;
                if (w <= 0 || h <= 0) continue;
                float scale = Math.Min(w / aspect.Width, h / aspect.Height);
                if (scale > best)
                {
                    best = scale;
                    columns = c;
                }
            }
            int rows = (n + columns - 1) / columns;
            int tileWidth = Math.Max(1, (width - gap * (columns + 1)) / columns);
            int tileHeight = Math.Max(1, (height - gap * (rows + 1)) / rows);
            for (int i = 0; i < n; i++)
            {
                tiles[i].Bounds = new Rectangle(
                    gap + (i % columns) * (tileWidth + gap),
                    gap + (i / columns) * (tileHeight + gap),
                    tileWidth, tileHeight);
            }
        }

        // BeginInvoke возможен только при живом окне: до этого момента результат загрузки был бы потерян.
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            StartLoad();
        }

        void StartLoad()
        {
            if (!IsHandleCreated) return;
            int token = ++loadToken;
            foreach (Tile tile in tiles.Where(x => x.V.Show != null))
            {
                Tile t = tile;
                string rel = showCut ? t.V.Show : t.V.Raw ?? t.V.Show;
                bool transparent = rel == t.V.Cut;
                string path = session.Abs(rel);
                ThreadPool.QueueUserWorkItem(state =>
                {
                    Bitmap bitmap = null;
                    string error = null;
                    try
                    {
                        bitmap = Decode(path);
                    }
                    catch (Exception e)
                    {
                        error = e.Message;
                    }
                    Bitmap result = bitmap;
                    try
                    {
                        BeginInvoke(new Action(() =>
                        {
                            if (token == loadToken && !t.IsDisposed) t.SetImage(result, transparent, error);
                            else if (result != null) result.Dispose();
                        }));
                    }
                    catch (InvalidOperationException)
                    {
                        if (result != null) result.Dispose();
                    }
                });
            }
        }

        // FileShare.Delete: перенос файла не должен падать, пока его ещё читает загрузка картинки.
        static FileStream OpenShared(string path)
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }

        static Bitmap Decode(string path)
        {
            using (FileStream file = OpenShared(path))
            using (Image source = Image.FromStream(file, false, false))
            {
                var bitmap = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppPArgb);
                bitmap.SetResolution(96, 96);
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.DrawImage(source, 0, 0, source.Width, source.Height);
                }
                return bitmap;
            }
        }

        void SetHint(string text, bool warning)
        {
            lblHint.Text = text;
            lblHint.ForeColor = warning ? Theme.Warn : Theme.Dim;
        }

        void Confirm()
        {
            Job job = Current;
            if (job == null) return;
            List<string> picked = tiles.Where(t => t.Marked).Select(t => t.V.Name).ToList();
            if (picked.Count == 0)
            {
                hintHeld = true;
                SetHint("Сначала отметьте вариант. Если подходящего нет — нажмите «Ни один не подходит».", true);
                return;
            }
            Commit(job, picked, null);
        }

        void NoneFit()
        {
            Job job = Current;
            if (job == null) return;
            string note = AskNote(job);
            if (note != null) Commit(job, new List<string>(), note);
        }

        string ShowNoteDialog(Job job)
        {
            using (var dialog = new NoteDialog(string.IsNullOrEmpty(job.Ru) ? job.Id : job.Ru))
                return dialog.ShowDialog(this) == DialogResult.OK ? dialog.Note : null;
        }

        void Commit(Job job, List<string> picked, string note)
        {
            CloseZoom();
            loadToken++;
            try
            {
                session.Decide(job, picked, note);
            }
            catch (Exception e)
            {
                ShowError("Файлы задания не перенесены, оно осталось на месте.", e);
                StartLoad();
                return;
            }
            PushStatus();
            if (returnTo != null)
            {
                OpenReview(returnTo);
                return;
            }
            ShowCurrent(null);
            if (session.Warning == null && sync.Detail == null && session.Queue.All(j => j.Section != job.Section))
            {
                hintHeld = true;
                SetHint("Раздел «" + job.Section.Name + "» закончен. Посмотреть выбранное: V или клик по разделу слева.", false);
            }
        }

        void Skip()
        {
            returnTo = null;
            if (session.Queue.Count < 2)
            {
                if (Current != null)
                {
                    hintHeld = true;
                    SetHint("Это последнее неразобранное задание.", true);
                }
                return;
            }
            session.Skip();
            ShowCurrent(null);
        }

        void Back()
        {
            returnTo = null;
            Decision last;
            try
            {
                last = session.Undo();
            }
            catch (Exception e)
            {
                ShowError("Не удалось вернуть предыдущее задание.", e);
                return;
            }
            if (last == null) return;
            PushStatus();
            ShowCurrent(last.Picked);
        }

        void ToggleView()
        {
            if (Current == null && review == null) return;
            showCut = !showCut;
            btnView.Text = (showCut ? "Показать как есть" : "Показать вырезанные") + "  [A]";
            CloseZoom();
            StartLoad();
        }

        void OpenNeeded()
        {
            string dir = session.Abs(Session.NeedDir);
            if (Directory.Exists(dir)) Process.Start("explorer.exe", "\"" + dir + "\"");
        }

        void OpenZoom(Tile tile)
        {
            if (tile.Image == null) return;
            zoom.Shown = tile;
            zoom.Visible = true;
            zoom.BringToFront();
            zoom.Invalidate();
        }

        void CloseZoom()
        {
            zoom.Visible = false;
            zoom.Shown = null;
        }

        void StepZoom(int delta)
        {
            int i = tiles.IndexOf(zoom.Shown);
            if (i < 0) return;
            Tile next = tiles[(i + delta + tiles.Count) % tiles.Count];
            if (next.Image != null) OpenZoom(next);
        }

        void ShowError(string title, Exception e)
        {
            MessageBox.Show(this, title + "\n\n" + e.Message, "Отбор вариантов", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    static class Program
    {
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main(string[] args)
        {
            SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string root = FindResults(args);
            if (root == null) return;
            Session session;
            try
            {
                session = new Session(root);
            }
            catch (Exception e)
            {
                MessageBox.Show("Не удалось открыть папку " + root + "\n\n" + e.Message, "Отбор вариантов",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            var sync = new GitHubSync(TokenStore.Load(), Session.PcId);
            sync.Start();
            var form = new MainForm(session, sync);
            form.Shown += (a, b) =>
            {
                form.PushStatus();
                if (sync.NeedsKey) form.GitClicked();
            };
            Application.Run(form);
        }

        static string FindResults(string[] args)
        {
            bool asked = args.Length > 0 && Directory.Exists(args[0]);
            string found = Session.ResultsOf(asked ? args[0] : AppDomain.CurrentDomain.BaseDirectory);
            if (found != null) return found;
            using (var dialog = new FolderBrowserDialog { Description = "Где лежит папка results?" })
                return dialog.ShowDialog() == DialogResult.OK ? Session.ResultsOf(dialog.SelectedPath) ?? dialog.SelectedPath : null;
        }
    }
}
