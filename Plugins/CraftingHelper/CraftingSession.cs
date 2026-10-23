namespace CraftingHelper
{
    using System;

    internal enum CraftingState
    {
        Idle,
        Arming,
        Ready,
        Preview,
        WaitingForResult,
        Matched,
        Stopped,
    }

    /// <summary>A complete observation supplied by the UI reader; this class never reads the game.</summary>
    internal readonly record struct CraftingSnapshot(
        long TimeMs,
        bool ShiftHeld,
        bool Valid,
        string BindingKey,
        string Signature,
        bool Matched,
        bool Unknown,
        int CurrencyCount,
        long SampleVersion,
        string InvalidReason = "");

    /// <summary>One held-Shift session. Every stop remains latched until Shift is released.</summary>
    internal sealed class CraftingSession
    {
        private const int MaximumFrameGapMs = 500;
        private const int StableResultMs = 50;
        private readonly Func<int, int, int> nextRandom;
        private int minIntervalMs = 200;
        private int maxIntervalMs = 300;
        private int armMs = 3000;
        private int resultTimeoutMs = 1500;
        private int maxClicks = 100;
        private long? lastTickMs;
        private long armStartedAtMs;
        private long clickedAtMs;
        private long nextClickAtMs;
        private long clickSampleVersion;
        private int currencyBeforeClick;
        private string bindingKey = string.Empty;
        private string signatureBeforeClick = string.Empty;
        private string stableSignature = string.Empty;
        private long stableSampleVersion;
        private long stableStartedAtMs;
        private bool hasStableCandidate;
        private bool clickOffered;

        internal CraftingSession(Func<int, int, int>? nextRandom = null)
        {
            this.nextRandom = nextRandom ?? Random.Shared.Next;
        }

        internal CraftingState State { get; private set; } = CraftingState.Idle;
        internal string Status { get; private set; } = "Hold Shift over the same item for 3 seconds.";
        internal string StatusCode { get; private set; } = "idle";
        internal int ClickCount { get; private set; }
        internal long ArmElapsedMs { get; private set; }
        internal bool IsWaiting => this.State == CraftingState.WaitingForResult;

        internal void Configure(int minIntervalMs = 200, int maxIntervalMs = 300,
            int armMs = 3000, int resultTimeoutMs = 1500, int maxClicks = 100)
        {
            // A settings edit cannot change the meaning or budget of an existing held-key session.
            if (this.State != CraftingState.Idle) return;
            this.minIntervalMs = Math.Clamp(minIntervalMs, 50, 5000);
            this.maxIntervalMs = Math.Clamp(maxIntervalMs, this.minIntervalMs, 5000);
            this.armMs = Math.Clamp(armMs, 500, 10000);
            this.resultTimeoutMs = Math.Clamp(resultTimeoutMs, 500, 10000);
            this.maxClicks = Math.Clamp(maxClicks, 1, 10000);
        }

        /// <summary>Returns true only for a real click opportunity. Call NotifyClickIssued after sending it.</summary>
        internal bool Tick(in CraftingSnapshot snapshot, bool dryRun)
        {
            this.clickOffered = false;
            if (!snapshot.ShiftHeld)
            {
                this.Reset();
                return false;
            }

            if (this.State is CraftingState.Stopped or CraftingState.Matched) return false;

            if (this.lastTickMs.HasValue &&
                (snapshot.TimeMs < this.lastTickMs.Value || snapshot.TimeMs - this.lastTickMs.Value > MaximumFrameGapMs))
            {
                this.Stop("Frame updates were interrupted; release Shift to restart.", "frame_gap");
                return false;
            }
            this.lastTickMs = snapshot.TimeMs;

            if (!snapshot.Valid || string.IsNullOrEmpty(snapshot.BindingKey))
            {
                this.Stop(string.IsNullOrWhiteSpace(snapshot.InvalidReason)
                    ? "The item or currency source is unavailable." : snapshot.InvalidReason, "invalid");
                return false;
            }

            if (this.State != CraftingState.Idle &&
                !string.Equals(this.bindingKey, snapshot.BindingKey, StringComparison.Ordinal))
            {
                this.Stop("The hovered item or currency source changed.", "target_changed");
                return false;
            }

            // Check on every observation, including the first frame and the final allowed click.
            if (snapshot.Matched)
            {
                this.State = CraftingState.Matched;
                this.StatusCode = "matched";
                this.Status = "Target affixes found; release Shift before restarting.";
                return false;
            }

            if (snapshot.Unknown || string.IsNullOrEmpty(snapshot.Signature))
            {
                this.Stop("The item result cannot be evaluated reliably.", "unknown");
                return false;
            }

            if (this.State == CraftingState.Idle)
            {
                this.bindingKey = snapshot.BindingKey;
                this.armStartedAtMs = snapshot.TimeMs;
                this.State = CraftingState.Arming;
            }

            if (this.IsWaiting)
            {
                if (!this.ObserveResult(snapshot)) return false;
            }

            if (snapshot.CurrencyCount <= 0)
            {
                this.Stop("The selected currency source is exhausted.", "currency_empty");
                return false;
            }

            if (this.ClickCount >= this.maxClicks)
            {
                this.Stop("The session click limit was reached.", "budget");
                return false;
            }

            this.ArmElapsedMs = Math.Clamp(snapshot.TimeMs - this.armStartedAtMs, 0, this.armMs);
            if (this.ArmElapsedMs < this.armMs)
            {
                this.State = CraftingState.Arming;
                this.StatusCode = "arming";
                this.Status = "Keep Shift held over the same item.";
                return false;
            }

            if (dryRun)
            {
                this.State = CraftingState.Preview;
                this.StatusCode = "preview";
                this.Status = "Preview ready: no clicks are sent.";
                return false;
            }

            // Changing out of preview must require a new physical held-Shift session.
            if (this.State == CraftingState.Preview)
            {
                this.Stop("Preview ended; release Shift before enabling real input.", "preview_ended");
                return false;
            }

            this.State = CraftingState.Ready;
            this.StatusCode = "ready";
            this.Status = "Ready for the next click.";
            this.clickOffered = snapshot.TimeMs >= this.nextClickAtMs;
            return this.clickOffered;
        }

        internal void NotifyClickIssued(in CraftingSnapshot snapshot)
        {
            if (!this.clickOffered || this.State != CraftingState.Ready)
            {
                this.Stop("An unexpected input was reported.", "unexpected_input");
                return;
            }

            this.clickOffered = false;
            this.ClickCount++;
            this.clickedAtMs = snapshot.TimeMs;
            // Sample once for this click. Polling later frames never changes its deadline.
            this.nextClickAtMs = snapshot.TimeMs + this.nextRandom(this.minIntervalMs, this.maxIntervalMs + 1);
            this.clickSampleVersion = snapshot.SampleVersion;
            this.currencyBeforeClick = snapshot.CurrencyCount;
            this.signatureBeforeClick = snapshot.Signature;
            this.hasStableCandidate = false;
            this.State = CraftingState.WaitingForResult;
            this.StatusCode = "waiting";
            this.Status = "Waiting for changed affixes and one confirmed currency consumption.";
        }

        internal void Stop(string reason, string statusCode = "stopped")
        {
            this.clickOffered = false;
            this.State = CraftingState.Stopped;
            this.StatusCode = statusCode;
            this.Status = reason;
        }

        private bool ObserveResult(in CraftingSnapshot snapshot)
        {
            if (snapshot.TimeMs - this.clickedAtMs >= this.resultTimeoutMs)
            {
                this.Stop("Result confirmation timed out; no additional click was sent.", "result_timeout");
                return false;
            }

            if (snapshot.CurrencyCount > this.currencyBeforeClick ||
                snapshot.CurrencyCount < this.currencyBeforeClick - 1)
            {
                this.Stop("The currency source changed unexpectedly.", "currency_changed");
                return false;
            }

            if (snapshot.SampleVersion > this.clickSampleVersion &&
                snapshot.CurrencyCount == this.currencyBeforeClick - 1 &&
                !string.Equals(snapshot.Signature, this.signatureBeforeClick, StringComparison.Ordinal))
            {
                if (!this.hasStableCandidate ||
                    !string.Equals(snapshot.Signature, this.stableSignature, StringComparison.Ordinal))
                {
                    this.hasStableCandidate = true;
                    this.stableSignature = snapshot.Signature;
                    this.stableSampleVersion = snapshot.SampleVersion;
                    this.stableStartedAtMs = snapshot.TimeMs;
                }
                else if (snapshot.SampleVersion > this.stableSampleVersion &&
                    snapshot.TimeMs - this.stableStartedAtMs >= StableResultMs)
                {
                    this.hasStableCandidate = false;
                    this.State = CraftingState.Ready;
                    return true;
                }
            }
            else
            {
                this.hasStableCandidate = false;
            }

            return false;
        }

        internal void Reset()
        {
            this.State = CraftingState.Idle;
            this.StatusCode = "idle";
            this.Status = "Hold Shift over the same item for 3 seconds.";
            this.ClickCount = 0;
            this.ArmElapsedMs = 0;
            this.lastTickMs = null;
            this.nextClickAtMs = 0;
            this.hasStableCandidate = false;
            this.clickOffered = false;
            this.bindingKey = string.Empty;
        }
    }
}
