namespace ZGSTokenBar.App;

internal sealed class MonitorRecordDialog : Form
{
    internal MonitorRecordDialog(string caption, string title, string summary, string record, bool chinese = true)
    {
        Text = caption;
        Font = SystemFonts.MessageBoxFont;
        ClientSize = new Size(496, 312);
        MinimumSize = new Size(380, 260);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        ShowIcon = false;
        MinimizeBox = false;
        MaximizeBox = false;
        BackColor = UiTokens.CardSurface;
        ForeColor = UiTokens.Text;

        var body = new RichTextBox
        {
            Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, Margin = Padding.Empty,
            BackColor = BackColor, ForeColor = ForeColor, DetectUrls = false, ScrollBars = RichTextBoxScrollBars.Vertical,
            AccessibleName = caption,
        };
        using (var heading = new Font(Font.FontFamily, 14, FontStyle.Bold))
        {
            body.SelectionFont = heading;
            body.SelectionColor = UiTokens.TextStrong;
            body.AppendText(title + Environment.NewLine + Environment.NewLine);
        }
        body.SelectionFont = Font;
        body.SelectionColor = ForeColor;
        body.AppendText(summary);
        body.Select(0, 0);

        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = Padding.Empty,
        };
        var close = new RouterButton
        {
            Text = chinese ? "关闭" : "Close", DialogResult = DialogResult.Cancel, Kind = RouterButtonKind.Primary,
        };
        var copy = new RouterButton { Text = chinese ? "复制 JSON" : "Copy JSON" };
        var feedback = new Label { AutoSize = true, ForeColor = UiTokens.Muted, Margin = new Padding(10, 6, 10, 0) };
        copy.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(record);
                feedback.Text = chinese ? "已复制" : "Copied";
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                feedback.Text = chinese ? "剪贴板暂不可用" : "Clipboard unavailable";
            }
        };
        footer.Controls.AddRange([close, copy, feedback]);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 2 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.Controls.Add(body, 0, 0);
        layout.Controls.Add(footer, 0, 1);
        Controls.Add(layout);
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        CancelButton = close;
        ActiveControl = close;
    }
}
