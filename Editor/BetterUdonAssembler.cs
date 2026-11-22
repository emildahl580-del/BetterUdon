using System;
using System.Reflection;
using VRC.Udon.Common;
using VRC.Udon.Common.Interfaces;
using VRC.Udon.Editor;
using VRC.Udon.EditorBindings;
using VRC.Udon.UAssembly.Assembler;
using VRC.Udon.UAssembly.Interfaces;

namespace BetterUdon.Editor
{
    /// <summary>
    /// Assembles Udon assembly with an explicit heap size.
    ///
    /// <see cref="UdonEditorManager.Assemble(string)"/> always uses a small default heap, which
    /// non-trivial programs overflow. This mirrors what the UdonSharp compiler does: build an
    /// <see cref="UAssemblyAssembler"/> whose heap factory allocates the exact size we need.
    /// </summary>
    internal static class BetterUdonAssembler
    {
        private sealed class FixedSizeHeapFactory : IUdonHeapFactory
        {
            private readonly uint _heapSize;

            public FixedSizeHeapFactory(uint heapSize)
            {
                _heapSize = heapSize;
            }

            public IUdonHeap ConstructUdonHeap() => new UdonHeap(_heapSize);

            public IUdonHeap ConstructUdonHeap(uint heapSize) => new UdonHeap(_heapSize);
        }

        private static readonly FieldInfo _typeResolverGroupField =
            typeof(UdonEditorInterface).GetField("_typeResolverGroup", BindingFlags.NonPublic | BindingFlags.Instance);

        private static UdonEditorInterface _editorInterface;
        private static IUAssemblyTypeResolver _typeResolver;

        public static IUdonProgram Assemble(string assembly, uint heapSize)
        {
            IUAssemblyTypeResolver typeResolver = GetTypeResolver();

            if (typeResolver == null)
            {
                // Fall back to the editor default if the SDK's internals ever change shape.
                return UdonEditorManager.Instance.Assemble(assembly);
            }

            UAssemblyAssembler assembler = new UAssemblyAssembler(new FixedSizeHeapFactory(heapSize), typeResolver);
            return assembler.Assemble(assembly);
        }

        private static IUAssemblyTypeResolver GetTypeResolver()
        {
            if (_typeResolver != null)
                return _typeResolver;

            if (_typeResolverGroupField == null)
                return null;

            UdonEditorManager.Instance.GetNodeRegistries();

            if (_editorInterface == null)
                _editorInterface = new UdonEditorInterface();

            _typeResolver = _typeResolverGroupField.GetValue(_editorInterface) as IUAssemblyTypeResolver;
            return _typeResolver;
        }
    }
}
