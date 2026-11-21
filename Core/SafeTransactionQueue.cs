using System.Numerics;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MagicInventory.Core;

public enum QueueBatchOutcome
{
    None,
    Succeeded,
    CancelledByUser,
    AbortedOnError,
}

public enum PacingProfile
{
    StandardSlot,
    GearsetSwitch,
    MarketQuery,
    VendorOrTurnIn,
    MarketListing,
    MarketFormEntry,
    MateriaExtraction,
}

public sealed record TransactionStep(
    string Description,
    string RequiredAddon,
    Func<bool> Dispatch,
    Func<bool> HasServerAck,
    PacingProfile Pacing = PacingProfile.StandardSlot,
    string? SecondaryRequiredAddon = null,
    double TimeoutSeconds = 4.0,
    int SkipOnFailureCount = 0);

public sealed unsafe class SafeTransactionQueue : IDisposable
{
    public const int MaxBatchSize = 25;
    private const double StepTimeoutSeconds = 4.0;
    private const double ModalTransitionGraceMs = 400.0;
    private const double MinCancelDebounceMs = 450.0;
    private const float MovementToleranceSq = 0.04f;

    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly ICondition condition;
    private readonly IGameGui gameGui;
    private readonly IChatGui chatGui;
    private readonly IPluginLog log;

    private readonly Queue<TransactionStep> pendingSteps = new();
    private TransactionStep? currentStep;
    private Action? onBatchCompletedNaturally;
    private State state = State.Idle;
    private DateTime batchStartedUtc = DateTime.MinValue;
    private DateTime stepStartedUtc;
    private DateTime nextActionUtc;
    private DateTime nextAllowedDispatchUtc = DateTime.MinValue;
    private DateTime? secondaryGraceStartedUtc;
    private Vector3 originPosition;
    private int consecutiveFailures;

    private enum State
    {
        Idle,
        Dispatching,
        AwaitingAck,
        PostAckJitter,
    }

    public SafeTransactionQueue(
        IFramework framework,
        IClientState clientState,
        IObjectTable objectTable,
        ICondition condition,
        IGameGui gameGui,
        IChatGui chatGui,
        IPluginLog log)
    {
        this.framework = framework;
        this.clientState = clientState;
        this.objectTable = objectTable;
        this.condition = condition;
        this.gameGui = gameGui;
        this.chatGui = chatGui;
        this.log = log;

        this.framework.Update += OnFrameworkUpdate;
        this.chatGui.ChatMessage += OnChatMessage;
    }

    public event Action<QueueBatchOutcome, string?>? BatchEnded;

    public QueueBatchOutcome LastOutcome { get; private set; } = QueueBatchOutcome.None;

    public bool IsExecuting => state is State.Dispatching or State.AwaitingAck
        || (state == State.PostAckJitter && pendingSteps.Count > 0);

    public bool IsCoolingDown => state == State.PostAckJitter && pendingSteps.Count == 0;

    public bool IsBusy => state != State.Idle || pendingSteps.Count > 0;

    public bool IgnoreToggleClick => IsExecuting
        && (DateTime.UtcNow - batchStartedUtc).TotalMilliseconds < MinCancelDebounceMs;

    public int RemainingCount => pendingSteps.Count + (currentStep != null ? 1 : 0);

    public bool TryEnqueueBatch(
        IReadOnlyList<TransactionStep> steps,
        out string failureReason,
        Action? onCompleted = null)
    {
        failureReason = string.Empty;

        var now = DateTime.UtcNow;
        if (IsBusy || now < nextAllowedDispatchUtc)
        {
            failureReason = "Another operation is already in progress.";
            return false;
        }

        if (steps.Count == 0)
        {
            failureReason = "Nothing to process.";
            return false;
        }

        if (steps.Count > MaxBatchSize)
        {
            failureReason = $"Batch exceeds the {MaxBatchSize}-item limit.";
            return false;
        }

        var player = objectTable.LocalPlayer;
        if (!clientState.IsLoggedIn || player == null)
        {
            failureReason = "Character is not available.";
            return false;
        }

        if (!IsInteractionConditionSafe())
        {
            failureReason = "Cannot run while zoning, in combat, or in a cutscene.";
            return false;
        }

        if (!IsStepAddonReady(steps[0]))
        {
            failureReason = "Target window is not open.";
            return false;
        }

        originPosition = player.Position;
        batchStartedUtc = now;
        consecutiveFailures = 0;
        secondaryGraceStartedUtc = null;
        onBatchCompletedNaturally = onCompleted;
        pendingSteps.Clear();

        foreach (var step in steps)
        {
            pendingSteps.Enqueue(step);
        }

        currentStep = pendingSteps.Dequeue();
        LastOutcome = QueueBatchOutcome.None;
        state = State.Dispatching;
        return true;
    }

    public void Cancel(string? echoReason = null, QueueBatchOutcome outcome = QueueBatchOutcome.CancelledByUser)
    {
        var wasBusy = IsBusy;
        var wasActive = state is State.Dispatching or State.AwaitingAck || pendingSteps.Count > 0;
        var now = DateTime.UtcNow;

        pendingSteps.Clear();
        currentStep = null;
        onBatchCompletedNaturally = null;
        consecutiveFailures = 0;
        secondaryGraceStartedUtc = null;

        if (wasActive || now < nextAllowedDispatchUtc)
        {
            var postCancelFloor = now.AddMilliseconds(Random.Shared.Next(1100, 1650));
            nextAllowedDispatchUtc = postCancelFloor > nextAllowedDispatchUtc
                ? postCancelFloor
                : nextAllowedDispatchUtc;
            nextActionUtc = nextAllowedDispatchUtc;
            state = State.PostAckJitter;
        }
        else
        {
            state = State.Idle;
        }

        if (wasActive)
        {
            LastOutcome = outcome;
            if (!string.IsNullOrWhiteSpace(echoReason))
            {
                chatGui.Print($"Action cancelled: {echoReason}");
            }

            BatchEnded?.Invoke(outcome, echoReason);
        }
        else if (wasBusy && LastOutcome == QueueBatchOutcome.None)
        {
            // All steps already completed; only trailing jitter was interrupted.
            LastOutcome = QueueBatchOutcome.Succeeded;
            BatchEnded?.Invoke(QueueBatchOutcome.Succeeded, null);
        }
    }

    private void OnChatMessage(IHandleableChatMessage message)
    {
        if (state != State.AwaitingAck || currentStep == null)
        {
            return;
        }

        if (message.LogKind is not (XivChatType.ErrorMessage or XivChatType.SystemError))
        {
            return;
        }

        var text = message.Message.TextValue.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        // Fail-closed immediately on any server/client red error while awaiting a step ACK
        // so we never sit until timeout or re-attempt a rejected action.
        Cancel($"Server error: {text}", QueueBatchOutcome.AbortedOnError);
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (state == State.Idle)
        {
            return;
        }

        var now = DateTime.UtcNow;

        if (state == State.PostAckJitter && pendingSteps.Count == 0)
        {
            if (now >= nextActionUtc)
            {
                var callback = onBatchCompletedNaturally;
                onBatchCompletedNaturally = null;
                currentStep = null;
                state = State.Idle;
                if (LastOutcome == QueueBatchOutcome.None)
                {
                    LastOutcome = QueueBatchOutcome.Succeeded;
                    BatchEnded?.Invoke(QueueBatchOutcome.Succeeded, null);
                }

                callback?.Invoke();
            }

            return;
        }

        var player = objectTable.LocalPlayer;
        if (!clientState.IsLoggedIn || player == null || !IsInteractionConditionSafe())
        {
            Cancel("Interrupted.", QueueBatchOutcome.AbortedOnError);
            return;
        }

        if (Vector3.DistanceSquared(player.Position, originPosition) > MovementToleranceSq)
        {
            Cancel("Movement detected.", QueueBatchOutcome.AbortedOnError);
            return;
        }

        if (currentStep == null)
        {
            state = State.Idle;
            return;
        }

        if (IsStepAddonReady(currentStep))
        {
            secondaryGraceStartedUtc = null;
        }
        else
        {
            var allowModalTransitionGrace = !string.IsNullOrEmpty(currentStep.SecondaryRequiredAddon)
                && state is State.AwaitingAck or State.PostAckJitter;
            if (allowModalTransitionGrace)
            {
                secondaryGraceStartedUtc ??= now;
                if ((now - secondaryGraceStartedUtc.Value).TotalMilliseconds < ModalTransitionGraceMs)
                {
                    return;
                }
            }

            Cancel("Window closed.", QueueBatchOutcome.AbortedOnError);
            return;
        }

        switch (state)
        {
            case State.Dispatching:
            {
                if (now < nextAllowedDispatchUtc)
                {
                    return;
                }

                bool dispatched;
                try
                {
                    dispatched = currentStep.Dispatch();
                }
                catch (Exception ex)
                {
                    log.Error(ex, $"Dispatch failed on '{currentStep.Description}'.");
                    dispatched = false;
                }

                // If Dispatch() itself called Cancel() (e.g. cap reached or safety abort), stop cleanly.
                if (currentStep == null || state != State.Dispatching)
                {
                    return;
                }

                var dispatchFloor = now.AddMilliseconds(SampleJitterMs(dispatched ? currentStep.Pacing : PacingProfile.StandardSlot));
                if (dispatchFloor > nextAllowedDispatchUtc)
                {
                    nextAllowedDispatchUtc = dispatchFloor;
                }

                if (!dispatched)
                {
                    // Skip dependent follow-up steps (e.g. Deliver after failed Select, SetPrice+Confirm after failed Open)
                    // so a skipped prerequisite never executes orphaned steps on the wrong modal.
                    for (var skip = 0; skip < currentStep.SkipOnFailureCount && pendingSteps.Count > 0; skip++)
                    {
                        pendingSteps.Dequeue();
                    }

                    consecutiveFailures++;
                    if (consecutiveFailures >= 2)
                    {
                        Cancel($"Unable to complete '{currentStep.Description}'.", QueueBatchOutcome.AbortedOnError);
                        return;
                    }

                    AdvanceOrFinish(now, PacingProfile.StandardSlot);
                    return;
                }

                stepStartedUtc = now;
                state = State.AwaitingAck;
                break;
            }

            case State.AwaitingAck:
            {
                bool acked;
                try
                {
                    acked = currentStep.HasServerAck();
                }
                catch (Exception ex)
                {
                    log.Error(ex, $"Verification failed on '{currentStep.Description}'.");
                    Cancel("Verification error.", QueueBatchOutcome.AbortedOnError);
                    return;
                }

                // If HasServerAck() itself called Cancel() (e.g. unexpected confirmation dialog), stop cleanly.
                if (currentStep == null || state != State.AwaitingAck)
                {
                    return;
                }

                if (acked)
                {
                    consecutiveFailures = 0;
                    AdvanceOrFinish(now, currentStep.Pacing);
                    return;
                }

                var timeoutSeconds = currentStep.TimeoutSeconds > 0 ? currentStep.TimeoutSeconds : StepTimeoutSeconds;
                if ((now - stepStartedUtc).TotalSeconds > timeoutSeconds)
                {
                    Cancel($"Timed out waiting on '{currentStep.Description}'.", QueueBatchOutcome.AbortedOnError);
                }

                break;
            }

            case State.PostAckJitter:
            {
                if (now < nextActionUtc)
                {
                    return;
                }

                if (pendingSteps.Count == 0)
                {
                    currentStep = null;
                    state = State.Idle;
                    return;
                }

                currentStep = pendingSteps.Dequeue();
                state = State.Dispatching;
                break;
            }
        }
    }

    private void AdvanceOrFinish(DateTime now, PacingProfile pacing)
    {
        if (pendingSteps.Count == 0)
        {
            currentStep = null;
        }

        var jitterMs = SampleJitterMs(pacing);
        nextActionUtc = now.AddMilliseconds(jitterMs);
        state = State.PostAckJitter;
    }

    private static int SampleJitterMs(PacingProfile pacing)
    {
        var (minMs, maxMs) = pacing switch
        {
            PacingProfile.GearsetSwitch => (700, 1150),
            PacingProfile.VendorOrTurnIn => (750, 1350),
            PacingProfile.MateriaExtraction => (850, 1400),
            PacingProfile.MarketQuery => (2200, 3000),
            PacingProfile.MarketListing => (1650, 2650),
            PacingProfile.MarketFormEntry => (380, 620),
            _ => (650, 1150),
        };

        var a = Random.Shared.Next(minMs, maxMs + 1);
        var b = Random.Shared.Next(minMs, maxMs + 1);
        return (a + b) / 2;
    }

    private bool IsInteractionConditionSafe()
    {
        return !condition[ConditionFlag.BetweenAreas]
            && !condition[ConditionFlag.BetweenAreas51]
            && !condition[ConditionFlag.InCombat]
            && !condition[ConditionFlag.Casting]
            && !condition[ConditionFlag.BoundByDuty]
            && !condition[ConditionFlag.OccupiedInCutSceneEvent]
            && !condition[ConditionFlag.Unconscious];
    }

    private bool IsStepAddonReady(TransactionStep step)
    {
        if (string.IsNullOrEmpty(step.RequiredAddon) && string.IsNullOrEmpty(step.SecondaryRequiredAddon))
        {
            return true;
        }

        if (NativeUiHelpers.IsAddonOpenAndReady(gameGui, step.RequiredAddon))
        {
            return true;
        }

        return !string.IsNullOrEmpty(step.SecondaryRequiredAddon)
            && NativeUiHelpers.IsAddonOpenAndReady(gameGui, step.SecondaryRequiredAddon);
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        chatGui.ChatMessage -= OnChatMessage;
        pendingSteps.Clear();
        currentStep = null;
        state = State.Idle;
    }
}
