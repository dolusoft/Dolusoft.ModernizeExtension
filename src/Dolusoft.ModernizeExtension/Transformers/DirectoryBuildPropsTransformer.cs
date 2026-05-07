using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Dolusoft.ModernizeExtension.Engine;

namespace Dolusoft.ModernizeExtension.Transformers;

/// <summary>
/// Creates (or updates) Directory.Build.props at the solution root with properties
/// that are identical across all projects in scope.
/// Removes the promoted properties from individual .csproj files.
/// </summary>
internal sealed class DirectoryBuildPropsTransformer : IProjectTransformer
{
    public string Name => "Directory.Build.props";

    // Properties that are candidates for promotion to Directory.Build.props
    private static readonly HashSet<string> CandidateProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "TargetFramework",
        "TargetFrameworks",
        "Nullable",
        "ImplicitUsings",
        "LangVersion",
        "Deterministic",
        "TreatWarningsAsErrors",
        "EnforceCodeStyleInBuild",
        "AnalysisLevel",
        "RootNamespace",
    };

    public Task TransformAsync(string solutionDirectory, IReadOnlyList<string> projectPaths, CancellationToken ct)
    {
        if (projectPaths.Count == 0) return Task.CompletedTask;

        var propsPath = Path.Combine(solutionDirectory, "Directory.Build.props");

        // Load all project XML documents
        var projectDocs = projectPaths
            .Where(File.Exists)
            .Select(p => (Path: p, Doc: XDocument.Load(p)))
            .ToList();

        if (projectDocs.Count == 0) return Task.CompletedTask;

        // Gather candidate property values from each project
        // key = property name, value = set of values found
        var propertyValues = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (_, doc) in projectDocs)
        {
            foreach (var pg in doc.Root?.Elements("PropertyGroup") ?? Enumerable.Empty<XElement>())
            {
                foreach (var prop in pg.Elements())
                {
                    if (!CandidateProperties.Contains(prop.Name.LocalName)) continue;
                    if (!propertyValues.TryGetValue(prop.Name.LocalName, out var values))
                        propertyValues[prop.Name.LocalName] = values = new HashSet<string>(StringComparer.Ordinal);
                    values.Add(prop.Value.Trim());
                }
            }
        }

        // Only promote properties with exactly one unique value across all projects
        // that have that property set
        var toPromote = propertyValues
            .Where(kv => kv.Value.Count == 1)
            .ToDictionary(kv => kv.Key, kv => kv.Value.First(), StringComparer.OrdinalIgnoreCase);

        if (toPromote.Count == 0) return Task.CompletedTask;

        // Load or create Directory.Build.props
        XDocument propsDoc;
        if (File.Exists(propsPath))
        {
            propsDoc = XDocument.Load(propsPath);
        }
        else
        {
            propsDoc = new XDocument(
                new XDeclaration("1.0", "utf-8", null),
                new XElement("Project"));
        }

        var root = propsDoc.Root!;
        var propsGroup = root.Element("PropertyGroup");
        if (propsGroup == null)
        {
            propsGroup = new XElement("PropertyGroup");
            root.Add(propsGroup);
        }

        foreach (var kv in toPromote)
        {
            var existing = propsGroup.Element(kv.Key);
            if (existing != null)
                existing.Value = kv.Value;
            else
                propsGroup.Add(new XElement(kv.Key, kv.Value));
        }

        propsDoc.Save(propsPath);

        // Remove promoted properties from each .csproj
        foreach (var (path, doc) in projectDocs)
        {
            var modified = false;
            foreach (var pg in doc.Root?.Elements("PropertyGroup").ToList() ?? new List<XElement>())
            {
                foreach (var prop in pg.Elements().ToList())
                {
                    if (!toPromote.ContainsKey(prop.Name.LocalName)) continue;
                    prop.Remove();
                    modified = true;
                }

                // Remove empty PropertyGroup elements
                if (!pg.HasElements)
                    pg.Remove();
            }

            if (modified)
                doc.Save(path);
        }

        return Task.CompletedTask;
    }
}
