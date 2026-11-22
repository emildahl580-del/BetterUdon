using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BetterUdon.Editor
{
    /// <summary>
    /// Calling a method on another BetterUdon behaviour, synchronously.
    ///
    /// Udon cannot execute another program's code, so the method body is inlined into the caller with
    /// <c>this</c> bound to the other behaviour's <c>UdonBehaviour</c>. Every member access inside the
    /// inlined body is then lowered to <c>GetProgramVariable</c>/<c>SetProgramVariable</c> on that
    /// behaviour, which makes arguments, return values and private fields all behave like C#: the body
    /// reads and writes the receiver's heap, and the call returns immediately.
    /// </summary>
    internal sealed partial class BehaviourCodeGenerator
    {
        private Slot _remoteReceiver;
        private ITypeSymbol _remoteType;

        private bool InRemoteBody => _remoteReceiver != null;

        /// <summary>True for a member declared on the behaviour being inlined into.</summary>
        private bool IsRemoteMemberOf(ISymbol member)
        {
            if (_remoteReceiver == null || _remoteType == null || member?.ContainingType == null)
                return false;

            for (INamedTypeSymbol current = _remoteType as INamedTypeSymbol; current != null; current = current.BaseType)
            {
                if (SymbolEqualityComparer.Default.Equals(current, member.ContainingType))
                    return true;
            }

            return false;
        }

        /// <summary>Inlines a method body with the receiver bound as <c>this</c>.</summary>
        private Slot InlineRemoteMethod(IMethodSymbol method, Slot receiver, List<Slot> arguments, SyntaxNode node)
        {
            return InlineRemoteMethod(method, receiver, arguments, node, out _);
        }

        private Slot InlineRemoteMethod(IMethodSymbol method, Slot receiver, List<Slot> arguments, SyntaxNode node,
            out List<Slot> parameterSlots)
        {
            Slot previousReceiver = _remoteReceiver;
            ITypeSymbol previousType = _remoteType;

            _remoteReceiver = receiver;
            _remoteType = method.ContainingType;

            try
            {
                return InlineMethod(method, receiver, arguments, node, out parameterSlots);
            }
            finally
            {
                _remoteReceiver = previousReceiver;
                _remoteType = previousType;
            }
        }

        /// <summary>Reads an auto property or an extern property of the receiver.</summary>
        private Slot EmitRemotePropertyRead(IPropertySymbol property, SyntaxNode node)
        {
            if (property.IsIndexer)
            {
                AddError($"Indexer '{property.Name}' cannot be used across behaviours.", node);
                return NewSlot(property.Type, "bad");
            }

            if (GetBackingField(property) != null)
                return EmitReadBehaviourMember(_remoteReceiver, property.Name, property.Type, node);

            if (AccessorHasBody(property.GetMethod))
                return MaybeCloneStruct(InlineMethod(property.GetMethod, _remoteReceiver, new List<Slot>(), node));

            return ReadExternMember(_remoteReceiver, UdonExternSignatures.PropertyGet(property), property.Type, node);
        }

        /// <summary>Lvalue for <c>this.Property</c> inside a remote body.</summary>
        private LValue CreateRemotePropertyLValue(IPropertySymbol property, SyntaxNode node)
        {
            if (property.IsIndexer)
            {
                AddError($"Indexer '{property.Name}' cannot be used across behaviours.", node);
                return new SlotLValue(this, NewSlot(property.Type, "bad"));
            }

            IFieldSymbol backing = GetBackingField(property);

            if (backing != null)
            {
                return new CallbackLValue(property.Type,
                    clone => EmitReadBehaviourMember(_remoteReceiver, property.Name, property.Type, node),
                    value => EmitWriteBehaviourMember(_remoteReceiver, property.Name, ConvertTo(value, property.Type)));
            }

            if (AccessorHasBody(property.GetMethod) || AccessorHasBody(property.SetMethod))
            {
                return new CallbackLValue(property.Type,
                    clone =>
                    {
                        if (!AccessorHasBody(property.GetMethod))
                        {
                            AddError($"'{property.Name}' has no getter.", node);
                            return NewSlot(property.Type, "bad");
                        }

                        return MaybeCloneStruct(InlineMethod(property.GetMethod, _remoteReceiver, new List<Slot>(), node));
                    },
                    value =>
                    {
                        if (!AccessorHasBody(property.SetMethod))
                        {
                            AddError($"'{property.Name}' has no setter.", node);
                            return;
                        }

                        InlineMethod(property.SetMethod, _remoteReceiver, new List<Slot> { value }, node);
                    });
            }

            return new ExternMemberLValue(this, _remoteReceiver,
                UdonExternSignatures.PropertyGet(property), UdonExternSignatures.PropertySet(property), property.Type);
        }

        /// <summary>True when the receiver of an invocation is <c>this</c> (or omitted).</summary>
        private static bool IsThisReceiver(InvocationExpressionSyntax invocation)
        {
            if (invocation.Expression is IdentifierNameSyntax)
                return true;

            if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
                return memberAccess.Expression is ThisExpressionSyntax || memberAccess.Expression is BaseExpressionSyntax;

            return false;
        }
    }
}
