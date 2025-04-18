﻿using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Meziantou.Analyzer.Internals;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Formatting;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Meziantou.Analyzer.Rules;

[ExportCodeFixProvider(LanguageNames.CSharp), Shared]
public sealed class ValidateArgumentsCorrectlyFixer : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(RuleIdentifiers.ValidateArgumentsCorrectly);

    public override FixAllProvider GetFixAllProvider()
    {
        return WellKnownFixAllProviders.BatchFixer;
    }

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var nodeToFix = root?.FindNode(context.Span, getInnermostNodeForTie: true);
        if (nodeToFix is null)
            return;

        var diagnostic = context.Diagnostics[0];

        var title = "Use local function";
        var codeAction = CodeAction.Create(
            title,
            ct => Refactor(context.Document, diagnostic, nodeToFix, ct),
            equivalenceKey: title);

        context.RegisterCodeFix(codeAction, context.Diagnostics);
    }

    private static async Task<Document> Refactor(Document document, Diagnostic diagnostic, SyntaxNode nodeToFix, CancellationToken cancellationToken)
    {
        var editor = await DocumentEditor.CreateAsync(document, cancellationToken).ConfigureAwait(false);
        var generator = editor.Generator;

        var index = int.Parse(diagnostic.Properties["Index"]!, CultureInfo.InvariantCulture);
        var symbol = (IMethodSymbol?)editor.SemanticModel.GetDeclaredSymbol(nodeToFix, cancellationToken);
        if (symbol is null)
            return document;

        var methodSyntaxNode = (MethodDeclarationSyntax)nodeToFix;
        if (methodSyntaxNode.Body is null)
            return document;

        // Create local function
        var returnTypeSyntax = generator.TypeExpression(symbol.ReturnType);
        var localFunctionSyntaxNode = LocalFunctionStatement((TypeSyntax)returnTypeSyntax, symbol.Name);
        localFunctionSyntaxNode = localFunctionSyntaxNode.WithParameterList(CopyParametersWithoutDefaultValues(methodSyntaxNode.ParameterList));
        if (methodSyntaxNode.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword)))
        {
            localFunctionSyntaxNode = localFunctionSyntaxNode.AddModifiers(Token(SyntaxKind.AsyncKeyword));
        }

        var localFunctionStatements = new List<StatementSyntax>();
        var statements = methodSyntaxNode.Body.Statements;
        var statementsToMove = statements.IndexOf(statement => statement.Span.Start > index);

        localFunctionStatements.AddRange(statements.Skip(statementsToMove));
        while (statements.Count > statementsToMove)
        {
            statements = statements.RemoveAt(statements.Count - 1);
        }

        localFunctionSyntaxNode = localFunctionSyntaxNode.WithBody(Block(localFunctionStatements));

        // Add local function to the method
        statements = statements.Add(
            (StatementSyntax)generator.ReturnStatement(
                generator.InvocationExpression(
                    generator.IdentifierName(symbol.Name),
                    methodSyntaxNode.ParameterList.Parameters.Select(p => IdentifierName(p.Identifier))))
            .WithLeadingTrivia(LineFeed));

        statements = statements.Add(localFunctionSyntaxNode);

        methodSyntaxNode = methodSyntaxNode
            .WithModifiers(methodSyntaxNode.Modifiers.Remove(SyntaxKind.AsyncKeyword))
            .WithParameterList(RemoveEnumeratorCancellationAttribute(methodSyntaxNode.ParameterList, editor.SemanticModel, cancellationToken))
            .WithBody(Block(statements))
            .WithAdditionalAnnotations(Formatter.Annotation);
        editor.ReplaceNode(nodeToFix, methodSyntaxNode);
        return editor.GetChangedDocument();
    }

    private static ParameterListSyntax CopyParametersWithoutDefaultValues(ParameterListSyntax parameterListSyntax)
    {
        return ParameterList(SeparatedList(parameterListSyntax.Parameters.Select(p => p.WithDefault(null))));
    }

    private static ParameterListSyntax RemoveEnumeratorCancellationAttribute(ParameterListSyntax parameterListSyntax, SemanticModel semanticModel, CancellationToken cancellationToken)
    {
        var enumeratorCancellationSymbol = semanticModel.Compilation.GetBestTypeByMetadataName("System.Runtime.CompilerServices.EnumeratorCancellationAttribute");
        if (enumeratorCancellationSymbol is null)
            return parameterListSyntax;

        return ParameterList(SeparatedList(parameterListSyntax.Parameters.Select(UpdateParameter)));

        ParameterSyntax UpdateParameter(ParameterSyntax parameter)
        {
            return parameter.WithAttributeLists(new SyntaxList<AttributeListSyntax>(parameter.AttributeLists.Select(UpdateAttributeList).Where(list => list.Attributes.Count > 0)));
        }

        AttributeListSyntax UpdateAttributeList(AttributeListSyntax attributes)
        {
            return AttributeList(SeparatedList(attributes.Attributes.Where(attr => !enumeratorCancellationSymbol.IsEqualTo(semanticModel.GetTypeInfo(attr, cancellationToken).Type))));
        }
    }
}
