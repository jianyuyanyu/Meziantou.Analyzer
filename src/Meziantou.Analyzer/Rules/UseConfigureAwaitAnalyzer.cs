using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Meziantou.Analyzer.Configurations;
using Meziantou.Analyzer.Internals;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Meziantou.Analyzer.Rules;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UseConfigureAwaitAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = new(
        RuleIdentifiers.UseConfigureAwaitFalse,
        title: "Use Task.ConfigureAwait",
        messageFormat: "Use Task.ConfigureAwait(false) if the current SynchronizationContext is not needed",
        RuleCategories.Usage,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "",
        helpLinkUri: RuleIdentifiers.GetHelpUri(RuleIdentifiers.UseConfigureAwaitFalse));

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.ReportDiagnostics);

        context.RegisterCompilationStartAction(ctx =>
        {
            var analyzerContext = new AnalyzerContext(ctx.Compilation);
            ctx.RegisterOperationAction(analyzerContext.AnalyzeAwaitOperation, OperationKind.Await);
            ctx.RegisterOperationAction(analyzerContext.AnalyzeForEachStatement, OperationKind.Loop);
            ctx.RegisterOperationAction(analyzerContext.AnalyzeUsingOperation, OperationKind.Using);
            ctx.RegisterOperationAction(analyzerContext.AnalyzeUsingDeclarationOperation, OperationKind.UsingDeclaration);
        });
    }

    private sealed class AnalyzerContext(Compilation compilation)
    {
        private INamedTypeSymbol? ConfiguredAsyncDisposableSymbol { get; } = compilation.GetBestTypeByMetadataName("System.Runtime.CompilerServices.ConfiguredAsyncDisposable");

        private INamedTypeSymbol? IAsyncEnumerableSymbol { get; } = compilation.GetBestTypeByMetadataName("System.Collections.Generic.IAsyncEnumerable`1");
        private INamedTypeSymbol? ConfiguredCancelableAsyncEnumerableSymbol { get; } = compilation.GetBestTypeByMetadataName("System.Runtime.CompilerServices.ConfiguredCancelableAsyncEnumerable`1");
        private INamedTypeSymbol? ConfiguredTaskAwaitableSymbol { get; } = compilation.GetBestTypeByMetadataName("System.Runtime.CompilerServices.ConfiguredTaskAwaitable");
        private INamedTypeSymbol? ConfiguredTaskAwaitableOfTSymbol { get; } = compilation.GetBestTypeByMetadataName("System.Runtime.CompilerServices.ConfiguredTaskAwaitable`1");

        private INamedTypeSymbol? WPF_DispatcherObject { get; } = compilation.GetBestTypeByMetadataName("System.Windows.Threading.DispatcherObject");
        private INamedTypeSymbol? WPF_ICommand { get; } = compilation.GetBestTypeByMetadataName("System.Windows.Input.ICommand");
        private INamedTypeSymbol? WinForms_Control { get; } = compilation.GetBestTypeByMetadataName("System.Windows.Forms.Control");
        private INamedTypeSymbol? WebForms_WebControl { get; } = compilation.GetBestTypeByMetadataName("System.Web.UI.WebControls.WebControl");
        private INamedTypeSymbol? AspNetCore_ControllerBase { get; } = compilation.GetBestTypeByMetadataName("Microsoft.AspNetCore.Mvc.ControllerBase");
        private INamedTypeSymbol? AspNetCore_IRazorPage { get; } = compilation.GetBestTypeByMetadataName("Microsoft.AspNetCore.Mvc.Razor.IRazorPage");
        private INamedTypeSymbol? AspNetCore_ITagHelper { get; } = compilation.GetBestTypeByMetadataName("Microsoft.AspNetCore.Razor.TagHelpers.ITagHelper");
        private INamedTypeSymbol? AspNetCore_ITagHelperComponent { get; } = compilation.GetBestTypeByMetadataName("Microsoft.AspNetCore.Razor.TagHelpers.ITagHelperComponent");
        private INamedTypeSymbol? AspNetCore_IFilterMetadata { get; } = compilation.GetBestTypeByMetadataName("Microsoft.AspNetCore.Mvc.Filters.IFilterMetadata");
        private INamedTypeSymbol? AspNetCore_IComponent { get; } = compilation.GetBestTypeByMetadataName("Microsoft.AspNetCore.Components.IComponent");

        public void AnalyzeAwaitOperation(OperationAnalysisContext context)
        {
            // if expression is of type ConfiguredTaskAwaitable, do nothing
            // If ConfigureAwait(false) somewhere in a method, all following await calls should have ConfigureAwait(false)
            // Use ConfigureAwait(false) everywhere except if the parent class is a WPF, Winform, or ASP.NET class, or ASP.NET Core (because there is no SynchronizationContext)
            var operation = (IAwaitOperation)context.Operation;

            var awaitedOperationType = operation.Operation.Type;
            if (awaitedOperationType is null || operation.SemanticModel is null)
                return;

            if (!CanAddConfigureAwait(awaitedOperationType, operation))
                return;

            if (MustUseConfigureAwait(operation.SemanticModel, context.Options, operation.Operation.Syntax, context.CancellationToken))
            {
                context.ReportDiagnostic(Rule, operation);
            }
        }

        public void AnalyzeForEachStatement(OperationAnalysisContext context)
        {
            if (context.Operation is not IForEachLoopOperation operation)
                return;

            if (!operation.IsAsynchronous)
                return;

            // ConfiguredCancelableAsyncEnumerable
            var collectionType = operation.Collection.GetActualType();
            if (collectionType is null)
                return;

            if (collectionType.OriginalDefinition.IsEqualTo(ConfiguredCancelableAsyncEnumerableSymbol))
            {
                // Enumerable().WithCancellation(ct) or Enumerable().ConfigureAwait(false)
                if (HasConfigureAwait(operation.Collection) && HasPartOfTypeIAsyncEnumerable(operation.Collection))
                    return;

                // Check if it's a variable reference that is already configured
                // note: this doesn't check if the value is well-configured
                // https://github.com/meziantou/Meziantou.Analyzer/issues/232
                if (operation.Collection.UnwrapImplicitConversionOperations() is ILocalReferenceOperation)
                    return;
            }

            if (!CanAddConfigureAwait(collectionType, operation.Collection))
                return;

            if (MustUseConfigureAwait(operation.SemanticModel!, context.Options, operation.Syntax, context.CancellationToken))
            {
                var data = ImmutableDictionary<string, string?>.Empty.Add("kind", "foreach");
                context.ReportDiagnostic(Rule, data, operation.Collection);
            }

            static bool HasConfigureAwait(IOperation operation)
            {
                if (operation is IInvocationOperation invocation)
                {
                    if (invocation.TargetMethod.Name == "ConfigureAwait")
                        return true;
                }

                foreach (var child in operation.GetChildOperations())
                {
                    if (HasConfigureAwait(child))
                        return true;
                }

                return false;
            }

            bool HasPartOfTypeIAsyncEnumerable(IOperation operation)
            {
                if (operation.Type.IsEqualTo(IAsyncEnumerableSymbol))
                    return true;

                foreach (var child in operation.GetChildOperations())
                {
                    if (HasConfigureAwait(child))
                        return true;
                }

                return false;
            }
        }

        public void AnalyzeUsingOperation(OperationAnalysisContext context)
        {
            var operation = (IUsingOperation)context.Operation;
            if (!operation.IsAsynchronous)
                return;

            var resources = operation.Resources;
            if (resources is IVariableDeclarationGroupOperation declarationGroup)
            {
                // await using(var a = expr, b = expr)
                AnalyzeVariableDeclarationGroupOperation(context, declarationGroup);
            }
            else
            {
                // await using(expr)
                if (resources.Type is null)
                    return;

                if (!CanAddConfigureAwait(resources.Type, resources))
                    return;

                if (MustUseConfigureAwait(resources.SemanticModel!, context.Options, resources.Syntax, context.CancellationToken))
                {
                    var properties = ImmutableDictionary<string, string?>.Empty.Add("kind", "using");
                    context.ReportDiagnostic(Rule, properties, resources);
                }
            }
        }

        public void AnalyzeUsingDeclarationOperation(OperationAnalysisContext context)
        {
            var operation = (IUsingDeclarationOperation)context.Operation;
            if (!operation.IsAsynchronous)
                return;

            AnalyzeVariableDeclarationGroupOperation(context, operation.DeclarationGroup);
        }

        private void AnalyzeVariableDeclarationGroupOperation(OperationAnalysisContext context, IVariableDeclarationGroupOperation declarationGroup)
        {
            foreach (var declaration in declarationGroup.Declarations)
            {
                foreach (var declarator in declaration.Declarators)
                {
                    if (declarator.Initializer is null)
                        continue;

                    // ConfiguredCancelableAsyncEnumerable
                    var variableType = declarator.Initializer.Value.GetActualType();
                    if (variableType is null || variableType.IsEqualTo(ConfiguredAsyncDisposableSymbol))
                        return;

                    if (!CanAddConfigureAwait(variableType, declarator.Initializer.Value))
                        return;

                    if (MustUseConfigureAwait(declarator.SemanticModel!, context.Options, declarator.Syntax, context.CancellationToken))
                    {
                        context.ReportDiagnostic(Rule, declarator);
                    }
                }
            }
        }

        private bool MustUseConfigureAwait(SemanticModel semanticModel, AnalyzerOptions options, SyntaxNode node, CancellationToken cancellationToken)
        {
            var modeValue = options.GetConfigurationValue(node.SyntaxTree, RuleIdentifiers.UseConfigureAwaitFalse + ".report", "");
            if (Enum.TryParse<ReportMode>(modeValue, ignoreCase: true, out var mode))
            {
                if (mode == ReportMode.Always)
                    return true;
            }

            if (HasPreviousConfigureAwait(semanticModel, node, cancellationToken))
                return true;

            var containingClass = GetParentSymbol<INamedTypeSymbol>(semanticModel, node, cancellationToken);
            if (containingClass is not null)
            {
                if (containingClass.InheritsFrom(WPF_DispatcherObject) ||
                    containingClass.Implements(WPF_ICommand) ||
                    containingClass.InheritsFrom(WinForms_Control) || // WinForms
                    containingClass.InheritsFrom(WebForms_WebControl) || // ASP.NET (Webforms)
                    containingClass.InheritsFrom(AspNetCore_ControllerBase) || // ASP.NET Core (as there is no SynchronizationContext, ConfigureAwait(false) is useless)
                    containingClass.Implements(AspNetCore_IRazorPage) || // ASP.NET Core
                    containingClass.Implements(AspNetCore_ITagHelper) || // ASP.NET Core
                    containingClass.Implements(AspNetCore_ITagHelperComponent) || // ASP.NET Core
                    containingClass.Implements(AspNetCore_IFilterMetadata) ||
                    containingClass.Implements(AspNetCore_IComponent))  // Blazor has a synchronization context, see https://github.com/meziantou/Meziantou.Analyzer/issues/96
                {
                    return false;
                }
            }

            var containingMethod = GetParentSymbol<IMethodSymbol>(semanticModel, node, cancellationToken);
            if (containingMethod is not null && containingMethod.IsUnitTestMethod())
                return false;

            return true;
        }

        private bool HasPreviousConfigureAwait(SemanticModel semanticModel, SyntaxNode node, CancellationToken cancellationToken)
        {
            // Find all previous awaits with ConfiguredAwait(false)
            // Use context.SemanticModel.AnalyzeControlFlow to check if the current await is accessible from one of the previous await
            // https://joshvarty.com/2015/03/24/learn-roslyn-now-control-flow-analysis/
            var method = node.FirstAncestorOrSelf<MethodDeclarationSyntax>();
            if (method is not null)
            {
                var otherAwaitExpressions = method.DescendantNodes(_ => true).OfType<AwaitExpressionSyntax>();
                foreach (var expr in otherAwaitExpressions)
                {
                    if (HasPreviousConfigureAwait(expr))
                        return true;

                    bool HasPreviousConfigureAwait(AwaitExpressionSyntax otherAwaitExpression)
                    {
                        if (otherAwaitExpression == node)
                            return false;

                        if (otherAwaitExpression.GetLocation().SourceSpan.Start > node.GetLocation().SourceSpan.Start)
                            return false;

                        if (!IsConfiguredTaskAwaitable(semanticModel, otherAwaitExpression, cancellationToken))
                            return false;

                        var nodeStatement = node.FirstAncestorOrSelf<StatementSyntax>();
                        var parentStatement = otherAwaitExpression.Ancestors().OfType<StatementSyntax>().FirstOrDefault();
                        while (parentStatement is not null && nodeStatement != parentStatement)
                        {
                            if (!IsEndPointReachable(semanticModel, parentStatement))
                                return false;

                            parentStatement = parentStatement.Ancestors().OfType<StatementSyntax>().FirstOrDefault();
                        }

                        return true;
                    }
                }
            }

            return false;
        }

        private static bool IsEndPointReachable(SemanticModel semanticModel, StatementSyntax statementSyntax)
        {
            var result = semanticModel.AnalyzeControlFlow(statementSyntax);
            if (result is null || !result.Succeeded)
                return false;

            if (!result.EndPointIsReachable)
                return false;

            return true;
        }

        private static bool CanAddConfigureAwait(ITypeSymbol awaitedType, IOperation operation)
        {
            return CanAddConfigureAwait(awaitedType, operation.SemanticModel!, operation.Syntax);
        }

        private static bool CanAddConfigureAwait(ITypeSymbol awaitedType, SemanticModel semanticModel, SyntaxNode node)
        {
            var location = node.GetLocation().SourceSpan.End;
            var result = semanticModel.LookupSymbols(location, container: awaitedType, name: "ConfigureAwait", includeReducedExtensionMethods: true);
            if (result.Length > 0)
                return true;

            return false;
        }

        private bool IsConfiguredTaskAwaitable(SemanticModel semanticModel, AwaitExpressionSyntax awaitSyntax, CancellationToken cancellationToken)
        {
            var awaitExpressionType = semanticModel.GetTypeInfo(awaitSyntax.Expression, cancellationToken).ConvertedType;
            if (awaitExpressionType is null)
                return false;

            return ConfiguredTaskAwaitableSymbol.IsEqualTo(awaitExpressionType) ||
                   ConfiguredTaskAwaitableOfTSymbol.IsEqualTo(awaitExpressionType.OriginalDefinition);
        }

        private static T? GetParentSymbol<T>(SemanticModel semanticModel, SyntaxNode node, CancellationToken cancellationToken) where T : class, ISymbol
        {
            var symbol = semanticModel.GetEnclosingSymbol(node.SpanStart, cancellationToken);
            while (symbol is not null)
            {
                if (symbol is T expectedSymbol)
                    return expectedSymbol;

                symbol = symbol.ContainingSymbol;
            }

            return default;
        }

        private enum ReportMode
        {
            DetectContext,
            Always,
        }
    }
}
