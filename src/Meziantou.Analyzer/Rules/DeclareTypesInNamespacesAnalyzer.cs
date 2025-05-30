﻿using System.Collections.Immutable;
using Meziantou.Analyzer.Internals;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Meziantou.Analyzer.Rules;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DeclareTypesInNamespacesAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = new(
        RuleIdentifiers.DeclareTypesInNamespaces,
        title: "Declare types in namespaces",
        messageFormat: "Declare type '{0}' in a namespace",
        RuleCategories.Design,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "",
        helpLinkUri: RuleIdentifiers.GetHelpUri(RuleIdentifiers.DeclareTypesInNamespaces));

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);

        context.RegisterSymbolAction(AnalyzeSymbol, SymbolKind.NamedType);
    }

    private static void AnalyzeSymbol(SymbolAnalysisContext context)
    {
        var symbol = (INamedTypeSymbol)context.Symbol;
        if (symbol.IsImplicitlyDeclared || symbol.IsImplicitClass || symbol.Name.Contains('$', System.StringComparison.Ordinal))
            return;

        if (symbol.IsTopLevelStatementsEntryPointType())
            return;

        if (symbol.ContainingType is null && (symbol.ContainingNamespace?.IsGlobalNamespace ?? true))
        {
            context.ReportDiagnostic(Rule, symbol, symbol.Name);
        }
    }
}
