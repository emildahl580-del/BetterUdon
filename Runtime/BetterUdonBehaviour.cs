using UnityEngine;
using VRC.Udon;

namespace BetterUdon
{
    /// <summary>
    /// Base class for scripts compiled by the BetterUdon compiler.
    ///
    /// You write plain C# that derives from this class. The BetterUdon compiler turns the script into
    /// an Udon assembly program, which is stored in a <c>BetterUdonProgramAsset</c> and executed by a
    /// backing <see cref="UdonBehaviour"/> on the same GameObject.
    ///
    /// Unlike UdonSharp, BetterUdon supports user defined <c>struct</c> types with real value semantics.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("BetterUdon/Better Udon Behaviour")]
    public class BetterUdonBehaviour : MonoBehaviour
    {
        /// <summary>
        /// The serialized Udon program that backs this behaviour.
        /// This is created and maintained by the BetterUdon editor tooling; do not assign it yourself.
        /// </summary>
        [HideInInspector]
        public UdonBehaviour udonBehaviour;

        /// <summary>
        /// True when this component has a valid backing Udon behaviour.
        /// </summary>
        public bool IsProgramRunning => udonBehaviour != null && udonBehaviour.enabled;

        /// <summary>
        /// Sends a custom event to the Udon program. Public methods on the behaviour are exposed as
        /// Udon entry points, so any <c>public void Foo()</c> can be raised with <c>SendCustomEvent(nameof(Foo))</c>.
        /// </summary>
        public void SendCustomEvent(string eventName)
        {
            if (udonBehaviour != null)
                udonBehaviour.SendCustomEvent(eventName);
        }

        /// <summary>
        /// Sends a custom event to the Udon program after a delay in seconds.
        /// </summary>
        public void SendCustomEventDelayedSeconds(string eventName, float delaySeconds)
        {
            if (udonBehaviour != null)
                udonBehaviour.SendCustomEventDelayedSeconds(eventName, delaySeconds);
        }

        /// <summary>
        /// Sends a custom event to the Udon program after a number of frames.
        /// </summary>
        public void SendCustomEventDelayedFrames(string eventName, int frameCount)
        {
            if (udonBehaviour != null)
                udonBehaviour.SendCustomEventDelayedFrames(eventName, frameCount);
        }

        /// <summary>
        /// Reads an Udon heap variable by symbol name.
        /// </summary>
        public object GetProgramVariable(string symbolName)
        {
            return udonBehaviour != null ? udonBehaviour.GetProgramVariable(symbolName) : null;
        }

        /// <summary>
        /// Writes an Udon heap variable by symbol name.
        /// </summary>
        public void SetProgramVariable(string symbolName, object value)
        {
            if (udonBehaviour != null)
                udonBehaviour.SetProgramVariable(symbolName, value);
        }

        /// <summary>
        /// Requests ownership of the backing Udon behaviour for the local player.
        /// </summary>
        public void TakeOwnership()
        {
            if (udonBehaviour != null)
                VRC.SDKBase.Networking.SetOwner(VRC.SDKBase.Networking.LocalPlayer, udonBehaviour.gameObject);
        }

        /// <summary>
        /// Sends a networked event: the named entry point runs on the owner or on every player,
        /// depending on <paramref name="target"/>. The behaviour needs a synchronisation mode other
        /// than <c>None</c>.
        /// </summary>
        public void SendCustomNetworkEvent(VRC.Udon.Common.Interfaces.NetworkEventTarget target, string eventName)
        {
            if (udonBehaviour != null)
                udonBehaviour.SendCustomNetworkEvent(target, eventName);
        }

        /// <summary>
        /// Sends the synced variables of this behaviour to the other players. Only has an effect when
        /// the local player owns the object and the behaviour uses manual synchronisation, which is
        /// what a field marked with <see cref="BetterUdonSyncedAttribute"/> selects.
        /// </summary>
        public void RequestSerialization()
        {
            if (udonBehaviour != null)
                udonBehaviour.RequestSerialization();
        }

        /// <summary>
        /// Raised before the synced variables are written. Assign the values that should be sent here.
        /// </summary>
        public virtual void OnPreSerialization()
        {
        }

        /// <summary>
        /// Raised after this behaviour received synced variables from its owner.
        /// </summary>
        public virtual void OnDeserialization(VRC.Udon.Common.DeserializationResult result)
        {
        }

        /// <summary>
        /// Raised after this behaviour serialised its synced variables.
        /// </summary>
        public virtual void OnPostSerialization(VRC.Udon.Common.SerializationResult result)
        {
        }

        /// <summary>
        /// Enables or disables the backing Udon behaviour as well as this component.
        /// </summary>
        public void SetProgramEnabled(bool value)
        {
            if (udonBehaviour != null)
                udonBehaviour.enabled = value;

            enabled = value;
        }
    }
}
