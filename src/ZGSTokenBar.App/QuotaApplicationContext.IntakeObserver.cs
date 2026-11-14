namespace ZGSTokenBar.App;

internal sealed partial class QuotaApplicationContext
{
    private void ShowIntakeObserver()
    {
        if (_quitting) return;
        if (_intakeObserver is { IsDisposed: false } opened)
        {
            if (opened.WindowState == FormWindowState.Minimized) opened.WindowState = FormWindowState.Normal;
            opened.Activate();
            return;
        }
        _intakeObserver = new(_text.IsChinese);
        _intakeObserver.Show();
    }
}
