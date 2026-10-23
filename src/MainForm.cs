using System;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace ChargeLimit
{
    class MainForm : Form
    {
        const int ERROR_FILE_NOT_FOUND = 2, ERROR_ACCESS_DENIED = 5;

        readonly Label status = new Label();
        readonly CheckBox enable = new CheckBox();
        readonly TrackBar target = new TrackBar();
        readonly Label targetValue = new Label();
        readonly NumericUpDown delta = new NumericUpDown();
        readonly Label summary = new Label();
        readonly Label applied = new Label();
        readonly Label error = new Label();
        readonly Button apply = new Button();
        readonly Timer timer = new Timer();

        public MainForm()
        {
            Text = Strings.Title;
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(12);

            BuildLayout();
            LoadSettings();

            enable.CheckedChanged += delegate { UpdateControls(); };
            target.ValueChanged += delegate { UpdateControls(); };
            delta.ValueChanged += delegate { UpdateControls(); };
            apply.Click += delegate { Apply(); };
            UpdateControls();
            CheckDriver();

            timer.Interval = 3000;
            timer.Tick += delegate { RefreshStatus(); };
            timer.Start();
            RefreshStatus();
        }

        void BuildLayout()
        {
            TableLayoutPanel grid = new TableLayoutPanel();
            grid.AutoSize = true;
            grid.ColumnCount = 2;
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            status.AutoSize = true;
            status.Font = new Font(Font.FontFamily, Font.Size + 2, FontStyle.Bold);
            status.Margin = new Padding(3, 0, 3, 12);
            AddWide(grid, status, 0);

            enable.Text = Strings.EnableLimit;
            enable.AutoSize = true;
            enable.Margin = new Padding(3, 3, 3, 8);
            AddWide(grid, enable, 1);

            target.Minimum = Limits.MinTarget;
            target.Maximum = Limits.MaxTarget;
            target.TickFrequency = 5;
            target.SmallChange = 1;
            target.LargeChange = 5;
            target.Width = 220;
            targetValue.AutoSize = true;
            grid.Controls.Add(MakeLabel(Strings.StopAt), 0, 2);
            grid.Controls.Add(Row(target, targetValue), 1, 2);

            delta.Minimum = Limits.MinDelta;
            delta.Maximum = Limits.MaxDelta;
            delta.Width = 60;
            grid.Controls.Add(MakeLabel(Strings.ResumeAfter), 0, 3);
            grid.Controls.Add(Row(delta, MakeLabel(Strings.Points)), 1, 3);

            summary.AutoSize = true;
            summary.ForeColor = SystemColors.GrayText;
            summary.Margin = new Padding(3, 10, 3, 3);
            AddWide(grid, summary, 4);

            applied.AutoSize = true;
            applied.ForeColor = SystemColors.GrayText;
            AddWide(grid, applied, 5);

            error.AutoSize = true;
            error.ForeColor = Color.Firebrick;
            error.MaximumSize = new Size(420, 0);
            error.Margin = new Padding(3, 10, 3, 3);
            error.Visible = false;
            AddWide(grid, error, 6);

            apply.Text = Strings.Apply;
            apply.AutoSize = true;
            apply.Anchor = AnchorStyles.Right;
            apply.Margin = new Padding(3, 12, 3, 3);
            AddWide(grid, apply, 7);
            AcceptButton = apply;

            Controls.Add(grid);
        }

        static void AddWide(TableLayoutPanel grid, Control control, int row)
        {
            grid.Controls.Add(control, 0, row);
            grid.SetColumnSpan(control, 2);
        }

        static FlowLayoutPanel Row(params Control[] controls)
        {
            FlowLayoutPanel row = new FlowLayoutPanel();
            row.AutoSize = true;
            row.WrapContents = false;
            row.Margin = Padding.Empty;
            foreach (Control c in controls)
            {
                c.Anchor = AnchorStyles.Left;
                row.Controls.Add(c);
            }
            return row;
        }

        static Label MakeLabel(string text)
        {
            Label label = new Label();
            label.Text = text;
            label.AutoSize = true;
            label.Anchor = AnchorStyles.Left;
            return label;
        }

        void LoadSettings()
        {
            Settings s = Settings.Load();
            enable.Checked = s.Limited;
            target.Value = s.Limited ? s.Target : Math.Min(s.LastLimit, Limits.MaxTarget - 1);
            delta.Value = Limits.ClampDelta(target.Value, s.Delta);
            ShowApplied(s.Known, s.Target, s.Delta);
        }

        void UpdateControls()
        {
            delta.Maximum = Math.Max(Limits.MinDelta, Limits.MaxDeltaFor(target.Value));
            targetValue.Text = target.Value + "%";
            target.Enabled = delta.Enabled = enable.Checked;
            summary.Text = enable.Checked
                ? string.Format(Strings.SummaryLimited, target.Value, target.Value - (int)delta.Value)
                : Strings.SummaryUnlimited;
        }

        void CheckDriver()
        {
            try { Native.CheckChargeControlAvailable(); }
            catch (Win32Exception e)
            {
                error.Text = Describe(e);
                error.Visible = true;
                apply.Enabled = false;
            }
        }

        void RefreshStatus()
        {
            try
            {
                BatteryState s = Native.ReadBattery();
                string state = !s.Online ? Strings.OnBattery : s.Charging ? Strings.Charging : Strings.NotCharging;
                string watts = s.RateMw != 0 ? string.Format(" · {0:0.0} W", Math.Abs(s.RateMw) / 1000.0) : "";
                status.Text = string.Format(Strings.Battery, s.Percent, state, watts);
            }
            catch (Win32Exception e)
            {
                status.Text = string.Format(Strings.BatteryUnreadable, e.Message);
            }
        }

        void ShowApplied(bool known, int appliedTarget, int appliedDelta)
        {
            if (!known)
                applied.Text = Strings.AppliedNone;
            else if (appliedTarget >= Limits.MaxTarget)
                applied.Text = Strings.AppliedUnlimited;
            else
                applied.Text = string.Format(Strings.AppliedLimited, appliedTarget, appliedTarget - appliedDelta);
        }

        void Apply()
        {
            int newTarget = enable.Checked ? target.Value : Limits.MaxTarget;
            int newDelta = (int)delta.Value;
            try
            {
                Native.SetChargeLimit(newTarget, newDelta);
            }
            catch (Win32Exception e)
            {
                MessageBox.Show(this, string.Format(Strings.ApplyFailed, Describe(e)), Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            Settings.SaveApplied(newTarget, newDelta);
            ShowApplied(true, newTarget, newDelta);
            RefreshStatus();
        }

        static string Describe(Win32Exception e)
        {
            if (e.NativeErrorCode == ERROR_FILE_NOT_FOUND) return Strings.Unsupported;
            if (e.NativeErrorCode == ERROR_ACCESS_DENIED) return Strings.NeedsAdmin;
            return e.Message;
        }
    }
}
