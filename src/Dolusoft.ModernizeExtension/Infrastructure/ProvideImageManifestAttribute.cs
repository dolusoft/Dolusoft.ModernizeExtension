using System;
using Microsoft.VisualStudio.Shell;

namespace Dolusoft.ModernizeExtension.Infrastructure;

/// <summary>
/// Registers the extension's image manifest with the VS Image Service so
/// theme-aware icons are served from it at runtime.
/// Generates a pkgdef entry under [$RootKey$\VisualStudio\ImageManifests].
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
internal sealed class ProvideImageManifestAttribute : RegistrationAttribute
{
    private const string ImageManifestsKey = @"VisualStudio\ImageManifests";
    private const string EntryName         = "Dolusoft.ModernizeExtension";
    private const string ManifestPath      = @"$PackageFolder$\Resources\ModernizeImages.imagemanifest";

    public override void Register(RegistrationContext context)
    {
        using var key = context.CreateKey(ImageManifestsKey);
        key.SetValue(EntryName, ManifestPath);
    }

    public override void Unregister(RegistrationContext context)
    {
        context.RemoveValue(ImageManifestsKey, EntryName);
    }
}
