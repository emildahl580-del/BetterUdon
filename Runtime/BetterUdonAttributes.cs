using System;
using UnityEngine;

namespace BetterUdon
{
    /// <summary>
    /// Marks a field so that it is synced over the network by the generated Udon program.
    /// Only fields of types that Udon is able to sync can be used.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class BetterUdonSyncedAttribute : Attribute
    {
        public BetterUdonSyncMode SyncMode { get; }

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
    /// Prevents a public field from being turned into an Udon entry point / inspector field.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class BetterUdonNoExportAttribute : Attribute
    {
    }
}
