# Dolusoft C# Modernizer — Plan

## What it does
Visual Studio 2022 VSIX extension. Right-click a .cs file, project, or solution node
in Solution Explorer and choose "Modernize …". Applies five C# modernization passes
in order using the live VS Roslyn workspace.

## Execution order
1. **DirectoryBuildPropsTransformer** (XML) — extracts common csproj properties to Directory.Build.props  
2. **GlobalUsingsTransformer** (Roslyn) — consolidates top-level usings into GlobalUsings.cs per project  
3. **FileScopedNamespaceTransformer** (Roslyn) — converts block-scoped namespaces to `namespace Foo;`  
4. **PrimaryConstructorTransformer** (Roslyn + SemanticModel) — converts single-ctor DI pattern to primary constructor  
5. **SimplifyNewExpressionTransformer** (Roslyn + SemanticModel) — replaces `new T()` → `new()` on explicit declarations  

## Key files
| File | Role |
|---|---|
| `ModernizePackage.cs` | AsyncPackage entry point, auto-loads on solution open |
| `Commands/ModernizeCommands.vsct` | Defines three context-menu buttons |
| `Commands/ModernizeCommand.cs` | Registers commands, resolves scope, fires engine |
| `Engine/ModernizationEngine.cs` | Orchestrates transformers, calls TryApplyChanges |
| `Transformers/*.cs` | One file per transformation |

## GUIDs
- Package: `E826BF7B-245A-4D9F-BD21-E21D23F5D411`
- CommandSet: `A13EF785-0610-4286-B993-A95AF1C28D2E`
