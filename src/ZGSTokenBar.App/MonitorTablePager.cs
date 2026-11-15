namespace ZGSTokenBar.App;

/// <summary>Page the bounded read-only result already in memory, retaining row identities and diagnostics.</summary>
internal sealed class MonitorTablePager
{
    private readonly RouterGrid _grid;
    private readonly RouterButton _previous = new() { Icon = RouterButtonIcon.Previous, AccessibleName = "上一页 / Previous page" };
    private readonly RouterButton _next = new() { Icon = RouterButtonIcon.Next, AccessibleName = "下一页 / Next page" };
    private readonly Label _page = new() { Width = 64, Height = 26, TextAlign = ContentAlignment.MiddleCenter };
    private int _offset;
    private bool _queued, _applying;
    internal Control[] Buttons => [_previous, _page, _next];

    internal MonitorTablePager(RouterGrid grid)
    {
        _grid = grid;
        _previous.Click += (_, _) => Move(-1);
        _next.Click += (_, _) => Move(1);
        _grid.PageRequested += (_, pages) => Move(pages);
        _grid.RowsAdded += (_, _) => Queue();
        _grid.RowsRemoved += (_, _) => { if (_grid.RowCount == 0) _offset = 0; Queue(); };
        _grid.SizeChanged += (_, _) => Queue();
        _grid.HandleCreated += (_, _) => Queue();
    }

    private int Capacity => Math.Max(1, (_grid.ClientSize.Height - _grid.ColumnHeadersHeight - 2)
        / Math.Max(1, _grid.RowTemplate.Height));

    private void Move(int pages)
    {
        var size = Capacity;
        _offset = Math.Clamp(_offset + pages * size, 0, Math.Max(0, (_grid.RowCount - 1) / size * size));
        Apply();
    }

    private void Queue()
    {
        if (_queued || _applying || !_grid.IsHandleCreated || _grid.IsDisposed) return;
        _queued = true;
        _grid.BeginInvoke(() => { _queued = false; if (!_grid.IsDisposed) Apply(); });
    }

    private void Apply()
    {
        if (_applying || _grid.IsDisposed) return;
        _applying = true;
        try
        {
            var size = Capacity;
            _offset = Math.Min(_offset, Math.Max(0, (_grid.RowCount - 1) / size * size));
            var current = _grid.CurrentCell;
            _grid.CurrentCell = null;
            for (var i = 0; i < _grid.RowCount; i++) _grid.Rows[i].Visible = i >= _offset && i < _offset + size;
            if (current?.OwningRow is { Visible: true } && current.DataGridView == _grid)
                _grid.CurrentCell = current;
            var scale = RouterVisualGeometry.Scale(_grid);
            _page.Width = (int)(64 * scale); _page.Height = (int)(26 * scale);
            _previous.Enabled = _offset > 0; _next.Enabled = _offset + size < _grid.RowCount;
            _page.Text = _grid.RowCount == 0 ? "0/0" : $"{_offset / size + 1}/{(_grid.RowCount + size - 1) / size}";
        }
        finally { _applying = false; }
    }
}
