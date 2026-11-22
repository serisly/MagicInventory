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
    int SkipOnFailureCount = 0,
    bool AllowCastingDuringAck = false);

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
    private string? activeBatchOwner;
    private State state = State.Idle;
    private DateTime batchStartedUtc = DateTime.MinValue;
    private DateTime stepStartedUtc;
    private DateTime nextActionUtc;
    private DateTime nextAllowedDispatchUtc = DateTime.MinValue;
    private DateTime? secondaryGraceStartedUtc;
    private Vector3 originPosition;
    private int consecutiveFailures;
    private bool anyStepAcked;

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

    public bool IsCoolingDown => (state == State.PostAckJitter && pendingSteps.Count == 0)
        || DateTime.UtcNow < nextAllowedDispatchUtc;

    public bool IsBusy => state != State.Idle || pendingSteps.Count > 0 || DateTime.UtcNow < nextAllowedDispatchUtc;

    public bool IgnoreToggleClick => IsExecuting
        && (DateTime.UtcNow - batchStartedUtc).TotalMilliseconds < MinCancelDebounceMs;

    public string? ActiveBatchOwner => IsBusy ? activeBatchOwner : null;

    public bool IsOwnedBy(string owner)
        => IsBusy && string.Equals(activeBatchOwner, owner, StringComparison.Ordinal);

    public static int SampleDialogConfirmDelayMs()
    {
        var a = Random.Shared.Next(380, 681);
        var b = Random.Shared.Next(380, 681);
        return (a + b) / 2;
    }

    public bool TryEnqueueBatch(
        IReadOnlyList<TransactionStep> steps,
        out string failureReason,
        Action? onCompleted = null,
        string? batchOwner = null)
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
            failureReason = $"Batch exceeds the {MaxBatchSize}-step limit.";
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
        anyStepAcked = false;
        secondaryGraceStartedUtc = null;
        onBatchCompletedNaturally = onCompleted;
        activeBatchOwner = batchOwner;
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
        var hadAck = anyStepAcked;
        var now = DateTime.UtcNow;

        pendingSteps.Clear();
        currentStep = null;
        onBatchCompletedNaturally = null;
        activeBatchOwner = null;
        consecutiveFailures = 0;
        anyStepAcked = false;
        secondaryGraceStartedUtc = null;

        if (wasActive)
        {
            var postCancelFloor = now.AddMilliseconds(Random.Shared.Next(1100, 1650));
            nextAllowedDispatchUtc = postCancelFloor > nextAllowedDispatchUtc
                ? postCancelFloor
                : nextAllowedDispatchUtc;
            nextActionUtc = nextAllowedDispatchUtc;
            state = State.PostAckJitter;
        }
        else if (now < nextAllowedDispatchUtc)
        {
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
                if (outcome == QueueBatchOutcome.Succeeded)
                {
                    chatGui.Print(echoReason);
                }
                else
                {
                    chatGui.Print($"Action cancelled: {echoReason}");
                }
            }

            BatchEnded?.Invoke(outcome, echoReason);
        }
        else if (wasBusy && LastOutcome == QueueBatchOutcome.None)
        {
            var finalOutcome = hadAck ? QueueBatchOutcome.Succeeded : QueueBatchOutcome.AbortedOnError;
            LastOutcome = finalOutcome;
            BatchEnded?.Invoke(finalOutcome, hadAck ? null : "Action did not complete.");
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
            if (now >= nextActionUtc && now >= nextAllowedDispatchUtc)
            {
                var callback = onBatchCompletedNaturally;
                var hadAck = anyStepAcked;
                onBatchCompletedNaturally = null;
                activeBatchOwner = null;
                currentStep = null;
                anyStepAcked = false;
                state = State.Idle;
                if (LastOutcome == QueueBatchOutcome.None)
                {
                    LastOutcome = hadAck ? QueueBatchOutcome.Succeeded : QueueBatchOutcome.AbortedOnError;
                    BatchEnded?.Invoke(LastOutcome, hadAck ? null : "Action did not complete.");
                }

                if (hadAck)
                {
                    callback?.Invoke();
                }
            }

            return;
        }

        var player = objectTable.LocalPlayer;
        var allowCasting = state == State.AwaitingAck && currentStep?.AllowCastingDuringAck == true;
        if (!clientState.IsLoggedIn || player == null || !IsInteractionConditionSafe(allowCasting))
        {
            Cancel("Interrupted.", QueueBatchOutcome.AbortedOnError);
            return;
        }

        if (Vector3.DistanceSquared(player.Position, originPosition) > MovementToleranceSq)
        {
            Cancel("Movement detected.", QueueBatchOutcome.AbortedOnError);
            return;
        }

        var stepToValidate = state == State.PostAckJitter && pendingSteps.Count > 0
            ? pendingSteps.Peek()
            : currentStep;
        if (stepToValidate == null)
        {
            activeBatchOwner = null;
            state = State.Idle;
            return;
        }

        if (IsStepAddonReady(stepToValidate)
            || (state == State.PostAckJitter && currentStep != null && IsStepAddonReady(currentStep)))
        {
            secondaryGraceStartedUtc = null;
        }
        else
        {
            var allowModalTransitionGrace = (!string.IsNullOrEmpty(stepToValidate.SecondaryRequiredAddon)
                    || state == State.PostAckJitter)
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

        var step = currentStep;
        if (step == null)
        {
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
                    dispatched = step.Dispatch();
                }
                catch (Exception ex)
                {
                    log.Error(ex, $"Dispatch failed on '{step.Description}'.");
                    dispatched = false;
                }

                if (currentStep == null || state != State.Dispatching)
                {
                    return;
                }

                var dispatchFloor = now.AddMilliseconds(SampleJitterMs(dispatched ? step.Pacing : PacingProfile.StandardSlot));
                if (dispatchFloor > nextAllowedDispatchUtc)
                {
                    nextAllowedDispatchUtc = dispatchFloor;
                }

                if (!dispatched)
                {
                    for (var skip = 0; skip < step.SkipOnFailureCount && pendingSteps.Count > 0; skip++)
                    {
                        pendingSteps.Dequeue();
                    }

                    consecutiveFailures++;
                    if (consecutiveFailures >= 2)
                    {
                        Cancel($"Unable to complete '{step.Description}'.", QueueBatchOutcome.AbortedOnError);
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
                    acked = step.HasServerAck();
                }
                catch (Exception ex)
                {
                    log.Error(ex, $"Verification failed on '{step.Description}'.");
                    Cancel("Verification error.", QueueBatchOutcome.AbortedOnError);
                    return;
                }

                if (currentStep == null || state != State.AwaitingAck)
                {
                    return;
                }

                if (acked)
                {
                    consecutiveFailures = 0;
                    anyStepAcked = true;
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
        var candidate = now.AddMilliseconds(jitterMs);
        nextActionUtc = candidate > nextAllowedDispatchUtc ? candidate : nextAllowedDispatchUtc;
        state = State.PostAckJitter;
    }

    private static int SampleJitterMs(PacingProfile pacing)
    {
        var (minMs, maxMs) = pacing switch
        {
            PacingProfile.VendorOrTurnIn => (750, 1350),
            PacingProfile.MateriaExtraction => (850, 1400),
            PacingProfile.MarketListing => (1650, 2650),
            PacingProfile.MarketFormEntry => (380, 620),
            _ => (650, 1150),
        };

        var a = Random.Shared.Next(minMs, maxMs + 1);
        var b = Random.Shared.Next(minMs, maxMs + 1);
        return (a + b) / 2;
    }

    private bool IsInteractionConditionSafe(bool allowCasting = false)
    {
        return !condition[ConditionFlag.BetweenAreas]
            && !condition[ConditionFlag.BetweenAreas51]
            && !condition[ConditionFlag.InCombat]
            && (allowCasting || !condition[ConditionFlag.Casting])
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
