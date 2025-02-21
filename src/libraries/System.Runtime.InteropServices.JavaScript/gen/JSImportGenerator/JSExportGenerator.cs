// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Microsoft.Interop.JavaScript
{
    [Generator]
    public sealed class JSExportGenerator : IIncrementalGenerator
    {
        internal sealed record IncrementalStubGenerationContext(
            JSSignatureContext SignatureContext,
            ContainingSyntaxContext ContainingSyntaxContext,
            ContainingSyntax StubMethodSyntaxTemplate,
            MethodSignatureDiagnosticLocations DiagnosticLocation,
            JSExportData JSExportData,
            MarshallingGeneratorFactoryKey<(TargetFramework TargetFramework, Version TargetFrameworkVersion, JSGeneratorOptions)> GeneratorFactoryKey,
            SequenceEqualImmutableArray<Diagnostic> Diagnostics);

        public static class StepNames
        {
            public const string CalculateStubInformation = nameof(CalculateStubInformation);
            public const string GenerateSingleStub = nameof(GenerateSingleStub);
        }

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            // Collect all methods adorned with JSExportAttribute
            var attributedMethods = context.SyntaxProvider
                .ForAttributeWithMetadataName(Constants.JSExportAttribute,
                   static (node, ct) => node is MethodDeclarationSyntax,
                   static (context, ct) => new { Syntax = (MethodDeclarationSyntax)context.TargetNode, Symbol = (IMethodSymbol)context.TargetSymbol });

            // Validate if attributed methods can have source generated
            var methodsWithDiagnostics = attributedMethods.Select(static (data, ct) =>
            {
                Diagnostic? diagnostic = GetDiagnosticIfInvalidMethodForGeneration(data.Syntax, data.Symbol);
                return new { Syntax = data.Syntax, Symbol = data.Symbol, Diagnostic = diagnostic };
            });

            var methodsToGenerate = methodsWithDiagnostics.Where(static data => data.Diagnostic is null);
            var invalidMethodDiagnostics = methodsWithDiagnostics.Where(static data => data.Diagnostic is not null);

            // Report diagnostics for invalid methods
            context.RegisterSourceOutput(invalidMethodDiagnostics, static (context, invalidMethod) =>
            {
                context.ReportDiagnostic(invalidMethod.Diagnostic);
            });

            // Compute generator options
            IncrementalValueProvider<JSGeneratorOptions> stubOptions = context.AnalyzerConfigOptionsProvider
                .Select(static (options, ct) => new JSGeneratorOptions(options.GlobalOptions));

            IncrementalValueProvider<StubEnvironment> stubEnvironment = context.CreateStubEnvironmentProvider();

            // Validate environment that is being used to generate stubs.
            context.RegisterDiagnostics(stubEnvironment.Combine(attributedMethods.Collect()).SelectMany((data, ct) =>
            {
                if (data.Right.IsEmpty // no attributed methods
                    || data.Left.Compilation.Options is CSharpCompilationOptions { AllowUnsafe: true }) // Unsafe code enabled
                {
                    return ImmutableArray<Diagnostic>.Empty;
                }

                return ImmutableArray.Create(Diagnostic.Create(GeneratorDiagnostics.JSExportRequiresAllowUnsafeBlocks, null));
            }));

            IncrementalValuesProvider<(MemberDeclarationSyntax, ImmutableArray<Diagnostic>)> generateSingleStub = methodsToGenerate
                .Combine(stubEnvironment)
                .Combine(stubOptions)
                .Select(static (data, ct) => new
                {
                    data.Left.Left.Syntax,
                    data.Left.Left.Symbol,
                    Environment = data.Left.Right,
                    Options = data.Right
                })
                .Select(
                    static (data, ct) => CalculateStubInformation(data.Syntax, data.Symbol, data.Environment, data.Options, ct)
                )
                .WithTrackingName(StepNames.CalculateStubInformation)
                .Select(
                    static (data, ct) => GenerateSource(data)
                )
                .WithComparer(Comparers.GeneratedSyntax)
                .WithTrackingName(StepNames.GenerateSingleStub);

            context.RegisterDiagnostics(generateSingleStub.SelectMany((stubInfo, ct) => stubInfo.Item2));

            context.RegisterConcatenatedSyntaxOutputs(generateSingleStub.Select((data, ct) => data.Item1), "JSExports.g.cs");
        }

        private static MemberDeclarationSyntax PrintGeneratedSource(
            ContainingSyntax userDeclaredMethod,
            JSSignatureContext stub,
            ContainingSyntaxContext containingSyntaxContext,
            BlockSyntax wrapperStatements, BlockSyntax registerStatements)
        {
            var WrapperName = "__Wrapper_" + userDeclaredMethod.Identifier + "_" + stub.TypesHash;
            var RegistrationName = "__Register_" + userDeclaredMethod.Identifier + "_" + stub.TypesHash;

            MemberDeclarationSyntax wrappperMethod = MethodDeclaration(PredefinedType(Token(SyntaxKind.VoidKeyword)), Identifier(WrapperName))
                .WithModifiers(TokenList(new[] { Token(SyntaxKind.InternalKeyword), Token(SyntaxKind.StaticKeyword), Token(SyntaxKind.UnsafeKeyword) }))
                .WithParameterList(ParameterList(SingletonSeparatedList(
                    Parameter(Identifier("__arguments_buffer")).WithType(PointerType(ParseTypeName(Constants.JSMarshalerArgumentGlobal))))))
                .WithBody(wrapperStatements);

            MemberDeclarationSyntax registerMethod = MethodDeclaration(PredefinedType(Token(SyntaxKind.VoidKeyword)), Identifier(RegistrationName))
                .WithAttributeLists(List(new AttributeListSyntax[]{
                    AttributeList(SingletonSeparatedList(Attribute(IdentifierName(Constants.ModuleInitializerAttributeGlobal)))),
                    AttributeList(SingletonSeparatedList(Attribute(IdentifierName(Constants.DynamicDependencyAttributeGlobal))
                    .WithArgumentList(AttributeArgumentList(SeparatedList(new[]{
                        AttributeArgument(LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(WrapperName))),
                        AttributeArgument(TypeOfExpression(ParseTypeName(stub.StubTypeFullName)))}
                    )))))}))
                .WithModifiers(TokenList(new[] { Token(SyntaxKind.InternalKeyword), Token(SyntaxKind.StaticKeyword) }))
                .WithBody(registerStatements);


            MemberDeclarationSyntax toPrint = containingSyntaxContext.WrapMembersInContainingSyntaxWithUnsafeModifier(wrappperMethod, registerMethod);

            return toPrint;
        }

        private static JSExportData? ProcessJSExportAttribute(AttributeData attrData)
        {
            // Found the JSExport, but it has an error so report the error.
            // This is most likely an issue with targeting an incorrect TFM.
            if (attrData.AttributeClass?.TypeKind is null or TypeKind.Error)
            {
                return null;
            }

            return new JSExportData();
        }

        private static IncrementalStubGenerationContext CalculateStubInformation(
            MethodDeclarationSyntax originalSyntax,
            IMethodSymbol symbol,
            StubEnvironment environment,
            JSGeneratorOptions options,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            // Get any attributes of interest on the method
            AttributeData? jsExportAttr = null;
            foreach (AttributeData attr in symbol.GetAttributes())
            {
                if (attr.AttributeClass is not null
                    && attr.AttributeClass.ToDisplayString() == Constants.JSExportAttribute)
                {
                    jsExportAttr = attr;
                }
            }

            Debug.Assert(jsExportAttr is not null);

            var generatorDiagnostics = new GeneratorDiagnostics();

            // Process the JSExport attribute
            JSExportData? jsExportData = ProcessJSExportAttribute(jsExportAttr!);

            if (jsExportData is null)
            {
                generatorDiagnostics.ReportConfigurationNotSupported(jsExportAttr!, "Invalid syntax");
                jsExportData = new JSExportData();
            }

            // Create the stub.
            var signatureContext = JSSignatureContext.Create(symbol, environment, generatorDiagnostics, ct);

            var containingTypeContext = new ContainingSyntaxContext(originalSyntax);

            var methodSyntaxTemplate = new ContainingSyntax(originalSyntax.Modifiers.StripTriviaFromTokens(), SyntaxKind.MethodDeclaration, originalSyntax.Identifier, originalSyntax.TypeParameterList);

            return new IncrementalStubGenerationContext(
                signatureContext,
                containingTypeContext,
                methodSyntaxTemplate,
                new MethodSignatureDiagnosticLocations(originalSyntax),
                jsExportData,
                CreateGeneratorFactory(environment, options),
                new SequenceEqualImmutableArray<Diagnostic>(generatorDiagnostics.Diagnostics.ToImmutableArray()));
        }

        private static MarshallingGeneratorFactoryKey<(TargetFramework, Version, JSGeneratorOptions)> CreateGeneratorFactory(StubEnvironment env, JSGeneratorOptions options)
        {
            JSGeneratorFactory jsGeneratorFactory = new JSGeneratorFactory();
            return MarshallingGeneratorFactoryKey.Create((env.TargetFramework, env.TargetFrameworkVersion, options), jsGeneratorFactory);
        }

        private static (MemberDeclarationSyntax, ImmutableArray<Diagnostic>) GenerateSource(
            IncrementalStubGenerationContext incrementalContext)
        {
            var diagnostics = new GeneratorDiagnostics();

            // Generate stub code
            var stubGenerator = new JSExportCodeGenerator(
            incrementalContext.GeneratorFactoryKey.Key.TargetFramework,
            incrementalContext.GeneratorFactoryKey.Key.TargetFrameworkVersion,
            incrementalContext.SignatureContext.SignatureContext.ElementTypeInformation,
            incrementalContext.JSExportData,
            incrementalContext.SignatureContext,
            (elementInfo, ex) =>
            {
                diagnostics.ReportMarshallingNotSupported(incrementalContext.DiagnosticLocation, elementInfo, ex.NotSupportedDetails, ex.DiagnosticProperties ?? ImmutableDictionary<string, string>.Empty);
            },
            incrementalContext.GeneratorFactoryKey.GeneratorFactory);

            BlockSyntax wrapper = stubGenerator.GenerateJSExportBody();
            BlockSyntax registration = stubGenerator.GenerateJSExportRegistration();

            return (PrintGeneratedSource(incrementalContext.StubMethodSyntaxTemplate, incrementalContext.SignatureContext, incrementalContext.ContainingSyntaxContext, wrapper, registration), incrementalContext.Diagnostics.Array.AddRange(diagnostics.Diagnostics));
        }

        private static Diagnostic? GetDiagnosticIfInvalidMethodForGeneration(MethodDeclarationSyntax methodSyntax, IMethodSymbol method)
        {
            // Verify the method has no generic types or defined implementation
            // and is marked static and partial.
            if (methodSyntax.TypeParameterList is not null
                || (methodSyntax.Body is null && methodSyntax.ExpressionBody is null)
                || !methodSyntax.Modifiers.Any(SyntaxKind.StaticKeyword)
                || methodSyntax.Modifiers.Any(SyntaxKind.PartialKeyword))
            {
                return Diagnostic.Create(GeneratorDiagnostics.InvalidExportAttributedMethodSignature, methodSyntax.Identifier.GetLocation(), method.Name);
            }

            // Verify that the types the method is declared in are marked partial.
            for (SyntaxNode? parentNode = methodSyntax.Parent; parentNode is TypeDeclarationSyntax typeDecl; parentNode = parentNode.Parent)
            {
                if (!typeDecl.Modifiers.Any(SyntaxKind.PartialKeyword))
                {
                    return Diagnostic.Create(GeneratorDiagnostics.InvalidExportAttributedMethodContainingTypeMissingModifiers, methodSyntax.Identifier.GetLocation(), method.Name, typeDecl.Identifier);
                }
            }

            // Verify the method does not have a ref return
            if (method.ReturnsByRef || method.ReturnsByRefReadonly)
            {
                return Diagnostic.Create(GeneratorDiagnostics.ReturnConfigurationNotSupported, methodSyntax.Identifier.GetLocation(), "ref return", method.ToDisplayString());
            }

            return null;
        }
    }
}
