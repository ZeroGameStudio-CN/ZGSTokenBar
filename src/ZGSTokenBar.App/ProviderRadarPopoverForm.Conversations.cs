using System.Text;
using ZGSTokenBar.Core;

namespace ZGSTokenBar.App;

internal sealed partial class ProviderRadarPopoverForm
{
    private CodexConversationQuery _conversationQuery = new();
    private CodexConversationView? _conversationView;
    private readonly TextBox _conversationSearch = new() { BorderStyle = BorderStyle.None, MaxLength = 160 };
    private readonly ContextMenuStrip _conversationMenu = new() { ShowImageMargin = false, ShowCheckMargin = true };
    private Font? _conversationSearchFont;
    private bool _settingConversationSearch, _conversationDialog;
    internal CodexConversationView? ConversationView => _conversationView;

    private void InitializeConversationTools()
    {
        Controls.Add(_conversationSearch);
        _conversationSearch.AccessibleName = "Search conversation history";
        _conversationSearch.Visible = false;
        _conversationSearch.BackColor = Color.FromArgb(22, 27, 39);
        _conversationSearch.ForeColor = Color.FromArgb(226, 232, 240);
        _conversationSearch.TextChanged += (_, _) =>
        {
            if (!_settingConversationSearch) SetConversationQuery(_conversationQuery with { Search = _conversationSearch.Text });
        };
        _conversationSearch.MouseDown += (_, _) =>
        {
            PinConversationInteraction();
            if (IsHandleCreated && !IsDisposed) BeginInvoke(() =>
            { if (!IsDisposed && _conversationSearch.CanFocus) { Activate(); _conversationSearch.Focus(); } });
        };
        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (!_usagePrimary || !_pinned) return;
            if (e.Control && e.KeyCode == Keys.F) { _conversationSearch.Focus(); e.SuppressKeyPress = true; }
            if (e.Control && e.KeyCode == Keys.E) { ExportConversationDialog(); e.SuppressKeyPress = true; }
        };
    }

    private void RefreshConversationView()
    {
        _conversationView = _usagePrimary
            ? CodexConversationView.Create(_tokenUsage, _conversationQuery, DateTimeOffset.UtcNow) : null;
        _displayTurns = _conversationView?.Rows ?? _tokenUsage?.ConversationTurns ?? [];
        _modelSpeeds = _conversationView is null ? []
            : CodexConversationSpeeds.BuildRows(_conversationView.Rows, DateTimeOffset.UtcNow);
    }

    internal void SetConversationQuery(CodexConversationQuery query)
    {
        _conversationQuery = query;
        _settingConversationSearch = true;
        try { if (_conversationSearch.Text != query.Search) _conversationSearch.Text = query.Search; }
        finally { _settingConversationSearch = false; }
        RefreshConversationView();
        _recentTurnOffset = 0;
        _hoveredRecentIndex = -1;
        _groupToolTip.SetToolTip(this, null);
        if (_usagePrimary)
        {
            _layout = BuildUsageLayout(_layout.Dpi);
            if (Visible) ApplyCurrentPlacement();
        }
        Invalidate();
    }

    private void PlaceConversationSearch()
    {
        _conversationSearch.Visible = _usagePrimary && !_historyVisible && !_layout.ConversationSearchBounds.IsEmpty;
        if (!_conversationSearch.Visible) return;
        var body = RadarPopoverRenderer.BodyBounds(_layout, _tailSide);
        var scale = _layout.Dpi / 96f;
        var bounds = _layout.ConversationSearchBounds;
        bounds.Offset(body.Location);
        bounds.Inflate(-(int)(7 * scale), -(int)(5 * scale));
        var pixels = 11.5f * scale;
        if (_conversationSearchFont is null || Math.Abs(_conversationSearchFont.Size - pixels) > .01)
        {
            var old = _conversationSearchFont;
            _conversationSearchFont = new Font("Microsoft YaHei UI", pixels, FontStyle.Regular, GraphicsUnit.Pixel);
            _conversationSearch.Font = _conversationSearchFont;
            _conversationMenu.Font = _conversationSearchFont;
            old?.Dispose();
        }
        _conversationSearch.Bounds = bounds;
        _conversationSearch.PlaceholderText = _text.CodexHistorySearch;
        _conversationSearch.AccessibleName = _text.CodexHistorySearch;
        _groupToolTip.SetToolTip(_conversationSearch, _text.CodexHistoryHint);
        _conversationSearch.BringToFront();
    }

    private void PinConversationInteraction()
    {
        if (!_pinned)
        {
            _pinned = true;
            if (IsHandleCreated) RecreateHandle();
        }
        DetailsInteraction?.Invoke(this, EventArgs.Empty);
    }

    private bool ConversationToolsHover(Point point)
    {
        if (!_usagePrimary) return false;
        if (_layout.ConversationToolbarBounds.Contains(point) || _layout.ConversationSummaryBounds.Contains(point)
            || _layout.ConversationHeaderAt(point) >= 0)
        {
            var caption = _layout.ConversationExportBounds.Contains(point) ? _text.CodexExportHistory
                : _layout.ConversationModelBounds.Contains(point) ? _conversationQuery.Model ?? _text.CodexAllModels
                : _layout.ConversationProjectBounds.Contains(point) ? _conversationQuery.Project ?? _text.CodexAllProjects
                : _text.CodexHistoryHint;
            _groupToolTip.SetToolTip(this, caption);
            Cursor = _layout.ConversationSummaryBounds.Contains(point) ? Cursors.Default : Cursors.Hand;
            _hoveredRecentIndex = -4;
            return true;
        }
        return false;
    }

    private bool ConversationToolsClick(Point point)
    {
        if (!_usagePrimary) return false;
        if (_layout.ConversationHeaderAt(point) is var column && column >= 0)
        {
            PinConversationInteraction();
            SetConversationQuery(_conversationQuery with
            {
                SortColumn = column,
                Descending = column == _conversationQuery.SortColumn ? !_conversationQuery.Descending : column >= 2,
            });
            return true;
        }
        if (!_layout.ConversationToolbarBounds.Contains(point)) return false;
        PinConversationInteraction();
        if (_layout.ConversationExportBounds.Contains(point)) { ExportConversationDialog(); return true; }
        if (_layout.ConversationSearchBounds.Contains(point)) { Activate(); _conversationSearch.Focus(); return true; }
        foreach (var old in _conversationMenu.Items.Cast<ToolStripItem>().ToArray()) old.Dispose();
        _conversationMenu.Items.Clear();
        _conversationMenu.Font = _conversationSearch.Font;
        _conversationMenu.Renderer = new DarkContextMenuRenderer(Color.FromArgb(17, 21, 29), Color.FromArgb(27, 34, 46),
            Color.FromArgb(50, 61, 78), Color.FromArgb(226, 232, 240), Color.FromArgb(148, 163, 184));
        var anchor = _layout.ConversationRangeBounds;
        void Item(string caption, bool selected, Action action)
        {
            var item = new ToolStripMenuItem(caption.Replace("&", "&&")) { Checked = selected };
            item.Click += (_, _) => action();
            _conversationMenu.Items.Add(item);
        }
        if (_layout.ConversationRangeBounds.Contains(point))
        {
            foreach (var period in Enum.GetValues<CodexHistoryPeriod>())
                Item(_text.CodexHistoryPeriod(period), period == _conversationQuery.Period,
                    () => SetConversationQuery(_conversationQuery with { Period = period }));
        }
        else
        {
            var models = _layout.ConversationModelBounds.Contains(point);
            anchor = models ? _layout.ConversationModelBounds : _layout.ConversationProjectBounds;
            var current = models ? _conversationQuery.Model : _conversationQuery.Project;
            void Select(string? value) => SetConversationQuery(models ? _conversationQuery with { Model = value } : _conversationQuery with { Project = value });
            Item(models ? _text.CodexAllModels : _text.CodexAllProjects, current is null, () => Select(null));
            var values = CodexConversationDisplay.Select(_tokenUsage).Select(turn => models ? turn.Model : turn.ProjectName)
                .Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>().Distinct(StringComparer.Ordinal)
                .Order(StringComparer.OrdinalIgnoreCase);
            foreach (var value in values) Item(value, value == current, () => Select(value));
        }
        var origin = RadarPopoverRenderer.BodyBounds(_layout, _tailSide).Location;
        _conversationMenu.Show(this, new Point(origin.X + anchor.Left, origin.Y + anchor.Bottom));
        return true;
    }

    internal void ExportConversationCsv(string path) => File.WriteAllText(path,
        (_conversationView ?? CodexConversationView.Create(_tokenUsage, _conversationQuery, DateTimeOffset.UtcNow)).Csv(), new UTF8Encoding(true));

    private async void ExportConversationDialog()
    {
        if (_conversationDialog) return;
        PinConversationInteraction();
        _conversationDialog = true;
        try
        {
            using var dialog = new SaveFileDialog
            {
                Title = _text.CodexExportHistory, Filter = "CSV (*.csv)|*.csv", DefaultExt = "csv",
                FileName = $"codex-retained-turns-{DateTime.Now:yyyyMMdd-HHmmss}.csv", OverwritePrompt = true,
            };
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                var csv = (_conversationView ?? CodexConversationView.Create(_tokenUsage, _conversationQuery, DateTimeOffset.UtcNow)).Csv();
                UseWaitCursor = true;
                await Task.Run(() => File.WriteAllText(dialog.FileName, csv, new UTF8Encoding(true)));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        { if (!IsDisposed) MessageBox.Show(this, error.Message, _text.CodexExportHistory, MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { _conversationDialog = false; if (!IsDisposed) UseWaitCursor = false; }
    }

    private string ConversationSpeedTooltip() => _text.CodexFilteredSpeedHint + Environment.NewLine
        + string.Join(Environment.NewLine, CodexConversationSpeeds.GroupModels(_modelSpeeds).SelectMany(group => group.Rows)
            .Select(row => _text.CodexModelSpeedLabel(row) + Environment.NewLine + _text.CodexModelSpeedValue(row)));

    private void DisposeConversationTools()
    {
        _conversationMenu.Dispose();
        _conversationSearchFont?.Dispose();
    }
}
