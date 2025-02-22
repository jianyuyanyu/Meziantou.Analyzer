﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Meziantou.Analyzer.Internals;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Meziantou.Analyzer.Rules;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SequenceNumberMustBeAConstantAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = new(
        RuleIdentifiers.SequenceNumberMustBeAConstant,
        title: "Sequence number must be a constant",
        messageFormat: "Sequence number must be a constant",
        RuleCategories.Design,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "",
        helpLinkUri: RuleIdentifiers.GetHelpUri(RuleIdentifiers.SequenceNumberMustBeAConstant));

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);

        context.RegisterCompilationStartAction(context =>
        {
            var analyzerContext = new AnalyzerContext(context.Compilation);
            if (analyzerContext.IsValid)
            {
                context.RegisterOperationAction(analyzerContext.AnalyzeInvocation, OperationKind.Invocation);
            }
        });
    }

    private sealed class AnalyzerContext(Compilation compilation)
    {
        private static readonly HashSet<string> RenderTreeMethodNames = new(StringComparer.Ordinal)
        {
            "AddAttribute",
            "AddComponentReferenceCapture",
            "AddContent",
            "AddElementReferenceCapture",
            "AddMarkupContent",
            "AddMultipleAttributes",
            "OpenComponent",
            "OpenElement",
            "OpenRegion",
        };

        private static readonly HashSet<string> WebRenderTreeBuilderExtensionsSymbolMethodNames = new(StringComparer.Ordinal)
        {
            "AddEventPreventDefaultAttribute",
            "AddEventStopPropagationAttribute",
        };

        public INamedTypeSymbol? RenderTreeBuilderSymbol { get; } = compilation.GetBestTypeByMetadataName("Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder");
        public INamedTypeSymbol? WebRenderTreeBuilderExtensionsSymbol { get; } = compilation.GetBestTypeByMetadataName("Microsoft.AspNetCore.Components.Web.WebRenderTreeBuilderExtensions");

        public bool IsValid => RenderTreeBuilderSymbol is not null;

        public void AnalyzeInvocation(OperationAnalysisContext context)
        {
            var operation = (IInvocationOperation)context.Operation;
            var targetMethod = operation.TargetMethod;
            if (targetMethod.ContainingType.IsEqualTo(RenderTreeBuilderSymbol))
            {
                if (RenderTreeMethodNames.Contains(targetMethod.Name) && targetMethod.Parameters.Length >= 1 && targetMethod.Parameters[0].Type.IsInt32() && targetMethod.Parameters[0].Name == "sequence")
                {
                    if (IsValidExpression(operation.Arguments[0].Value))
                        return;

                    context.ReportDiagnostic(Rule, operation.Arguments[0].Value);
                }
            }
            else if (targetMethod.ContainingType.IsEqualTo(WebRenderTreeBuilderExtensionsSymbol))
            {
                if (WebRenderTreeBuilderExtensionsSymbolMethodNames.Contains(targetMethod.Name) && targetMethod.Parameters.Length >= 2 && targetMethod.Parameters[1].Type.IsInt32() && targetMethod.Parameters[1].Name == "sequence")
                {
                    if (IsValidExpression(operation.Arguments[1].Value))
                        return;

                    context.ReportDiagnostic(Rule, operation.Arguments[1].Value);
                }
            }

            static bool IsValidExpression(IOperation operation)
            {
                if (operation.ConstantValue.HasValue)
                    return true;

                if (operation is IParameterReferenceOperation)
                    return true;

                if (operation is IConversionOperation conversion)
                    return IsValidExpression(conversion.Operand);

                return false;
            }
        }
    }
}
