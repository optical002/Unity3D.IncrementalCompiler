﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using GenerationAttributes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using SF = Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace IncrementalCompiler {
  public class MacroProcessorImplicits {
    readonly MacroHelper helper;
    readonly INamedTypeSymbol implicitType;
    readonly INamedTypeSymbol implicitPassThroughType;
    readonly HashSet<IMethodSymbol> passThroughMethods = new HashSet<IMethodSymbol>();
    readonly Dictionary<IMethodSymbol, ImmutableHashSet<IParameterSymbol>> implicitMethods =
      new Dictionary<IMethodSymbol, ImmutableHashSet<IParameterSymbol>>();

    public MacroProcessorImplicits(MacroHelper helper) {
      this.helper = helper;
      implicitType = helper.getTypeSymbol<Implicit>();
      implicitPassThroughType = helper.getTypeSymbol<ImplicitPassThrough>();

      foreach (var method in helper.allMethods) {
        // this line duplicates with findImplicits
        var implicitParameters = method.Parameters
          .Where(p => containsImplicit(p.GetAttributes()))
          .ToImmutableHashSet();

        if (implicitParameters.Count > 0) {
          implicitMethods.Add(method, implicitParameters);
        }
      }
    }

    bool containsImplicit(ImmutableArray<AttributeData> attributes) {
      return attributes.Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, implicitType));
    }

    ImmutableHashSet<ISymbol> ImplicitsAtPosition(SemanticModel model, bool isStatic, int position) {
      return model
        .LookupSymbols(position)
        .Where(s =>
          (s is IParameterSymbol || (s is IFieldSymbol && (!isStatic || s.IsStatic))) &&
          containsImplicit(s.GetAttributes())
        )
        .ToImmutableHashSet();
    }

    static ImplicitParameter[] ImplicitsToFill(
      ImmutableHashSet<IParameterSymbol> implicitParameters, IInvocationOperation iop
    ) {
      return iop.Arguments
        .Where(a => a.IsImplicit && implicitParameters.Contains(a.Parameter))
        .Select(a => new ImplicitParameter(a.Parameter))
        .ToArray();
    }

    // TODO: cache
    (ImplicitParameter[] toFill, ImplicitSymbolRef[] found, ISymbol maybeMethod) FindImplicits(
      ImmutableHashSet<IParameterSymbol> implicitParameters, IInvocationOperation iop, SemanticModel model,
      bool throwIfHidden, bool forceFindReferences
    ) {
      var implicitsToFill = ImplicitsToFill(implicitParameters, iop);

      var enclosingSymbol = model.GetEnclosingSymbol(iop.Syntax.SpanStart);
      var current = enclosingSymbol;
      var isStatic = current.IsStatic;
      // skips local functions and gets to the method
      while (!(current.ContainingSymbol is ITypeSymbol)) {
        current = current.ContainingSymbol;
        isStatic |= current.IsStatic;
      }

      if (implicitsToFill.Length > 0 || forceFindReferences) {

        var implicitSymbols = ImplicitsAtPosition(model, isStatic, iop.Syntax.SpanStart);

        if (throwIfHidden) {
          const string HIDDEN_IMPLICIT_HELP =
            "This happens when you have a non-implicit variable/field with the same name as " +
            "the implicit one. Make sure your implicit value has a unique name in the scope where you " +
            "want the implicit to be passed.";

          if (current is IMethodSymbol methodSymbol)
            foreach (var parameter in methodSymbol.Parameters)
              if (containsImplicit(parameter.GetAttributes()) && !implicitSymbols.Contains(parameter))
                throw new MacroProcessorError(
                  $"Hidden implicit symbol: method parameter '{parameter.Name}'. {HIDDEN_IMPLICIT_HELP}");

          {
            // search for hidden implicits in class and all base classes
            var currentTs = current.ContainingSymbol as ITypeSymbol;
            while (currentTs != null) {
              foreach (var member in currentTs.GetMembers())
                if ((!isStatic || member.IsStatic) &&
                    containsImplicit(member.GetAttributes()) &&
                    !implicitSymbols.Contains(member)
                ) {
                  throw new MacroProcessorError(
                    $"Hidden implicit symbol: '{member.ToDisplayString()}'. {HIDDEN_IMPLICIT_HELP}");
                }

              currentTs = currentTs.BaseType;
            }
          }
        }

        var implicitSymbolsWithNames = implicitSymbols.Select(s => new ImplicitSymbolRef(s)).ToArray();

        return (implicitsToFill, implicitSymbolsWithNames, current);
      }

      return (Array.Empty<ImplicitParameter>(), Array.Empty<ImplicitSymbolRef>(), current);
    }

    public void CheckMehodAttribute(IMethodSymbol method, AttributeData attribute) {
      if (SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, implicitPassThroughType)) {
        passThroughMethods.Add(method);
      }
    }

    public void AfterCheckAttributes() {
      var passthroughReferences = new ConcurrentBag<(IMethodSymbol, IMethodSymbol)>();
      var passthroughDirectImplicits = new ConcurrentBag<(IMethodSymbol, ITypeSymbol[])>();
      var passthroughFoundImplicits = new ConcurrentBag<(IMethodSymbol, ITypeSymbol[])>();

      helper.trees.AsParallel().ForAll(tree => {
        var root = tree.GetCompilationUnitRoot();
        var model = helper.compilation.GetSemanticModel(tree);
        var opFinder = new RootOperationsFinder(model);
        opFinder.Visit(root);
        foreach (var operation in opFinder.results) {
          var symbol = model.GetDeclaredSymbol(operation.Syntax);
          if (symbol is IMethodSymbol ms && passThroughMethods.Contains(ms)) {
            var descendants = operation.DescendantsAndSelf().ToArray();
            var directImplicits = new HashSet<ITypeSymbol>();

            foreach (var op in descendants.OfType<IInvocationOperation>()) {
              var method = op.TargetMethod.OriginalDefinition;
              if (passThroughMethods.Contains(method)) passthroughReferences.Add((ms, method));
              if (implicitMethods.TryGetValue(method, out var parameters)) {
                foreach (var parameterSymbol in ImplicitsToFill(parameters, op)) {
                  directImplicits.Add(parameterSymbol.type);
                }
              }
            }

            if (directImplicits.Count > 0) {
              passthroughDirectImplicits.Add((ms, directImplicits.OrderBy(_ => _.Name).ToArray()));
            }

            var methodSyntax = (BaseMethodDeclarationSyntax) operation.Syntax;

            var maybeSpanStart = methodSyntax.Body?.SpanStart ?? methodSyntax.ExpressionBody?.SpanStart;

            if (maybeSpanStart != null) {
              passthroughFoundImplicits.Add((
                ms,
                ImplicitsAtPosition(model, ms.IsStatic, maybeSpanStart.Value)
                  .Select(s => new ImplicitSymbolRef(s).type)
                  .ToArray()
              ));
            }
          }
        }
      });

      var childrenDict = passthroughReferences
        .GroupBy(_ => _.Item1)
        .ToDictionary(g => g.Key, g => g.Select(_ => _.Item2).ToArray());
      var directMissingImplicitsDict = passthroughDirectImplicits.ToDictionary(_ => _.Item1, _ => _.Item2);
      var resolved = new Dictionary<IMethodSymbol, HashSet<ITypeSymbol>>();
      var stack = new Stack<IMethodSymbol>();
      var passthroughMissingImplicits = new Dictionary<IMethodSymbol, ImplicitParameter[]>();
      var passthroughFoundImplicitsDict = passthroughFoundImplicits.ToDictionary(_ => _.Item1, _ => _.Item2);

      // [Implicit]
      var implicitAttributeList =
        SF.SingletonList(SF.AttributeList(SF.SingletonSeparatedList(SF.Attribute(SF.ParseName(implicitType.ToDisplayString())))));

      foreach (var method in passThroughMethods) {
        var current = Resolve(method);
        if (current.Count > 0) {
          var sorted = current
            .OrderBy(_ => _.Name)
            .Select(_ => new ImplicitParameter(_))
            .ToArray();
          passthroughMissingImplicits.Add(method, sorted);
          helper.builderDefinitions.Add(method, ctx => {
            var syntax = method.DeclaringSyntaxReferences[0].GetSyntax();
            helper.tryMacro(syntax, method, () => {
              var methodSyntax = (BaseMethodDeclarationSyntax) syntax;
              var newParameters = sorted
                .Select(p => SF.Parameter(
                  implicitAttributeList, // [Implicit]
                  default,
                  SF.ParseTypeName(p.type.ToDisplayString()),
                  SF.Identifier(p.name),
                  SF.EqualsValueClause(SF.LiteralExpression(SyntaxKind.DefaultLiteralExpression)) // = default
                ))
                .ToArray();
              var edited = ((BaseMethodDeclarationSyntax) ctx.Visit(methodSyntax))
                .WithParameterList(methodSyntax.ParameterList.AddParameters(newParameters));
              ctx.ChangedNodes.Add(methodSyntax, edited);
            });
          });
        }
      }

      HashSet<ITypeSymbol> Resolve(IMethodSymbol method) {
        if (resolved.TryGetValue(method, out var result)) return result;
        if (stack.Contains(method)) {
          var cycleString = string.Join(
            ", ", stack.Reverse().SkipWhile(_ => _ != method).Select(_ => _.ToDisplayString())
          );
          throw new MacroProcessorError(
            $"Cyclical reference detected while resolving {nameof(ImplicitPassThrough)}. " +
            $"Detected cycle: {cycleString}"
          );
        }
        stack.Push(method);
        try {
          result = new HashSet<ITypeSymbol>();
          if (childrenDict.TryGetValue(method, out var children)) {
            foreach (var child in children) {
              result.UnionWith(Resolve(child));
            }
          }
          if (directMissingImplicitsDict.TryGetValue(method, out var implicits)) {
            result.UnionWith(implicits);
          }
          if (passthroughFoundImplicitsDict.TryGetValue(method, out var foundImplicits)) {
            result.ExceptWith(foundImplicits);
          }
          resolved.Add(method, result);
          return result;
        }
        finally {
          stack.Pop();
        }
      }

      foreach (var method in passThroughMethods.Union(implicitMethods.Keys)) {
        helper.builderInvocations.Add(method, (ctx, op) => {
          if (op is IInvocationOperation iop)
            helper.tryMacro(op, method, () => {
              var implicitParameters =
                implicitMethods.TryGetValue(method, out var parameters)
                  ? parameters
                  : ImmutableHashSet<IParameterSymbol>.Empty;

              var passThroughToFill =
                passthroughMissingImplicits!.TryGetValue(method, out var val)
                  ? val
                  : Array.Empty<ImplicitParameter>();

              var t = FindImplicits(
                implicitParameters,
                iop,
                ctx.Model,
                throwIfHidden: true,
                forceFindReferences: passThroughToFill.Length > 0
              );

              if (t.toFill.Length > 0 || passThroughToFill.Length > 0) {
                var passThrough =
                  t.maybeMethod is IMethodSymbol ms && passthroughMissingImplicits!.TryGetValue(ms, out var val2)
                    ? val2
                    : Array.Empty<ImplicitParameter>();
                var allFound = t.found.Concat(passThrough.Select(_ => _.toRef)).ToArray();
                var resolvedImplicits = t.toFill.Concat(passThroughToFill).Select(parameter => {
                  var matchingImplicits =
                    allFound.Where(s => SymbolEqualityComparer.Default.Equals(s.type, parameter.type)).ToArray();
                  if (matchingImplicits.Length == 0)
                    throw new MacroProcessorError(
                      "No matching implicits found for " +
                      $"parameter '{parameter.name}' of type {parameter.type}"
                    );
                  if (matchingImplicits.Length > 1)
                    throw new MacroProcessorError(
                      $"{matchingImplicits.Length} matching implicits found for " +
                      $"parameter '{parameter.name}' of type {parameter.type}. " +
                      $"Candidates: {string.Join(", ", matchingImplicits.Select(_ => _.displayString))}"
                    );
                  return (parameter, fieldName: matchingImplicits[0].name);
                }).ToArray();

                var addedArguments =
                  resolvedImplicits.Select(tpl => SF.Argument(
                    SF.NameColon(SF.IdentifierName(tpl.parameter.name)),
                    default,
                    SF.IdentifierName(tpl.fieldName))
                  ).ToArray();

                var invocation = (InvocationExpressionSyntax) iop.Syntax;
                var updated = invocation.WithArgumentList(invocation.ArgumentList.AddArguments(addedArguments));
                ctx.ChangedNodes.Add(iop.Syntax, updated);
              }
            });
        });
      }
    }

    readonly struct ImplicitParameter {
      public readonly ITypeSymbol type;
      public readonly string name;

      public ImplicitParameter(IParameterSymbol s) {
        type = s.Type;
        name = s.Name;
      }

      public ImplicitParameter(ITypeSymbol type) {
        this.type = type;
        name = $"_implicit_{GetName(type)}";

        static string GetName(ITypeSymbol t) =>
          t switch {
            INamedTypeSymbol nts when nts.TypeArguments.Length > 0 =>
              $"{t.Name}_{string.Join("_", nts.TypeArguments.Select(GetName))}",
            _ => t.Name
          };
      }

      public ImplicitSymbolRef toRef => new ImplicitSymbolRef(name, type, name);
    }

    readonly struct ImplicitSymbolRef {
      public readonly string displayString;
      public readonly ITypeSymbol type;
      public readonly string name;

      public ImplicitSymbolRef(ISymbol symbol) {
        displayString = symbol switch {
          IParameterSymbol s => s.Name,
          IFieldSymbol s => s.IsStatic ? s.ToDisplayString() : $"this.{s.Name}",
          _ => throw new ArgumentOutOfRangeException(nameof(symbol))
        };
        switch (symbol) {
          case IParameterSymbol s:
            type = s.Type;
            name = s.Name;
            break;
          case IFieldSymbol s:
            type = s.Type;
            name = s.Name;
            break;
          default:
            throw new ArgumentOutOfRangeException(nameof(symbol));
        }
      }

      public ImplicitSymbolRef(string displayString, ITypeSymbol type, string name) {
        this.displayString = displayString;
        this.type = type;
        this.name = name;
      }
    }
  }
}
