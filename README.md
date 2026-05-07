# Dolusoft C# Modernizer

A Visual Studio extension that automatically modernizes C# codebases using Roslyn. Right-click any file, project, or solution in Solution Explorer and apply all transformations in one step.

## Features

| Transformation | Scope | Description |
|---|---|---|
| **Global Usings** | Project / Solution | Consolidates common `using` directives into a single `GlobalUsings.cs` |
| **File-Scoped Namespace** | File / Project / Solution | Converts block-scoped `namespace Foo { }` to `namespace Foo;` |
| **Primary Constructor** | File / Project / Solution | Extracts constructor parameters as primary constructor, removes backing fields, renames references |
| **Directory.Build.props** | Solution | Extracts shared MSBuild properties (`TargetFramework`, `Nullable`, `ImplicitUsings`) into a solution-level `Directory.Build.props` |
| **Collection Initializers** | File / Project / Solution | Converts consecutive `.Add()` calls into collection initializer syntax (`{ }`) |
| **Collection Expressions** | File / Project / Solution | Converts collection initializers to C# 12 collection expression syntax (`[...]`) |
| **Simplify New Expression** | File / Project / Solution | Replaces `new T()` with target-typed `new()` where the type can be inferred |

## Requirements

- Visual Studio 2022 (17.0 or later)
- Projects targeting .NET 6 or later (for collection expression support, .NET 8+)

## Usage

Right-click in **Solution Explorer**:

- **File node** → *Modernize File* — applies all applicable transformations to that file only (Global Usings and Directory.Build.props are skipped at file scope)
- **Project node** → *Modernize Project* — applies all transformations across the project
- **Solution node** → *Modernize Solution* — applies all transformations across the entire solution

All changes are applied as a single undo operation (`Ctrl+Z` reverts everything at once).

## How It Works

The extension uses the **Roslyn compiler API** (`VisualStudioWorkspace`) to parse, analyze, and rewrite syntax trees. Each transformation is a self-contained `ICodeTransformer` or `IProjectTransformer` that receives the current solution and returns a modified solution. The final result is applied atomically via `TryApplyChanges`.

## Transformer Pipeline

```
GlobalUsingsTransformer
  └─ FileScopedNamespaceTransformer
       └─ PrimaryConstructorTransformer
            └─ CollectionInitializerTransformer
                 └─ CollectionExpressionTransformer
                      └─ SimplifyNewExpressionTransformer
DirectoryBuildPropsTransformer  (Solution scope only)
```

## Building from Source

1. Install the **Visual Studio SDK** workload in the Visual Studio Installer
2. Open `Dolusoft.ModernizeExtension.sln`
3. Build → the VSIX is produced in `src/Dolusoft.ModernizeExtension/bin/Debug/`

## License

MIT
