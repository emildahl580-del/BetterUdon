using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using VRC.SDK3.UdonNetworkCalling;

namespace BetterUdon.Editor
{
    /// <summary>
    /// <c>[NetworkCallable]</c>: network events with parameters.
    ///
    /// The client sends the arguments, looks the entry point up by name and writes each parameter into
    /// a heap variable named after <see cref="NetworkCallingEntrypointMetadata.Parameters"/>. The
    /// compiler therefore declares an entry point whose parameter symbols are named
    /// <c>&lt;Method&gt;_&lt;parameter&gt;__param</c> and publishes matching metadata on the program asset.
    /// </summary>
    internal sealed partial class BehaviourCodeGenerator
    {
        private readonly List<NetworkCallingEntrypointMetadata> _networkMetadata =
            new List<NetworkCallingEntrypointMetadata>();

        private readonly HashSet<string> _networkCallableNames = new HashSet<string>();

        public IReadOnlyList<NetworkCallingEntrypointMetadata> NetworkMetadata => _networkMetadata;

        private static bool IsNetworkCallable(IMethodSymbol method)
        {
            return method.GetAttributes().Any(a => a.AttributeClass?.Name == "NetworkCallableAttribute");
        }

        /// <summary>Validates and registers a network callable method; returns false on error.</summary>
        private bool TryEmitNetworkCallableEntryPoint(IMethodSymbol method)
        {
            SyntaxNode location = method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();

            if (!ValidateNetworkCallable(method, location))
                return false;

            if (!_networkCallableNames.Add(method.Name))
            {
                AddError($"Duplicate network callable method name '{method.Name}'. Overloading is not supported.", location);
                return false;
            }

            AttributeData attribute = method.GetAttributes().First(a => a.AttributeClass?.Name == "NetworkCallableAttribute");
            int rate = attribute.ConstructorArguments.Length > 0 && attribute.ConstructorArguments[0].Value is int value ? value : 0;

            List<NetworkCallingParameterMetadata> parameters = new List<NetworkCallingParameterMetadata>();

            foreach (IParameterSymbol parameter in method.Parameters)
            {
                System.Type parameterType = UdonTypeSystem.ToSystemType(parameter.Type);

                if (parameterType == null || !VRC.Udon.UdonNetworkTypes.CanSync(parameterType))
                {
                    AddError($"Network callable parameter '{method.Name}({parameter.Name})' has type " +
                             $"'{parameter.Type.ToDisplayString()}', which cannot be sent over the network.", location);
                    return false;
                }

                // The name is per method so two network callables can use the same parameter name with
                // different types without sharing a heap variable.
                parameters.Add(new NetworkCallingParameterMetadata(
                    method.Name + "_" + parameter.Name + "__param", parameterType));
            }

            _networkMetadata.Add(new NetworkCallingEntrypointMetadata(
                method.Name, new NetworkCallableAttribute(rate), parameters.ToArray()));

            _asm.MarkEntryPoint(method.Name);
            _exportedEntryPoints.Add(method.Name);

            ReturnContext returnContext = new ReturnContext { EndLabel = NewLabel("networkEnd") };

            using (PushScope())
            {
                foreach (IParameterSymbol parameter in method.Parameters)
                {
                    Slot slot = GetParameterSlot(method.Name + "_" + parameter.Name + "__param", parameter.Type);
                    _scopes[_scopes.Count - 1][parameter] = slot;
                }

                EmitMethodBody(method, returnContext);
            }

            _asm.MarkLabel(returnContext.EndLabel);
            Halt();
            return true;
        }

        private bool ValidateNetworkCallable(IMethodSymbol method, SyntaxNode location)
        {
            if (method.DeclaredAccessibility != Accessibility.Public)
                return Fail($"Network callable method '{method.Name}' must be public.");

            if (method.IsStatic || method.IsGenericMethod || method.ReturnsVoid == false)
                return Fail($"Network callable method '{method.Name}' must be a non-static, non-generic void method.");

            if (method.IsVirtual || method.IsAbstract || method.IsOverride)
                return Fail($"Network callable method '{method.Name}' cannot be virtual, abstract or an override.");

            if (method.Parameters.Length > 8)
                return Fail($"Network callable method '{method.Name}' cannot have more than 8 parameters.");

            foreach (IParameterSymbol parameter in method.Parameters)
            {
                if (parameter.RefKind != RefKind.None || parameter.IsParams || parameter.HasExplicitDefaultValue)
                    return Fail($"Network callable parameter '{parameter.Name}' cannot be ref/out, params or have a default value.");
            }

            return true;

            bool Fail(string message)
            {
                AddError(message, location);
                return false;
            }
        }
    }
}
