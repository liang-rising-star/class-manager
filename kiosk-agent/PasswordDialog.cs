using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace ClassManager.KioskAgent
{
    /// <summary>
    /// 退出确认弹窗：沿用设置页的视觉语言（白底、圆角、蓝色主色、灰色次按钮）。
    /// </summary>
    internal sealed class PasswordDialog : Form
    {
        private static readonly Color TextColor = Color.FromArgb(26, 34, 51);
        private static readonly Color MutedColor = Color.FromArgb(107, 119, 136);
        private static readonly Color BorderColor = Color.FromArgb(227, 232, 240);
        private static readonly Color Primary = Color.FromArgb(220, 38, 38);       // 退出＝危险操作
        private static readonly Color PrimaryHover = Color.FromArgb(185, 28, 28);
        private static readonly Color GhostBg = Color.FromArgb(241, 244, 249);
        private static readonly Color GhostHover = Color.FromArgb(230, 235, 243);

        private readonly RoundTextBox _input;
        private readonly Label _error;
        private readonly Func<string, bool> _verify;

        public PasswordDialog(Func<string, bool> verify)
        {
            _verify = verify;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.White;
            ClientSize = new Size(470, 330);
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;

            var title = new Label
            {
                Text = "退出需要密码",
                Font = new Font("Microsoft YaHei UI", 15f, FontStyle.Bold, GraphicsUnit.Point),
                ForeColor = TextColor,
                AutoSize = true,
                Location = new Point(32, 30),
                BackColor = Color.Transparent
            };

            var hint = new Label
            {
                Text = "输入一体机端的访问密码后退出",
                Font = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Regular, GraphicsUnit.Point),
                ForeColor = MutedColor,
                AutoSize = true,
                Location = new Point(32, 76),
                BackColor = Color.Transparent
            };

            _input = new RoundTextBox
            {
                Location = new Point(32, 116),
                Size = new Size(406, 54)
            };

            _error = new Label
            {
                Text = "",
                Font = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Regular, GraphicsUnit.Point),
                ForeColor = Color.FromArgb(220, 38, 38),
                AutoSize = true,
                Location = new Point(32, 184),
                BackColor = Color.Transparent
            };

            var cancel = new RoundButton("取消", GhostBg, GhostHover, Color.FromArgb(71, 85, 105))
            {
                Location = new Point(200, 250),
                Size = new Size(112, 46)
            };
            cancel.Clicked += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

            var ok = new RoundButton("退出", Primary, PrimaryHover, Color.White)
            {
                Location = new Point(326, 250),
                Size = new Size(112, 46)
            };
            ok.Clicked += (_, _) => TrySubmit();

            Controls.AddRange(new Control[] { title, hint, _input, _error, cancel, ok });

            KeyPreview = true;
            KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
                else if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; TrySubmit(); }
            };

            Shown += (_, _) => _input.FocusInput();
        }

        private void TrySubmit()
        {
            if (_verify(_input.Value))
            {
                DialogResult = DialogResult.OK;
                Close();
                return;
            }

            _error.Text = "密码不正确，请重试";
            _input.SelectAll();
            _input.FocusInput();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ClassStyle |= 0x00020000;    // CS_DROPSHADOW
                return cp;
            }
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            using var path = TrayMenu.Rounded(new Rectangle(0, 0, Width, Height), 12);
            var old = Region;
            Region = new Region(path);
            old?.Dispose();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(BorderColor);
            using var path = TrayMenu.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), 12);
            e.Graphics.DrawPath(pen, path);
        }

        // ---------------- 圆角输入框 ----------------

        private sealed class RoundTextBox : Control
        {
            private readonly TextBox _box;
            private bool _focused;

            public RoundTextBox()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                         ControlStyles.UserPaint, true);
                BackColor = Color.White;

                _box = new TextBox
                {
                    BorderStyle = BorderStyle.None,
                    UseSystemPasswordChar = true,
                    Font = new Font("Microsoft YaHei UI", 13f, FontStyle.Regular, GraphicsUnit.Point),
                    ForeColor = TextColor
                };
                _box.GotFocus += (_, _) => { _focused = true; Invalidate(); };
                _box.LostFocus += (_, _) => { _focused = false; Invalidate(); };
                _box.KeyDown += (_, e) =>
                {
                    if (e.KeyCode != Keys.Enter) return;
                    e.SuppressKeyPress = true;
                    Parent?.SelectNextControl(this, true, true, true, true);
                };
                Controls.Add(_box);
            }

            public string Value => _box.Text;
            public void SelectAll() => _box.SelectAll();
            public void FocusInput() => _box.Focus();

            protected override void OnResize(EventArgs e)
            {
                base.OnResize(e);
                _box.SetBounds(16, (Height - _box.PreferredHeight) / 2, Width - 32, _box.PreferredHeight);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;

                using var bg = new SolidBrush(Color.FromArgb(251, 252, 254));
                using var path = TrayMenu.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), 10);
                g.FillPath(bg, path);

                using var pen = new Pen(_focused ? Color.FromArgb(37, 99, 235) : BorderColor);
                g.DrawPath(pen, path);
            }
        }

        // ---------------- 圆角按钮 ----------------

        private sealed class RoundButton : Control
        {
            private readonly string _text;
            private readonly Color _fill;
            private readonly Color _hover;
            private readonly Color _textColor;
            private bool _hot;

            public event EventHandler? Clicked;

            public RoundButton(string text, Color fill, Color hover, Color textColor)
            {
                _text = text;
                _fill = fill;
                _hover = hover;
                _textColor = textColor;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                         ControlStyles.UserPaint, true);
                BackColor = Color.White;
                Cursor = Cursors.Hand;
                Font = new Font("Microsoft YaHei UI", 11.5f, FontStyle.Bold, GraphicsUnit.Point);
            }

            protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { _hot = false; Invalidate(); base.OnMouseLeave(e); }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                if (e.Button == MouseButtons.Left) Clicked?.Invoke(this, EventArgs.Empty);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

                using var brush = new SolidBrush(_hot ? _hover : _fill);
                using var path = TrayMenu.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), 9);
                g.FillPath(brush, path);

                using var textBrush = new SolidBrush(_textColor);
                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(_text, Font, textBrush, ClientRectangle, sf);
            }
        }
    }
}
