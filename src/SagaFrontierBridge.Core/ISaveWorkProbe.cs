using System;

namespace SagaFrontierBridge.Core
{
    /// <summary>
    /// Captures read-only views of live SAVE_WORK / <c>SaveData</c> state.
    /// Implementations must not mutate game state, force a save, or write to disk.
    /// </summary>
    public interface ISaveWorkProbe
    {
        SaveWorkSnapshot Capture();

        PartySnapshot CaptureParty();

        FlagSnapshot CaptureFlags();

        StateSnapshot CaptureState();
    }

    /// <summary>
    /// Fallback used outside the game (tests, the MCP host) and when the live
    /// probe could not be installed at plugin startup.
    /// </summary>
    public sealed class UnavailableSaveWorkProbe : ISaveWorkProbe
    {
        private readonly string _reason;

        public UnavailableSaveWorkProbe(string reason)
        {
            _reason = string.IsNullOrWhiteSpace(reason)
                ? "The live SAVE_WORK probe is not configured."
                : reason;
        }

        public SaveWorkSnapshot Capture()
        {
            return new SaveWorkSnapshot
            {
                Available = false,
                Error = _reason,
                CapturedAtUtc = DateTimeOffset.UtcNow
            };
        }

        public PartySnapshot CaptureParty()
        {
            return new PartySnapshot
            {
                Available = false,
                Error = _reason,
                CapturedAtUtc = DateTimeOffset.UtcNow
            };
        }

        public FlagSnapshot CaptureFlags()
        {
            return new FlagSnapshot
            {
                Available = false,
                Error = _reason,
                CapturedAtUtc = DateTimeOffset.UtcNow
            };
        }

        public StateSnapshot CaptureState()
        {
            return new StateSnapshot
            {
                Available = false,
                Error = _reason,
                CapturedAtUtc = DateTimeOffset.UtcNow
            };
        }
    }
}
