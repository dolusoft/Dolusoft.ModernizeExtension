using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Linq;
using System.Threading.Tasks;
using Dolusoft.ModernizeExtension.Engine;
using Dolusoft.ModernizeExtension.Infrastructure;
using EnvDTE;
using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace Dolusoft.ModernizeExtension.Commands;

internal sealed class ModernizeCommand
{
    private const int CmdModernizeFile     = 0x0100;
    private const int CmdModernizeProject  = 0x0101;
    private const int CmdModernizeSolution = 0x0102;

    public static readonly Guid CommandSet = new("A13EF785-0610-4286-B993-A95AF1C28D2E");

    private readonly AsyncPackage _package;

    private ModernizeCommand(AsyncPackage package, OleMenuCommandService cs)
    {
        _package = package;
        AddCommand(cs, CmdModernizeFile,     OnModernizeFile,     OnQueryFile);
        AddCommand(cs, CmdModernizeProject,  OnModernizeProject,  OnQueryProject);
        AddCommand(cs, CmdModernizeSolution, OnModernizeSolution, OnQuerySolution);
    }

    public static async Task InitializeAsync(AsyncPackage package)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
        var cs = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
        if (cs != null)
            new ModernizeCommand(package, cs);
    }

    private void AddCommand(OleMenuCommandService cs, int id, EventHandler execute, EventHandler queryStatus)
    {
        var cmd = new OleMenuCommand(execute, new CommandID(CommandSet, id));
        cmd.BeforeQueryStatus += queryStatus;
        cs.AddCommand(cmd);
    }

    // ── Query status ───────────────────────────────────────────────────────

    private void OnQueryFile(object? sender, EventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var cmd = (OleMenuCommand)sender!;
        var path = GetSelectedFilePath();
        cmd.Visible = path?.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) == true;
        cmd.Enabled = cmd.Visible;
    }

    private void OnQueryProject(object? sender, EventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var cmd = (OleMenuCommand)sender!;
        cmd.Visible = GetSelectedProject() != null;
        cmd.Enabled = cmd.Visible;
    }

    private void OnQuerySolution(object? sender, EventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var cmd = (OleMenuCommand)sender!;
        var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
        cmd.Visible = dte?.Solution?.IsOpen == true;
        cmd.Enabled = cmd.Visible;
    }

    // ── Execute ────────────────────────────────────────────────────────────

    private void OnModernizeFile(object? sender, EventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var path = GetSelectedFilePath();
        if (path == null) return;

        _package.JoinableTaskFactory.RunAsync(async () =>
        {
            var workspace = await GetWorkspaceAsync();
            var scope = workspace.CurrentSolution.Projects
                .SelectMany(p => p.Documents)
                .Where(d => string.Equals(d.FilePath, path, StringComparison.OrdinalIgnoreCase))
                .Select(d => d.Id)
                .ToList();

            if (scope.Count == 0) return;
            await RunEngineAsync(workspace, scope, ModernizationScope.File);
        }).FileAndForget("Dolusoft.ModernizeExtension/File");
    }

    private void OnModernizeProject(object? sender, EventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var project = GetSelectedProject();
        if (project == null) return;
        var projectFile = project.FileName;

        _package.JoinableTaskFactory.RunAsync(async () =>
        {
            var workspace = await GetWorkspaceAsync();
            var roslynProject = workspace.CurrentSolution.Projects
                .FirstOrDefault(p => string.Equals(p.FilePath, projectFile, StringComparison.OrdinalIgnoreCase));
            if (roslynProject == null) return;

            var scope = roslynProject.Documents.Select(d => d.Id).ToList();
            await RunEngineAsync(workspace, scope, ModernizationScope.Project);
        }).FileAndForget("Dolusoft.ModernizeExtension/Project");
    }

    private void OnModernizeSolution(object? sender, EventArgs e)
    {
        _package.JoinableTaskFactory.RunAsync(async () =>
        {
            var workspace = await GetWorkspaceAsync();
            var scope = workspace.CurrentSolution.Projects
                .SelectMany(p => p.Documents)
                .Select(d => d.Id)
                .ToList();

            await RunEngineAsync(workspace, scope, ModernizationScope.Solution);
        }).FileAndForget("Dolusoft.ModernizeExtension/Solution");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private async Task RunEngineAsync(VisualStudioWorkspace workspace, IReadOnlyList<DocumentId> scope, ModernizationScope modernizationScope)
    {
        // IVsStatusbar must be obtained on the main thread
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var statusBar = Package.GetGlobalService(typeof(SVsStatusbar)) as IVsStatusbar;

        using var progress = new ProgressReporter(statusBar, _package.JoinableTaskFactory);
        var engine = new ModernizationEngine(workspace, progress);
        await engine.RunAsync(workspace.CurrentSolution, scope, modernizationScope, _package.DisposalToken);
    }

    private string? GetSelectedFilePath()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
        var items = dte?.SelectedItems;
        if (items == null || items.Count != 1) return null;
        return items.Item(1).ProjectItem?.FileNames[1];
    }

    private EnvDTE.Project? GetSelectedProject()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
        var items = dte?.SelectedItems;
        if (items == null || items.Count != 1) return null;
        return items.Item(1).Project;
    }

    private async Task<VisualStudioWorkspace> GetWorkspaceAsync()
    {
        var componentModel = await _package.GetServiceAsync(typeof(SComponentModel)) as IComponentModel
            ?? throw new InvalidOperationException("IComponentModel service is not available.");
        return componentModel.GetService<VisualStudioWorkspace>();
    }
}
