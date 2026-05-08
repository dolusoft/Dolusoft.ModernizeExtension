using System;
using System.Runtime.InteropServices;
using System.Threading;
using Dolusoft.ModernizeExtension.Commands;
using Dolusoft.ModernizeExtension.Infrastructure;
using Dolusoft.ModernizeExtension.Options;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace Dolusoft.ModernizeExtension;

[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[Guid(PackageGuidString)]
[ProvideMenuResource("Menus.ctmenu", 1)]
[ProvideAutoLoad("{f1536ef8-92ec-443c-9ed7-fdadf150da82}", PackageAutoLoadFlags.BackgroundLoad)]
[ProvideOptionPage(typeof(ModernizeOptions), "Dolusoft C# Modernizer", "General", 0, 0, true)]
[ProvideImageManifest]
public sealed class ModernizePackage : AsyncPackage
{
    public const string PackageGuidString = "E826BF7B-245A-4D9F-BD21-E21D23F5D411";

    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        await ModernizeCommand.InitializeAsync(this);
    }
}
