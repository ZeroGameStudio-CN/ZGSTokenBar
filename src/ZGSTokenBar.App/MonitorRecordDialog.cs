namespace ZGSTokenBar.App;

internal sealed class MonitorRecordDialog : Form
{
    internal MonitorRecordDialog(string caption, string title, string summary, string record, bool chinese = true)
    {
        Text = caption;
        Font = SystemFonts.MessageBoxFont;
        var lineCount = string.IsNullOrEmpty(summary) ? 0 : summary.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Length;
        ClientSize = new Size(540, Math.Clamp(140 + lineCount * 22, 320, 520));
        MinimumSize = new Size(420, 280);
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
        AppendFormattedSummary(body, summary);
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

    private void AppendFormattedSummary(RichTextBox body, string summary)
    {
        if (string.IsNullOrEmpty(summary)) return;
        using var sectionFont = new Font(Font.FontFamily, Font.Size, FontStyle.Bold);
        var lines = summary.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0) body.AppendText(Environment.NewLine);
            var line = lines[i];
            if (line.Length == 0) continue;
            if (line.StartsWith("── ", StringComparison.Ordinal))
            {
                body.SelectionFont = sectionFont;
                body.SelectionColor = UiTokens.Accent;
                body.AppendText(line);
                continue;
            }
            if (line.StartsWith("提示：", StringComparison.Ordinal) || line.StartsWith("Note:", StringComparison.Ordinal))
            {
                body.SelectionFont = Font;
                body.SelectionColor = UiTokens.Warning;
                body.AppendText(line);
                continue;
            }
            if (TrySplitFormattedPair(line, out var keyPart, out var valuePart))
            {
                body.SelectionFont = Font;
                body.SelectionColor = UiTokens.Muted;
                body.AppendText(keyPart);
                body.SelectionFont = Font;
                body.SelectionColor = UiTokens.TextStrong;
                body.AppendText(valuePart);
                continue;
            }
            body.SelectionFont = Font;
            body.SelectionColor = ForeColor;
            body.AppendText(line);
        }
    }

    private static bool TrySplitFormattedPair(string line, out string keyPart, out string valuePart)
    {
        var twoSpaces = line.IndexOf("  ", StringComparison.Ordinal);
        if (twoSpaces is > 0 and <= 14 && !char.IsWhiteSpace(line[0]))
        {
            keyPart = line[..twoSpaces];
            valuePart = line[twoSpaces..];
            return true;
        }
        var colon = line.IndexOf('：');
        if (colon is > 0 and <= 12 && !char.IsWhiteSpace(line[0]) && colon + 1 < line.Length)
        {
            keyPart = line[..(colon + 1)];
            valuePart = line[(colon + 1)..];
            return true;
        }
        foreach (var prefix in new[] { "排队 ", "资源占用 ", "派发前检查 ", "派发恢复等待 ", "原生测试 ", "恢复执行 ", "恢复等待 ", "清理 ", "槽位总占用 " })
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                keyPart = prefix;
                valuePart = line[prefix.Length..];
                return true;
            }
        }
        keyPart = "";
        valuePart = "";
        return false;
    }
}
