using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BetterUdon.Editor
{
    internal sealed partial class BehaviourCodeGenerator
    {
        private const string UdonReceiverType = "VRCUdonCommonInterfacesIUdonEventReceiver";

        #region Calls

        private static string GetFullName(ITypeSymbol type) => type?.ToDisplayString() ?? string.Empty;

        private static readonly string[] _udonEventApiNames =
        {
            "SendCustomEvent",
            "SendCustomEventDelayedSeconds",
            "SendCustomEventDelayedFrames",
            "GetProgramVariable",
            "SetProgramVariable",
            "RequestSerialization",
            "SendCustomNetworkEvent",
        };

        private bool IsCompilerIntrinsic(IMethodSymbol method)
        {
            if (method.ContainingType == null)
                return false;

            string containingType = GetFullName(method.ContainingType);

            if (containingType == "BetterUdon.BetterUdonBehaviour")
                return true;

            // The same API on a raw UdonBehaviour is lowered identically. This also avoids binding to
            // the generic SetProgramVariable<T>/GetProgramVariable<T> overloads, which would produce a
            // signature Udon does not expose.
            return containingType == "VRC.Udon.UdonBehaviour" && _udonEventApiNames.Contains(method.Name);
        }

        private bool IsUserDefinedMethod(IMethodSymbol method)
        {
            if (method == null)
                return false;

            return method.DeclaringSyntaxReferences.Length > 0 &&
                   !UdonTypeSystem.IsExternAssembly(method.ContainingAssembly);
        }

        private Slot _forcedReceiver;
        private bool _hasForcedReceiver;

        private Slot EmitInvocation(InvocationExpressionSyntax invocation)
        {
            IMethodSymbol method = GetSymbolInfo(invocation).Symbol as IMethodSymbol;
            if (method == null)
            {
                SymbolInfo info = GetSymbolInfo(invocation);
                method = info.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
            }

            if (method == null)
            {
                AddError($"Unable to resolve the method '{invocation.Expression}'.", invocation);
                return NewSlot(_objectType, "bad");
            }

            if (method.ReducedFrom != null)
                method = method.ReducedFrom;

            if (IsCompilerIntrinsic(method))
                return EmitIntrinsic(method, invocation);

            if (method.IsGenericMethod && TryEmitGenericUnityMethod(method, invocation, out Slot genericResult))
                return genericResult;

            if (TryEmitListMethod(method, invocation, out Slot listResult))
                return listResult;

            // Struct instances have no heap identity, so Equals is lowered to a member-wise comparison.
            if (method.Name == "Equals" &&
                method.Parameters.Length == 1 &&
                invocation.ArgumentList.Arguments.Count == 1 &&
                !method.IsStatic &&
                invocation.Expression is MemberAccessExpressionSyntax equalsAccess)
            {
                ITypeSymbol receiverType = GetType(equalsAccess.Expression);

                if (UdonTypeSystem.IsUserStruct(receiverType))
                {
                    Slot receiverValue = EmitExpression(equalsAccess.Expression);
                    Slot argumentValue = EmitExpression(invocation.ArgumentList.Arguments[0].Expression);
                    return EmitStructEquality(receiverValue, argumentValue, receiverType, false, invocation);
                }
            }

            List<Slot> arguments = new List<Slot>(invocation.ArgumentList.Arguments.Count);
            Slot instance = null;

            if (_hasForcedReceiver)
            {
                instance = _forcedReceiver;
            }
            else if (!method.IsStatic && method.ContainingType != null && GetFullName(method.ContainingType) != "BetterUdon.BetterUdonBehaviour")
            {
                if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
                    instance = EmitStructContainer(memberAccess.Expression, method.ContainingType);
                else
                    instance = MaybeCloneStruct(_thisSlot);
            }

            bool isUserMethod = IsUserDefinedMethod(method);

            // Bind arguments to parameters, honouring named arguments, ref/out and optional values.
            int parameterCount = method.Parameters.Length;
            Slot[] bound = new Slot[parameterCount];
            LValue[] refTargets = new LValue[parameterCount];

            int nextPositional = 0;
            int paramsStartArgument = -1;
            int paramsIndex = parameterCount > 0 && method.Parameters[parameterCount - 1].IsParams
                ? parameterCount - 1
                : -1;

            for (int argumentIndex = 0; argumentIndex < invocation.ArgumentList.Arguments.Count; argumentIndex++)
            {
                ArgumentSyntax argument = invocation.ArgumentList.Arguments[argumentIndex];
                int target;

                if (argument.NameColon != null)
                {
                    target = IndexOfParameter(method, argument.NameColon.Name.Identifier.ValueText);

                    if (target < 0)
                    {
                        AddError($"'{method.Name}' has no parameter named '{argument.NameColon.Name.Identifier.ValueText}'.", argument);
                        continue;
                    }
                }
                else
                {
                    target = nextPositional;
                }

                // Everything from the params parameter onwards goes into the packed array.
                if (paramsIndex >= 0 && target >= paramsIndex)
                {
                    if (paramsStartArgument < 0)
                        paramsStartArgument = argumentIndex;

                    continue;
                }

                nextPositional = target + 1;

                if (target >= parameterCount)
                    continue;

                IParameterSymbol parameter = method.Parameters[target];

                if (argument.Expression is DeclarationExpressionSyntax declaration)
                {
                    // out var x = ...
                    ILocalSymbol declaredLocal = GetDeclaredSymbol(declaration.Designation) as ILocalSymbol;
                    if (declaredLocal == null)
                    {
                        AddError("Unable to declare the out variable.", argument);
                        continue;
                    }

                    Slot local = DeclareLocal(declaredLocal, declaredLocal.Type, declaredLocal.Name);
                    bound[target] = local;
                    refTargets[target] = new SlotLValue(this, local);
                    continue;
                }

                if (parameter.RefKind == RefKind.Ref || parameter.RefKind == RefKind.Out)
                {
                    LValue target_LValue = EmitLValue(argument.Expression);

                    if (target_LValue == null)
                    {
                        AddError($"The argument for '{parameter.Name}' must be a variable because it is passed by reference.", argument);
                        bound[target] = EmitExpression(argument.Expression);
                        continue;
                    }

                    refTargets[target] = target_LValue;
                    bound[target] = target_LValue.Get(false);
                    continue;
                }

                bound[target] = EmitExpression(argument.Expression);
            }

            // Fill in optional parameters and pack params arrays.
            for (int i = 0; i < parameterCount; i++)
            {
                if (bound[i] != null)
                    continue;

                IParameterSymbol parameter = method.Parameters[i];

                if (parameter.IsParams && parameter.Type is IArrayTypeSymbol paramsArrayType)
                {
                    bound[i] = PackParams(invocation, paramsStartArgument >= 0 ? paramsStartArgument : invocation.ArgumentList.Arguments.Count, paramsArrayType);
                    continue;
                }

                if (parameter.HasExplicitDefaultValue)
                {
                    bound[i] = EmitConstant(null, parameter.ExplicitDefaultValue, parameter.Type);
                    continue;
                }

                AddError($"Missing argument for parameter '{parameter.Name}' of '{method.Name}'.", invocation);
                bound[i] = EmitDefaultValueSlot(parameter.Type);
            }

            arguments.AddRange(bound);

            if (method.ReducedFrom != null && instance != null)
                arguments.Insert(0, instance);

            if (isUserMethod && IsRecursiveMethod(method))
            {
                if (instance != null && !IsThisReceiver(invocation))
                {
                    AddError($"'{method.Name}' is recursive, so it cannot be called on another behaviour.", invocation);
                    return method.ReturnsVoid ? null : NewSlot(method.ReturnType, "bad");
                }

                return EmitRecursiveCall(method, arguments, invocation);
            }

            if (isUserMethod)
            {
                ITypeSymbol containingType = method.ContainingType;

                // A call on another BetterUdon behaviour is inlined with that behaviour bound as 'this':
                // its fields become Get/SetProgramVariable on its heap, so arguments, return values and
                // private state all work synchronously.
                // Inside an inlined remote body 'this' is the receiver, so calls to members of that type
                // are still remote calls even though the syntax says 'this'.
                bool remoteCall = DerivesFromBetterUdon(containingType) && instance != null &&
                                  (!IsThisReceiver(invocation) || (InRemoteBody && IsRemoteMemberOf(method)));

                if (!remoteCall && !IsOwnOrBaseType(containingType) && containingType.TypeKind != TypeKind.Struct)
                {
                    AddError(DescribeUnsupportedCrossBehaviourCall(method), invocation);
                    return NewSlot(method.ReturnType, "bad");
                }

                List<Slot> parameterSlots;
                Slot result = remoteCall
                    ? InlineRemoteMethod(method, instance, arguments, invocation, out parameterSlots)
                    : InlineMethod(method, instance, arguments, invocation, out parameterSlots);

                // Write ref/out parameters back into the caller's variables.
                if (refTargets.Length > 0 && parameterSlots != null)
                {
                    for (int i = 0; i < refTargets.Length && i < parameterSlots.Count; i++)
                    {
                        if (refTargets[i] == null || parameterSlots[i] == null)
                            continue;

                        refTargets[i].Set(parameterSlots[i]);
                    }
                }

                return result;
            }

            string signature = UdonExternSignatures.Method(method);
            Slot externResult = method.ReturnsVoid ? null : NewSlot(method.ReturnType, "call");

            if (instance != null)
                _asm.Push(instance);

            for (int i = 0; i < arguments.Count && i < method.Parameters.Length; i++)
            {
                // ref/out arguments are passed as the caller's heap slot so the extern writes back into it.
                bool byReference = method.Parameters[i].RefKind == RefKind.Ref || method.Parameters[i].RefKind == RefKind.Out;
                _asm.Push(byReference ? arguments[i] : ConvertTo(arguments[i], method.Parameters[i].Type));
            }

            if (externResult != null)
                _asm.Push(externResult);

            Extern(signature, $"call to '{method.Name}'");
            return externResult;
        }

        private static string DescribeUnsupportedCrossBehaviourCall(IMethodSymbol method)
        {
            if (method.IsStatic)
                return $"'{method.Name}' is static; static methods are not exposed across behaviours.";

            if (method.Parameters.Length > 0)
            {
                return $"'{method.Name}' takes arguments. Udon cannot pass arguments between behaviours: " +
                       "write the value to a public field first, then raise the event.";
            }

            if (!method.ReturnsVoid)
            {
                return $"'{method.Name}' returns a value. Udon cannot return values between behaviours: " +
                       "raise an event and read the result from a public field.";
            }

            return $"'{method.Name}' is not an Udon entry point. Only public, parameterless void methods " +
                   "can be raised across behaviours.";
        }

        private static int IndexOfParameter(IMethodSymbol method, string name)
        {
            for (int i = 0; i < method.Parameters.Length; i++)
            {
                if (method.Parameters[i].Name == name)
                    return i;
            }

            return -1;
        }

        private Slot EmitDefaultValueSlot(ITypeSymbol type)
        {
            Slot slot = NewSlot(type, "default");
            EmitDefaultValue(slot, type);
            return slot;
        }

        /// <summary>
        /// Collects the arguments from <paramref name="startArgument"/> onwards into an array.
        /// </summary>
        private Slot PackParams(InvocationExpressionSyntax invocation, int startArgument, IArrayTypeSymbol arrayType)
        {
            List<ExpressionSyntax> values = new List<ExpressionSyntax>();

            for (int i = startArgument; i < invocation.ArgumentList.Arguments.Count; i++)
                values.Add(invocation.ArgumentList.Arguments[i].Expression);

            // A single argument that is already the array type is passed through unchanged.
            if (values.Count == 1 &&
                SymbolEqualityComparer.Default.Equals(GetType(values[0]), arrayType))
            {
                return EmitExpression(values[0]);
            }

            Slot arraySlot = NewSlot(arrayType, "params");
            CreateArray(arraySlot, arrayType, IntConstant(values.Count));

            for (int i = 0; i < values.Count; i++)
            {
                ArrayElementLValue element = new ArrayElementLValue(this, arraySlot, IntConstant(i), arrayType, arrayType.ElementType);
                element.Set(EmitExpression(values[i]));
            }

            return arraySlot;
        }

        private ITypeSymbol _componentTypeSymbol => _model.Compilation.GetTypeByMetadataName("UnityEngine.Component");

        /// <summary>
        /// Udon exposes a handful of generic Unity methods with an extra <c>SystemType</c> parameter and
        /// a <c>Component</c> return value. These are the ones worth supporting.
        /// </summary>
        private bool TryEmitGenericUnityMethod(IMethodSymbol method, InvocationExpressionSyntax invocation, out Slot result)
        {
            result = null;

            if (!method.IsGenericMethod || method.TypeArguments.Length != 1)
                return false;

            ITypeSymbol typeArgument = method.TypeArguments[0];

            if (typeArgument.IsValueType)
                return false;

            string containingTypeName = UdonTypeSystem.GetUdonTypeName(method.ContainingType);

            string signature;

            switch (method.Name)
            {
                case "GetComponent":
                    signature = $"{containingTypeName}.__GetComponent__SystemType__UnityEngineComponent";
                    break;

                case "GetComponentInChildren":
                    signature = $"{containingTypeName}.__GetComponentInChildren__SystemType__UnityEngineComponent";
                    break;

                case "GetComponentInParent":
                    signature = $"{containingTypeName}.__GetComponentInParent__SystemType__UnityEngineComponent";
                    break;

                default:
                    return false;
            }

            Type systemType;
            bool isBehaviour = DerivesFromBetterUdon(typeArgument);

            if (isBehaviour)
            {
                // The C# component is removed from a built world. Looking the behaviour up by its own
                // type would return null there, so the backing UdonBehaviour is resolved instead; that
                // is the object that owns the variables.
                if (method.Name != "GetComponent")
                {
                    AddError($"'{method.Name}<{typeArgument.Name}>' is not supported for BetterUdon behaviours. " +
                             "Use GetComponent on the object itself, or keep a public UdonBehaviour reference.", invocation);
                    result = NewSlot(method.ReturnType, "bad");
                    return true;
                }

                systemType = typeof(VRC.Udon.UdonBehaviour);
            }
            else
            {
                systemType = UdonTypeSystem.ToSystemType(typeArgument);
            }

            if (systemType == null)
            {
                AddError($"Unable to resolve the component type '{typeArgument}'.", invocation);
                result = NewSlot(method.ReturnType, "bad");
                return true;
            }

            Slot instance = invocation.Expression is MemberAccessExpressionSyntax memberAccess
                ? EmitExpression(memberAccess.Expression)
                : _thisSlot;

            // A GameObject can host several behaviours, so a typed lookup scans the UdonBehaviours and
            // picks the one whose type marker matches.
            if (isBehaviour)
            {
                result = EmitFindBehaviour(instance, containingTypeName, typeArgument, invocation);
                return true;
            }

            Slot typeSlot = ConstantSlot(_systemTypeSymbol, systemType, "SystemType");
            Slot componentSlot = NewSlot(_componentTypeSymbol, "component");

            _asm.Push(instance);
            _asm.Push(typeSlot);
            _asm.Push(componentSlot);
            Extern(signature, $"'{method.Name}<{typeArgument.Name}>'");

            result = ConvertTo(componentSlot, method.ReturnType);
            return true;
        }

        /// <summary>
        /// Finds the UdonBehaviour whose hidden type marker is <paramref name="typeArgument"/>, scanning
        /// every UdonBehaviour on the object (a GameObject may host several BetterUdon behaviours).
        /// </summary>
        private Slot EmitFindBehaviour(Slot instance, string containingTypeName, ITypeSymbol typeArgument, SyntaxNode node)
        {
            ITypeSymbol componentArrayType = _model.Compilation.CreateArrayTypeSymbol(_componentTypeSymbol);
            bool anyBehaviour = typeArgument.ToDisplayString() == BetterUdonBaseTypeName;

            string lookupType = containingTypeName == "UnityEngineGameObject"
                ? "UnityEngineGameObject"
                : "UnityEngineComponent";

            Slot array = NewSlot(componentArrayType, "components");
            Slot udonType = ConstantSlot(_systemTypeSymbol, typeof(VRC.Udon.UdonBehaviour), "SystemType");

            _asm.Push(instance);
            _asm.Push(udonType);
            _asm.Push(array);
            Extern($"{lookupType}.__GetComponents__SystemType__UnityEngineComponentArray", "GetComponents(UdonBehaviour)");

            Slot result = NewSlot(_componentTypeSymbol, "component");
            Copy(ConstantSlot(_objectType, null), result);

            Slot length = NewSlot(_intType, "count");
            _asm.Push(array);
            _asm.Push(length);
            Extern("UnityEngineComponentArray.__get_Length__SystemInt32", "component count");

            Slot index = NewSlot(_intType, "index");
            Copy(IntConstant(0), index);

            string condition = NewLabel("componentCondition");
            string end = NewLabel("componentEnd");
            string next = NewLabel("componentNext");

            _asm.MarkLabel(condition);
            Slot isLess = EmitBinaryOperator(index, length, _intType, "op_LessThan", _boolType, node);
            _asm.JumpIfFalse(isLess, end);

            Slot candidate = NewSlot(_componentTypeSymbol, "candidate");
            _asm.Push(array);
            _asm.Push(index);
            _asm.Push(candidate);
            Extern("UnityEngineComponentArray.__Get__SystemInt32__UnityEngineComponent", "component read");

            if (anyBehaviour)
            {
                Copy(candidate, result);
                _asm.Jump(end);
                return result;
            }

            Slot marker = EmitGetProgramVariable(candidate, TypeMarkerSymbol, "behaviour type");
            Slot expected = ConstantSlot(_stringType, typeArgument.ToDisplayString());
            Slot matches = EmitBinaryOperator(marker, expected, _stringType, "op_Equality", _boolType, node);

            _asm.JumpIfFalse(matches, next);
            Copy(candidate, result);
            _asm.Jump(end);

            _asm.MarkLabel(next);
            Slot incremented = EmitBinaryOperator(index, IntConstant(1), _intType, "op_Addition", _intType, null);
            Copy(incremented, index);
            _asm.Jump(condition);
            _asm.MarkLabel(end);

            return result;
        }

        private Slot EmitIntrinsic(IMethodSymbol method, InvocationExpressionSyntax invocation)
        {
            List<Slot> arguments = invocation.ArgumentList.Arguments
                .Select(a => EmitExpression(a.Expression))
                .ToList();

            // These APIs are also callable on *another* behaviour, which is how BetterUdon behaviours
            // talk to each other.
            Slot receiver = _thisSlot;

            if (invocation.Expression is MemberAccessExpressionSyntax receiverAccess)
            {
                Slot target = EmitExpression(receiverAccess.Expression);

                if (target != null)
                    receiver = target;
            }

            string signature;

            switch (method.Name)
            {
                case "SendCustomEvent":
                    signature = $"{UdonReceiverType}.__SendCustomEvent__SystemString__SystemVoid";
                    _asm.Push(receiver);
                    _asm.Push(ConvertTo(arguments[0], _stringType));
                    Extern(signature, "SendCustomEvent");
                    return null;

                case "SendCustomEventDelayedSeconds":
                    _asm.Push(receiver);
                    _asm.Push(ConvertTo(arguments[0], _stringType));
                    _asm.Push(arguments[1]);
                    Extern($"{UdonReceiverType}.__SendCustomEventDelayedSeconds__SystemString_SystemSingle__SystemVoid", "SendCustomEventDelayedSeconds");
                    return null;

                case "SendCustomEventDelayedFrames":
                    _asm.Push(receiver);
                    _asm.Push(ConvertTo(arguments[0], _stringType));
                    _asm.Push(ConvertTo(arguments[1], _intType));
                    Extern($"{UdonReceiverType}.__SendCustomEventDelayedFrames__SystemString_SystemInt32__SystemVoid", "SendCustomEventDelayedFrames");
                    return null;

                case "GetProgramVariable":
                {
                    Slot result = NewSlot(_objectType, "progvar");
                    _asm.Push(receiver);
                    _asm.Push(ConvertTo(arguments[0], _stringType));
                    _asm.Push(result);
                    Extern($"{UdonReceiverType}.__GetProgramVariable__SystemString__SystemObject", "GetProgramVariable");
                    return result;
                }

                case "SetProgramVariable":
                    _asm.Push(receiver);
                    _asm.Push(ConvertTo(arguments[0], _stringType));
                    _asm.Push(ConvertTo(arguments[1], _objectType));
                    Extern($"{UdonReceiverType}.__SetProgramVariable__SystemString_SystemObject__SystemVoid", "SetProgramVariable");
                    return null;

                case "RequestSerialization":
                    _asm.Push(receiver);
                    Extern($"{UdonReceiverType}.__RequestSerialization__SystemVoid", "RequestSerialization");
                    return null;

                case "SendCustomNetworkEvent":
                {
                    // SendCustomNetworkEvent(NetworkEventTarget target, string eventName[, params object[]])
                    const string targetType = "VRCUdonCommonInterfacesNetworkEventTarget";

                    int parameterCount = arguments.Count - 2;

                    if (parameterCount > 8)
                    {
                        AddError("SendCustomNetworkEvent supports at most 8 parameters.", invocation);
                        return null;
                    }

                    System.Text.StringBuilder networkSignature = new System.Text.StringBuilder();
                    networkSignature.Append(UdonReceiverType).Append(".__SendCustomNetworkEvent__").Append(targetType).Append("_SystemString");

                    for (int i = 0; i < parameterCount; i++)
                        networkSignature.Append("_SystemObject");

                    networkSignature.Append("__SystemVoid");

                    _asm.Push(receiver);
                    _asm.Push(arguments[0]);
                    _asm.Push(ConvertTo(arguments[1], _stringType));

                    for (int i = 0; i < parameterCount; i++)
                        _asm.Push(ConvertTo(arguments[i + 2], _objectType));

                    Extern(networkSignature.ToString(), "SendCustomNetworkEvent");
                    return null;
                }

                case "TakeOwnership":
                {
                    // Networking.SetOwner(Networking.LocalPlayer, gameObject)
                    Slot player = NewSlot(_model.Compilation.GetTypeByMetadataName("VRC.SDKBase.VRCPlayerApi"), "ownerPlayer");
                    _asm.Push(player);
                    Extern("VRCSDKBaseNetworking.__get_LocalPlayer__VRCSDKBaseVRCPlayerApi", "Networking.LocalPlayer");

                    Slot target = NewSlot(_model.Compilation.GetTypeByMetadataName("UnityEngine.GameObject"), "ownerTarget");
                    _asm.Push(_thisSlot);
                    _asm.Push(target);
                    Extern("UnityEngineComponent.__get_gameObject__UnityEngineGameObject", "gameObject");

                    _asm.Push(player);
                    _asm.Push(target);
                    Extern("VRCSDKBaseNetworking.__SetOwner__VRCSDKBaseVRCPlayerApi_UnityEngineGameObject__SystemVoid", "Networking.SetOwner");
                    return null;
                }

                default:
                    AddError($"'{method.Name}' is not supported inside Udon code.", invocation);
                    return method.ReturnsVoid ? null : NewSlot(method.ReturnType, "bad");
            }
        }

        /// <summary>
        /// Expands a call to a user method inline. Udon has no call stack, so every user method call is
        /// inlined into the calling entry point.
        /// </summary>
        private Slot InlineMethod(IMethodSymbol method, Slot instance, List<Slot> arguments, SyntaxNode node, out List<Slot> parameterSlots)
        {
            parameterSlots = null;
            return InlineMethodCore(method, instance, arguments, node, out parameterSlots);
        }

        private Slot InlineMethod(IMethodSymbol method, Slot instance, List<Slot> arguments, SyntaxNode node)
        {
            return InlineMethodCore(method, instance, arguments, node, out _);
        }

        private Slot InlineMethodCore(IMethodSymbol method, Slot instance, List<Slot> arguments, SyntaxNode node, out List<Slot> parameterSlots)
        {
            parameterSlots = null;
            if (method.IsGenericMethod)
            {
                AddError($"Generic methods are not supported by the BetterUdon compiler ('{method.Name}').", node);
                return method.ReturnsVoid ? null : NewSlot(method.ReturnType, "bad");
            }

            if (method.DeclaringSyntaxReferences.Length == 0)
            {
                AddError($"'{method.Name}' has no source code available to inline.", node);
                return method.ReturnsVoid ? null : NewSlot(method.ReturnType, "bad");
            }

            if (_methodsInProgress.Contains(method))
            {
                AddError($"'{method.Name}' is recursive. Udon has no call stack, so recursion is not supported.", node);
                return method.ReturnsVoid ? null : NewSlot(method.ReturnType, "bad");
            }

            Slot result = method.ReturnsVoid ? null : NewSlot(method.ReturnType, "ret");
            string endLabel = NewLabel("callEnd");

            Slot previousThis = _thisSlot;
            bool previousThisIsStruct = _thisIsStruct;

            _methodsInProgress.Add(method);

            using (PushScope())
            {
                parameterSlots = new List<Slot>(method.Parameters.Length);

                for (int i = 0; i < method.Parameters.Length; i++)
                {
                    IParameterSymbol parameter = method.Parameters[i];
                    Slot parameterSlot = DeclareLocal(parameter, parameter.Type, parameter.Name);
                    parameterSlots.Add(parameterSlot);

                    // Parameters inside an inlined method can be assigned to, so they need a private copy.
                    if (i < arguments.Count)
                        Copy(ConvertTo(arguments[i], parameter.Type), parameterSlot);
                    else
                        EmitDefaultValue(parameterSlot, parameter.Type);
                }

                if (instance != null)
                {
                    _thisSlot = instance;
                    _thisIsStruct = UdonTypeSystem.IsUserStruct(method.ContainingType);
                }
                else if (_thisIsStruct && method.IsStatic)
                {
                    _thisIsStruct = false;
                }

                ReturnContext returnContext = new ReturnContext { ReturnSlot = result, EndLabel = endLabel };
                EmitMethodBody(method, returnContext);

                _thisSlot = previousThis;
                _thisIsStruct = previousThisIsStruct;
            }

            _methodsInProgress.Remove(method);
            _asm.MarkLabel(endLabel);

            return result;
        }

        #endregion

        #region Object and array creation

        private Slot EmitObjectCreation(ExpressionSyntax creationExpression, ArgumentListSyntax argumentList, InitializerExpressionSyntax initializer)
        {
            ITypeSymbol type = GetType(creationExpression);

            IMethodSymbol constructor = GetSymbolInfo(creationExpression).Symbol as IMethodSymbol;
            List<Slot> arguments = new List<Slot>();
            if (argumentList != null)
            {
                foreach (ArgumentSyntax argument in argumentList.Arguments)
                    arguments.Add(EmitExpression(argument.Expression));
            }

            // List<T> is lowered to a two element object[] by the compiler.
            if (TryGetListElementType(type, out ITypeSymbol listElementType))
                return CreateList(listElementType, arguments.Count > 0 ? ConvertTo(arguments[0], _intType) : IntConstant(0));

            if (UdonTypeSystem.IsUserStruct(type))
            {
                StructLayout layout = GetStructLayout(type);
                Slot slot = NewObjectArraySlot(layout.Fields.Count, type);
                foreach (IFieldSymbol field in layout.Fields)
                {
                    Slot fieldValue = NewSlot(field.Type);
                    EmitDefaultValue(fieldValue, field.Type);
                    StoreStructField(slot, layout.GetIndex(field), fieldValue);
                }

                if (constructor != null && constructor.Parameters.Length > 0)
                    InlineMethod(constructor, slot, arguments, creationExpression);

                if (initializer != null)
                    ApplyInitializer(slot, type, initializer);

                return slot;
            }

            Slot result = NewSlot(type, "new");

            if (constructor == null)
            {
                AddError($"Unable to resolve a constructor for '{type}'.", creationExpression);
                return result;
            }

            foreach (Slot argument in arguments)
                _asm.Push(argument);

            _asm.Push(result);
            Extern(UdonExternSignatures.Method(constructor), $"constructor for '{type?.Name}'");

            return result;
        }

        private void ApplyInitializer(Slot target, ITypeSymbol type, InitializerExpressionSyntax initializer)
        {
            StructLayout layout = GetStructLayout(type);
            if (layout == null)
                return;

            foreach (ExpressionSyntax expression in initializer.Expressions)
            {
                if (!(expression is AssignmentExpressionSyntax assignment))
                    continue;

                if (!(GetSymbolInfo(assignment.Left).Symbol is IFieldSymbol field))
                    continue;

                int index = layout.GetIndex(field);
                if (index < 0)
                    continue;

                Slot value = EmitExpression(assignment.Right);
                StoreStructField(target, index, ConvertTo(value, field.Type));
            }
        }

        private Slot EmitArrayCreation(InitializerExpressionSyntax initializer, ExpressionSyntax node)
        {
            ITypeSymbol arrayType = GetType(node);

            if (!(arrayType is IArrayTypeSymbol arraySymbol))
            {
                AddError("Unable to determine the array type.", node);
                return NewSlot(_objectType, "bad");
            }

            Slot arraySlot = NewSlot(arrayType, "arr");

            if (initializer != null)
            {
                int size = initializer.Expressions.Count;
                CreateArray(arraySlot, arrayType, IntConstant(size));

                for (int i = 0; i < size; i++)
                {
                    ArrayElementLValue element = new ArrayElementLValue(this, arraySlot, IntConstant(i), arrayType, arraySymbol.ElementType);
                    element.Set(EmitExpression(initializer.Expressions[i]));
                }

                return arraySlot;
            }

            SyntaxNode rankNode = node;
            ExpressionSyntax sizeExpression = null;

            if (rankNode is ArrayCreationExpressionSyntax arrayCreation)
                sizeExpression = arrayCreation.Type.RankSpecifiers.FirstOrDefault()?.Sizes.FirstOrDefault();
            else if (rankNode is ImplicitArrayCreationExpressionSyntax)
                sizeExpression = null;

            if (sizeExpression == null)
            {
                AddError("Array creation requires a size or an initializer.", node);
                return arraySlot;
            }

            Slot sizeSlot = ConvertTo(EmitExpression(sizeExpression), _intType);
            CreateArray(arraySlot, arrayType, sizeSlot);

            // Arrays of value types need their elements initialised because the struct representation is
            // a reference to an object[] and the default of a reference is null.
            if (UdonTypeSystem.IsUserStruct(arraySymbol.ElementType))
                FillDefaultStructs(arraySlot, arrayType, sizeSlot, arraySymbol.ElementType, node);

            return arraySlot;
        }

        private void CreateArray(Slot target, ITypeSymbol arrayType, Slot size)
        {
            _asm.Push(size);
            _asm.Push(target);
            Extern(UdonExternSignatures.ArrayConstructor(arrayType), "array allocation");
        }

        private void FillDefaultStructs(Slot array, ITypeSymbol arrayType, Slot size, ITypeSymbol elementType, SyntaxNode node)
        {
            Slot index = NewSlot(_intType, "fillIdx");
            Copy(IntConstant(0), index);

            string conditionLabel = NewLabel("fillCond");
            string endLabel = NewLabel("fillEnd");

            _asm.MarkLabel(conditionLabel);
            Slot isLess = EmitBinaryOperator(index, size, _intType, "op_LessThan", _boolType, node);
            _asm.JumpIfFalse(isLess, endLabel);

            Slot defaultValue = NewSlot(elementType, "elemDefault");
            EmitDefaultValue(defaultValue, elementType);

            ArrayElementLValue element = new ArrayElementLValue(this, array, index, arrayType, elementType);
            element.Set(defaultValue);

            Slot next = EmitBinaryOperator(index, IntConstant(1), _intType, "op_Addition", _intType, node);
            Copy(next, index);

            _asm.Jump(conditionLabel);
            _asm.MarkLabel(endLabel);
        }

        #endregion

        #region Field initializers

        private object EvaluateFieldInitializer(IFieldSymbol field)
        {
            object defaultValue = GetDefaultValueForField(field.Type);

            if (field.DeclaringSyntaxReferences.Length == 0)
                return defaultValue;

            if (!(field.DeclaringSyntaxReferences[0].GetSyntax() is VariableDeclaratorSyntax declarator) ||
                declarator.Initializer == null)
            {
                return defaultValue;
            }

            return EvaluateStaticExpression(declarator.Initializer.Value, field.Type);
        }

        private object GetDefaultValueForField(ITypeSymbol type)
        {
            // Heap initial values are applied after assembly, so this must be a plain CLR value.
            return GetDefaultValue(type);
        }

        /// <summary>
        /// Evaluates an expression at compile time so it can be baked into the initial heap values.
        /// </summary>
        private object EvaluateStaticExpression(ExpressionSyntax expression, ITypeSymbol targetType)
        {
            expression = Unwrap(expression);

            if (expression is DefaultExpressionSyntax)
                return GetDefaultValue(targetType);

            Optional<object> constant;
            try
            {
                constant = GetConstantValue(expression);
            }
            catch (Exception e)
            {
                AddError($"Internal compiler error evaluating '{expression}' ({e.Message}); model tree '{_model.SyntaxTree.FilePath}', node tree '{expression.SyntaxTree.FilePath}'.", expression);
                return GetDefaultValue(targetType);
            }
            if (constant.HasValue)
                return MaterializeConstant(constant.Value, targetType);

            if (expression is ObjectCreationExpressionSyntax objectCreation)
            {
                // The type of the whole creation is more reliable than the type reference node: an
                // unbound identifier yields no symbol while the expression still has its type.
                ITypeSymbol type = GetType(objectCreation) ?? GetTypeInfo(objectCreation.Type).Type;

                if (UdonTypeSystem.IsUserStruct(type))
                    return BuildStaticStruct(type, objectCreation.ArgumentList);

                // Extern value types (Color, Vector3, ...) are ordinary CLR structs, so their
                // constructor can just be invoked while compiling.
                object constructed = TryConstructExternValue(type, objectCreation.ArgumentList);

                if (constructed != null)
                    return constructed;

                AddError($"The field initializer '{expression}' cannot be evaluated at compile time. " +
                         "Assign it in Start instead.", expression);

                return GetDefaultValue(type);
            }

            if (expression is ArrayCreationExpressionSyntax arrayCreation)
            {
                if (arrayCreation.Initializer != null)
                    return BuildStaticArray(GetType(arrayCreation), arrayCreation.Initializer);

                ExpressionSyntax sizeExpression = arrayCreation.Type.RankSpecifiers.FirstOrDefault()?.Sizes.FirstOrDefault();
                Optional<object> sizeConstant = sizeExpression != null ? GetConstantValue(sizeExpression) : default;

                if (sizeConstant.HasValue && sizeConstant.Value is int size && GetType(arrayCreation) is IArrayTypeSymbol sizedArrayType)
                {
                    bool elementIsStruct = UdonTypeSystem.IsUserStruct(sizedArrayType.ElementType);
                    Type elementType = elementIsStruct ? null : UdonTypeSystem.ToSystemType(sizedArrayType.ElementType);
                    Array array = elementType != null ? Array.CreateInstance(elementType, size) : new object[size];

                    for (int i = 0; i < size; i++)
                        array.SetValue(GetDefaultValue(sizedArrayType.ElementType), i);

                    return array;
                }
            }

            if (expression is ImplicitArrayCreationExpressionSyntax implicitArray && implicitArray.Initializer != null)
                return BuildStaticArray(GetType(implicitArray), implicitArray.Initializer);

            AddError($"The field initializer '{expression}' must be a compile time constant. Assign it in _start instead.", expression);
            return GetDefaultValue(targetType);
        }

        /// <summary>
        /// Evaluates <c>new SomeStruct(args)</c> for a struct that Udon already knows about by
        /// invoking its real constructor in the editor. Returns null when the arguments are not
        /// compile time values or no constructor matches.
        /// </summary>
        private object TryConstructExternValue(ITypeSymbol type, ArgumentListSyntax arguments)
        {
            if (arguments == null)
                return null;

            Type systemType = UdonTypeSystem.ToSystemType(type);

            if (systemType == null || !systemType.IsValueType)
                return null;

            object[] values = new object[arguments.Arguments.Count];

            for (int i = 0; i < values.Length; i++)
            {
                ExpressionSyntax argument = arguments.Arguments[i].Expression;
                values[i] = EvaluateStaticExpression(argument, GetType(argument));

                if (values[i] == null)
                    return null;
            }

            foreach (System.Reflection.ConstructorInfo constructor in systemType.GetConstructors())
            {
                System.Reflection.ParameterInfo[] parameters = constructor.GetParameters();

                if (parameters.Length != values.Length)
                    continue;

                object[] converted = new object[values.Length];
                bool matches = true;

                for (int i = 0; i < values.Length; i++)
                {
                    try
                    {
                        converted[i] = Convert.ChangeType(values[i], parameters[i].ParameterType,
                            System.Globalization.CultureInfo.InvariantCulture);
                    }
                    catch
                    {
                        matches = false;
                        break;
                    }
                }

                if (!matches)
                    continue;

                try
                {
                    return constructor.Invoke(converted);
                }
                catch
                {
                    return null;
                }
            }

            return null;
        }

        private object BuildStaticStruct(ITypeSymbol type, ArgumentListSyntax arguments)
        {
            StructLayout layout = GetStructLayout(type);
            if (layout == null)
                return null;

            object[] fields = new object[layout.Fields.Count];
            for (int i = 0; i < fields.Length; i++)
                fields[i] = GetDefaultValue(layout.Fields[i].Type);

            List<object> values = new List<object>();
            if (arguments != null)
            {
                foreach (ArgumentSyntax argument in arguments.Arguments)
                    values.Add(EvaluateStaticExpression(argument.Expression, null));
            }

            for (int i = 0; i < values.Count && i < fields.Length; i++)
            {
                if (UdonTypeSystem.IsUserStruct(layout.Fields[i].Type) && values[i] is object[] nested)
                    fields[i] = CloneStaticStruct(nested, layout.Fields[i].Type);
                else
                    fields[i] = values[i];
            }

            return fields;
        }

        private object CloneStaticStruct(object[] source, ITypeSymbol type)
        {
            StructLayout layout = GetStructLayout(type);
            if (layout == null)
                return source;

            object[] result = new object[layout.Fields.Count];
            for (int i = 0; i < result.Length && i < source.Length; i++)
            {
                if (UdonTypeSystem.IsUserStruct(layout.Fields[i].Type) && source[i] is object[] nested)
                    result[i] = CloneStaticStruct(nested, layout.Fields[i].Type);
                else
                    result[i] = source[i];
            }

            return result;
        }

        private object BuildStaticArray(ITypeSymbol arrayType, InitializerExpressionSyntax initializer)
        {
            ITypeSymbol elementType = (arrayType as IArrayTypeSymbol)?.ElementType;
            bool elementIsStruct = UdonTypeSystem.IsUserStruct(elementType);
            Type systemType = elementIsStruct ? null : UdonTypeSystem.ToSystemType(elementType);

            Array array = systemType != null
                ? Array.CreateInstance(systemType, initializer.Expressions.Count)
                : new object[initializer.Expressions.Count];

            for (int i = 0; i < initializer.Expressions.Count; i++)
            {
                object value = EvaluateStaticExpression(initializer.Expressions[i], elementType);

                if (elementIsStruct && value is object[] structValue)
                    value = CloneStaticStruct(structValue, elementType);

                array.SetValue(value, i);
            }

            return array;
        }

        #endregion
    }
}
