using System.Drawing.Drawing2D;
using System.Reflection;
using System.ComponentModel;
using System.Runtime.InteropServices;
using ZGSTokenBar.Core;
using ZGSTokenBar.PluginSdk;

namespace ZGSTokenBar.App;

/// <summary>Settings rows: the composable row components (value, toggle, palette, action, about, note).</summary>
internal sealed partial class SettingsForm
{
    private abstract class SettingsRow : Panel
    {
        protected readonly SettingsTheme Theme;
        protected readonly float ScaleFactor;

        protected SettingsRow(SettingsTheme theme, float scale, int logicalHeight)
        {
            Theme = theme;
            ScaleFactor = scale;
            BackColor = theme.Content;
            Height = Scale(logicalHeight);
            Margin = Padding.Empty;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        }

        protected int Scale(int value) => Math.Max(1, (int)Math.Round(value * ScaleFactor));

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Theme.Content);
            using var divider = new Pen(Theme.Border);
            e.Graphics.DrawLine(divider, 0, Height - 1, Width, Height - 1);
        }

        protected static Label CreateTitle(string text, SettingsTheme theme, float scale) => new()
        {
            AutoSize = false,
            BackColor = theme.Content,
            Font = ScaledFont("Segoe UI Semibold", 10f, FontStyle.Regular, scale),
            ForeColor = theme.Text,
            Text = text,
            TextAlign = ContentAlignment.MiddleLeft,
            UseMnemonic = false,
        };

        protected static Label CreateDescription(string text, SettingsTheme theme, float scale) => new()
        {
            AutoSize = false,
            BackColor = theme.Content,
            Font = ScaledFont("Segoe UI", 8.75f, FontStyle.Regular, scale),
            ForeColor = theme.Muted,
            Text = text,
            TextAlign = ContentAlignment.TopLeft,
            AutoEllipsis = true,
            UseMnemonic = false,
        };
    }

    private sealed class ValueSettingRow : SettingsRow
    {
        private readonly Label _title;
        private readonly Label _description;
        private readonly SettingsComboBox _field;
        private readonly int _fieldWidth;

        public ValueSettingRow(
            string title,
            string description,
            SettingsComboBox field,
            int fieldWidth,
            SettingsTheme theme,
            float scale)
            : base(theme, scale, 76)
        {
            _title = CreateTitle(title, theme, scale);
            _description = CreateDescription(description, theme, scale);
            _field = field;
            _fieldWidth = fieldWidth;
            Controls.Add(_title);
            Controls.Add(_description);
            Controls.Add(_field);
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            if (_title is null || _description is null || _field is null) return;
            var gap = Scale(20);
            var fieldHeight = Scale(36);
            var fieldWidth = Math.Min(_fieldWidth, Math.Max(Scale(132), Width * 2 / 5));
            var textWidth = Math.Max(1, Width - fieldWidth - gap);
            _title.SetBounds(0, Scale(11), textWidth, Scale(24));
            _description.SetBounds(0, _title.Bottom - Scale(1), textWidth, Scale(26));
            _field.SetBounds(Width - fieldWidth, Math.Max(0, (Height - fieldHeight) / 2), fieldWidth, fieldHeight);
        }
    }

    private sealed class ToggleSettingRow : SettingsRow
    {
        private readonly Label _title;
        private readonly Label _description;
        private readonly ToggleSwitch _toggle;

        public ToggleSettingRow(
            ToggleSwitch toggle,
            string title,
            string description,
            SettingsTheme theme,
            float scale)
            : base(theme, scale, 76)
        {
            _toggle = toggle;
            _title = CreateTitle(title, theme, scale);
            _description = CreateDescription(description, theme, scale);
            Controls.Add(_title);
            Controls.Add(_description);
            Controls.Add(_toggle);
            _title.Cursor = Cursors.Hand;
            _title.Click += (_, _) => _toggle.Activate();
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            if (_title is null || _description is null || _toggle is null) return;
            var gap = Scale(20);
            var textWidth = Math.Max(1, Width - _toggle.Width - gap);
            _title.SetBounds(0, Scale(11), textWidth, Scale(24));
            _description.SetBounds(0, _title.Bottom - Scale(1), textWidth, Scale(26));
            _toggle.Location = new Point(Width - _toggle.Width, Math.Max(0, (Height - _toggle.Height) / 2));
        }
    }

    private sealed class SectionNote : SettingsRow
    {
        private readonly Label _label;

        /// <summary>A note row sized to its text: 18 px per line between the 12 px top and 10 px bottom padding.</summary>
        public SectionNote(string text, SettingsTheme theme, float scale, int lines = 2)
            : base(theme, scale, 20 + 18 * Math.Clamp(lines, 1, 3))
        {
            _label = CreateDescription(text, theme, scale);
            Controls.Add(_label);
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            if (_label is null) return;
            _label.SetBounds(0, Scale(12), Width, Math.Max(1, Height - Scale(20)));
        }
    }

    private sealed class PaletteSettingRow : SettingsRow
    {
        private readonly Label _title;
        private readonly Label _description;
        private readonly PaletteGrid _grid;

        public PaletteSettingRow(
            string title,
            string description,
            PaletteGrid grid,
            SettingsTheme theme,
            float scale)
            : base(theme, scale, 112)
        {
            _title = CreateTitle(title, theme, scale);
            _description = CreateDescription(description, theme, scale);
            _grid = grid;
            Controls.Add(_title);
            Controls.Add(_description);
            Controls.Add(_grid);
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            if (_title is null || _description is null || _grid is null) return;
            var gap = Scale(20);
            if (Width < Scale(440))
            {
                Height = Scale(164);
                _title.SetBounds(0, Scale(10), Width, Scale(24));
                _description.SetBounds(0, _title.Bottom - Scale(1), Width, Scale(24));
                _grid.SetBounds(0, Scale(68), Width, Scale(82));
            }
            else
            {
                Height = Scale(112);
                var labelWidth = Math.Max(Scale(160), Width * 2 / 5);
                _title.SetBounds(0, Scale(18), labelWidth, Scale(24));
                _description.SetBounds(0, _title.Bottom - Scale(1), labelWidth, Scale(40));
                _grid.SetBounds(labelWidth + gap, Scale(16), Math.Max(1, Width - labelWidth - gap), Scale(80));
            }
        }
    }

    private sealed class ActionSettingRow : SettingsRow
    {
        private readonly Label _title;
        private readonly Label _description;
        private readonly Label _runtime;
        private readonly RoundedButton _button;

        public ActionSettingRow(
            string title,
            string description,
            string runtime,
            RoundedButton button,
            SettingsTheme theme,
            float scale)
            : base(theme, scale, 88)
        {
            _title = CreateTitle(title, theme, scale);
            _description = CreateDescription(description, theme, scale);
            _runtime = CreateDescription(runtime, theme, scale);
            _button = button;
            _title.AccessibleName = title;
            _description.AccessibleName = description;
            _runtime.AccessibleName = runtime;
            Controls.Add(_title);
            Controls.Add(_description);
            Controls.Add(_runtime);
            Controls.Add(_button);
        }

        public void SetRuntime(string runtime)
        {
            _runtime.Text = runtime;
            _runtime.AccessibleName = runtime;
            AccessibleDescription = runtime;
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            if (_title is null || _description is null || _runtime is null || _button is null) return;
            var gap = Scale(20);
            var textWidth = Math.Max(1, Width - _button.Width - gap);
            _title.SetBounds(0, Scale(8), textWidth, Scale(23));
            _description.SetBounds(0, _title.Bottom - Scale(1), textWidth, Scale(25));
            _runtime.SetBounds(0, _description.Bottom - Scale(2), textWidth, Scale(23));
            _button.Location = new Point(Width - _button.Width, Math.Max(0, (Height - _button.Height) / 2));
        }
    }

    private sealed class AboutRow : SettingsRow
    {
        private readonly Label _product;
        private readonly Label _version;
        private readonly Label _description;
        private readonly Label _privacy;

        public AboutRow(
            string version,
            string description,
            string privacy,
            SettingsTheme theme,
            float scale)
            : base(theme, scale, 154)
        {
            _product = new Label
            {
                AutoSize = false,
                BackColor = theme.Content,
                Font = ScaledFont("Segoe UI Semibold", 12f, FontStyle.Regular, scale),
                ForeColor = theme.Text,
                Text = "ZGSTokenBar",
                TextAlign = ContentAlignment.MiddleLeft,
            };
            _version = CreateDescription(version, theme, scale);
            _version.ForeColor = theme.Accent;
            _description = CreateDescription(description, theme, scale);
            _privacy = CreateDescription(privacy, theme, scale);
            Controls.Add(_product);
            Controls.Add(_version);
            Controls.Add(_description);
            Controls.Add(_privacy);
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            if (_product is null || _version is null || _description is null || _privacy is null) return;
            var versionWidth = Math.Min(Scale(130), Width / 3);
            _product.SetBounds(0, Scale(14), Math.Max(1, Width - versionWidth), Scale(30));
            _version.SetBounds(Width - versionWidth, Scale(15), versionWidth, Scale(28));
            _description.SetBounds(0, Scale(54), Width, Scale(34));
            _privacy.SetBounds(0, Scale(96), Width, Scale(42));
        }
    }
}
