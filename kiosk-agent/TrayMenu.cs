using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace ClassManager.KioskAgent
{
    /// <summary>
    /// 托盘右键菜单：与教师端（班主任端）完全同一套 GDI 自绘样式
    /// （白底、12px 圆角、淡蓝悬停、图标 + 文字）。
    /// 菜单项：打开设置 / 在浏览器中打开 / 开机自启 / 重启服务 / 退出。
    /// </summary>
    internal sealed class TrayMenu : Form
    {
        private const int Radius = 12;
        private const int RowWidth = 208;
        private const int RowHeight = 38;

        private static readonly Color TextColor = Color.FromArgb(31, 41, 55);
        private static readonly Color Accent = Color.FromArgb(37, 99, 235);
        private static readonly Color HoverBg = Color.FromArgb(239, 244, 255);
        private static readonly Color BorderColor = Color.FromArgb(226, 232, 240);
        private static readonly Color Danger = Color.FromArgb(185, 28, 28);
        private static readonly Color DangerHoverBg = Color.FromArgb(254, 242, 242);
        private static readonly Color Separator = Color.FromArgb(241, 245, 249);

        private readonly List<Control> _rows = new();

        public event Action? OpenRequested;
        public event Action? OpenBrowserRequested;
        public event Action? ToggleAutoStartRequested;
        public event Action? RestartRequested;
        public event Action? ExitRequested;

        public TrayMenu(bool autoStartEnabled)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Color.White;
            DoubleBuffered = true;
            Padding = new Padding(8);
            AutoScaleMode = AutoScaleMode.None;
            KeyPreview = true;
            Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point);

            AddRow("打开设置", "\uE713", () => OpenRequested?.Invoke(), bold: true, accent: true);
            AddRow("在浏览器中打开", "\uE774", () => OpenBrowserRequested?.Invoke());

            AddSeparator();

            AddRow("开机自启", "\uE768", () => ToggleAutoStartRequested?.Invoke(),
                   checkedState: autoStartEnabled);
            AddRow("重启服务", "\uE72C", () => RestartRequested?.Invoke());

            AddSeparator();

            AddRow("退出", "\uE7E8", () => ExitRequested?.Invoke(), danger: true);

            LayoutRows();
        }

        private void AddRow(string text, string glyph, Action onClick, bool bold = false,
                            bool accent = false, bool danger = false, bool? checkedState = null)
        {
            var row = new MenuRow(text, glyph, bold, accent, danger, checkedState)
            {
                Width = RowWidth,
                Height = RowHeight,
                Cursor = Cursors.Hand
            };
            row.Clicked += () =>
            {
                Hide();
                Close();
                Application.DoEvents();
                try { onClick(); } catch { }
            };
            _rows.Add(row);
        }

        private void AddSeparator()
        {
            _rows.Add(new SeparatorRow { Width = RowWidth, Height = 11 });
        }

        private void LayoutRows()
        {
            int y = Padding.Top;
            foreach (var row in _rows)
            {
                row.Left = Padding.Left;
                row.Top = y;
                y += row.Height;
                Controls.Add(row);
            }
            Width = RowWidth + Padding.Horizontal + 2;
            Height = y + Padding.Bottom + 2;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ClassStyle |= 0x00020000;
                return cp;
            }
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            using var path = Rounded(new Rectangle(0, 0, Width, Height), Radius);
            var old = Region;
            Region = new Region(path);
            old?.Dispose();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(BorderColor);
            using var path = Rounded(new Rectangle(0, 0, Width - 1, Height - 1), Radius);
            e.Graphics.DrawPath(pen, path);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { Hide(); Close(); }
            base.OnKeyDown(e);
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            Hide();
            Close();
        }

        internal static GraphicsPath Rounded(Rectangle r, int radius)
        {
            int d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private sealed class MenuRow : Control
        {
            private readonly string _text;
            private readonly string _glyph;
            private readonly bool _bold;
            private readonly Color _fg;
            private readonly Color _hover;
            private readonly bool? _checkedState;
            private bool _hot;

            public event Action? Clicked;

            public MenuRow(string text, string glyph, bool bold, bool accent, bool danger, bool? checkedState = null)
            {
                _text = text;
                _glyph = glyph;
                _bold = bold;
                _checkedState = checkedState;
                _fg = danger ? Danger : accent ? Accent : TextColor;
                _hover = danger ? DangerHoverBg : HoverBg;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                         ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
                BackColor = Color.White;
            }

            protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { _hot = false; Invalidate(); base.OnMouseLeave(e); }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                if (e.Button == MouseButtons.Left) Clicked?.Invoke();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

                if (_hot)
                {
                    using var bg = new SolidBrush(_hover);
                    using var path = Rounded(new Rectangle(0, 0, Width - 1, Height - 1), 8);
                    g.FillPath(bg, path);
                }

                using var iconFont = new Font("Segoe MDL2 Assets", 10.5f, FontStyle.Regular, GraphicsUnit.Point);
                using var iconBrush = new SolidBrush(_fg);
                var iconRect = new Rectangle(10, 0, 20, Height);
                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(_glyph, iconFont, iconBrush, iconRect, sf);

                using var font = new Font("Microsoft YaHei UI", 9.5f,
                    _bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Point);
                using var textBrush = new SolidBrush(_fg);
                var textRect = new Rectangle(36, 0, Width - 44, Height);
                var sf2 = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center };
                g.DrawString(_text, font, textBrush, textRect, sf2);

                // 勾选态（开机自启）：右侧画对勾
                if (_checkedState == true)
                {
                    using var tickFont = new Font("Segoe MDL2 Assets", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
                    using var tickBrush = new SolidBrush(Accent);
                    var tickRect = new Rectangle(Width - 34, 0, 22, Height);
                    var sf3 = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    g.DrawString("\uE73E", tickFont, tickBrush, tickRect, sf3);
                }
            }
        }

        private sealed class SeparatorRow : Control
        {
            public SeparatorRow()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
                BackColor = Color.White;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                using var pen = new Pen(Separator);
                int y = Height / 2;
                e.Graphics.DrawLine(pen, 8, y, Width - 8, y);
            }
        }
    }
}
