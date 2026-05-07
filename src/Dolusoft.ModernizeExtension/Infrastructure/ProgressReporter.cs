using System;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;

namespace Dolusoft.ModernizeExtension.Infrastructure;

internal sealed class ProgressReporter : IDisposable
{
    private readonly IVsStatusbar? _statusBar;
    private readonly JoinableTaskFactory _jtf;
    private uint _cookie;

    public ProgressReporter(IVsStatusbar? statusBar, JoinableTaskFactory jtf)
    {
        _statusBar = statusBar;
        _jtf       = jtf;
    }

    public void Report(string message, uint current, uint total)
    {
        if (_statusBar == null) return;
        // Fire-and-forget — progress updates are non-critical
        _jtf.RunAsync(async () =>
        {
            await _jtf.SwitchToMainThreadAsync();
            _statusBar.Progress(ref _cookie, 1, message, current, total);
        }).FileAndForget("Dolusoft.ModernizeExtension/Progress");
    }

    public void Dispose()
    {
        if (_statusBar == null) return;
        // Block until the final status bar update completes
        _jtf.Run(async () =>
        {
            await _jtf.SwitchToMainThreadAsync();
            _statusBar.Progress(ref _cookie, 0, string.Empty, 0, 0);
            _statusBar.SetText("Modernization complete.");
        });
    }
}
