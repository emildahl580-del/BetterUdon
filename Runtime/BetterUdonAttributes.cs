using System;

namespace BetterUdon
{
    /// <summary>
    /// Marks a field so that it is synced over the network by the generated Udon program.
    /// Only fields of types that Udon is able to sync can be used.
    ///
    /// Set <see cref="OnChange"/> to a parameterless method or a property setter to run code when a
    /// value arrives from the network.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class BetterUdonSyncedAttribute : Attribute
    {
        public BetterUdonSyncMode SyncMode { get; }

        /// <summary>
        /// Method (or property) called after the field receives a value from the network. A method must
        /// take no parameters and return void; a property must have a setter body.
        /// </summary>
        public string OnChange { get; set; }

        public BetterUdonSyncedAttribute(BetterUdonSyncMode syncMode = BetterUdonSyncMode.None)
        {
            SyncMode = syncMode;
        }
    }

    public enum BetterUdonSyncMode
    {
        None,
        Linear,
        Smooth
    }

    /// <summary>
    /// Forces the synchronisation mode of the behaviour instead of letting the compiler choose it from
    /// the synced fields, mirroring UdonSharp's <c>[UdonBehaviourSyncMode]</c>.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class BetterUdonBehaviourSyncModeAttribute : Attribute
    {
        public BetterUdonBehaviourSyncMode SyncMode { get; }

        public BetterUdonBehaviourSyncModeAttribute(BetterUdonBehaviourSyncMode syncMode)
        {
            SyncMode = syncMode;
        }
    }

    public enum BetterUdonBehaviourSyncMode
    {
        /// <summary>Let the compiler pick from the synced fields (the default).</summary>
        Any,

        /// <summary>Nothing is synced and network events cannot be sent.</summary>
        None,

        /// <summary>No variables are synced, but network events still work.</summary>
        NoVariableSync,

        /// <summary>Force continuous sync. Synced arrays and structs are not allowed.</summary>
        Continuous,

        /// <summary>Force manual sync. Interpolation is not allowed.</summary>
        Manual
    }

    /// <summary>
    /// Prevents a public field from being turned into an Udon entry point / inspector field.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class BetterUdonNoExportAttribute : Attribute
    {
    }
}
