using System.ComponentModel;
using Microsoft.VisualStudio.Shell;

namespace Dolusoft.ModernizeExtension.Options;

public sealed class ModernizeOptions : DialogPage
{
    [Category("Transformers")]
    [DisplayName("Global Usings")]
    [Description("Consolidates common using directives into GlobalUsings.cs per project.")]
    [DefaultValue(true)]
    public bool GlobalUsings { get; set; } = true;

    [Category("Transformers")]
    [DisplayName("File-Scoped Namespace")]
    [Description("Converts block-scoped namespaces to file-scoped namespace declarations.")]
    [DefaultValue(true)]
    public bool FileScopedNamespace { get; set; } = true;

    [Category("Transformers")]
    [DisplayName("Primary Constructor")]
    [Description("Converts single-constructor DI pattern to primary constructor syntax.")]
    [DefaultValue(true)]
    public bool PrimaryConstructor { get; set; } = true;

    [Category("Transformers")]
    [DisplayName("Directory.Build.props")]
    [Description("Extracts shared MSBuild properties to a solution-level Directory.Build.props file.")]
    [DefaultValue(true)]
    public bool DirectoryBuildProps { get; set; } = true;

    [Category("Transformers")]
    [DisplayName("Collection Initializers")]
    [Description("Converts consecutive Add() calls to collection initializer syntax.")]
    [DefaultValue(true)]
    public bool CollectionInitializers { get; set; } = true;

    [Category("Transformers")]
    [DisplayName("Collection Expressions")]
    [Description("Converts collection initializers to C# 12 collection expression syntax [...].")]
    [DefaultValue(true)]
    public bool CollectionExpressions { get; set; } = true;

    [Category("Transformers")]
    [DisplayName("Simplify New Expression")]
    [Description("Replaces new T() with target-typed new() where the type can be inferred.")]
    [DefaultValue(true)]
    public bool SimplifyNewExpression { get; set; } = true;
}
