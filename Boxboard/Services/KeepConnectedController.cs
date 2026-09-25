using Bevdox.Models;
using Boxboard.Models;

namespace Boxboard.Services;

public sealed class KeepConnectedController(
    ISessionWindows windows, SessionCoordinator sessions, TimeProvider? clock = null) : IDisposable
{
    private sealed class SlotState
    {
        public required string MachineId { get; init; }
        public DateTimeOffset? MissingSince { get; set; }
        public DateTimeOffset NextAttempt { get; set; }
        public int Attempts { get; set; }
        public CancellationTokenSource? Request { get; set; }
    }

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Dictionary<Guid, SlotState> _states = [];
    private readonly HashSet<WindowIdentity> _failedPromptCloses = [];

    public void Reset()
    {
        foreach (var state in _states.Values)
            state.Request?.Cancel();
        _states.Clear();
        _failedPromptCloses.Clear();
    }

    public async Task TickAsync(IReadOnlyList<(SlotAssignment Slot, DevBoxInstance Machine)> assignments,
        IReadOnlyList<DevBoxInstance> catalog, bool enabled, CancellationToken ct = default)
    {
        if (!enabled)
        {
            Reset();
            return;
        }
        var environment = windows.GetEnvironment();
        if (!environment.CanInteract)
            return;
        sessions.Observe();
        var assignedSlots = assignments.Select(item => item.Slot.Id).ToHashSet();
        foreach (var removed in _states.Keys.Where(id => !assignedSlots.Contains(id)).ToList())
        {
            _states[removed].Request?.Cancel();
            _states.Remove(removed);
        }
        foreach (var (slot, machine) in assignments)
        {
            ct.ThrowIfCancellationRequested();
            if (!BoardSettings.SameId(slot.MachineId, machine.UniqueId))
                throw new InvalidOperationException($"{slot.Name} no longer identifies {machine.EffectiveName}.");
            if (_states.TryGetValue(slot.Id, out var previous) &&
                !BoardSettings.SameId(previous.MachineId, machine.UniqueId))
            {
                previous.Request?.Cancel();
                _states.Remove(slot.Id);
            }
            var session = sessions.For(slot);
            if (catalog.Count(item => string.Equals(item.OriginalName, machine.OriginalName,
                StringComparison.OrdinalIgnoreCase)) != 1)
            {
                session.Status = "Keep connected paused: this Dev Box name is ambiguous in the catalog.";
                continue;
            }
            var matches = sessions.Candidates(machine);
            if (matches.Count > 0)
            {
                if (_states.TryGetValue(slot.Id, out var observed))
                    observed.MissingSince = null;
                if (matches.Count > 1)
                    session.Status = "Keep connected paused: multiple matching client windows. Resolve them manually.";
                else if (matches[0].DesktopId != environment.DesktopId)
                    session.Status = "Client is on another desktop. Use Re-apply to move it; no duplicate was launched.";
                else if (sessions.ReconnectPromptCandidates.Any(candidate =>
                    candidate.Identity == matches[0].Identity && candidate.VisibleWindowCount == 2))
                {
                    if (session.Connecting)
                    {
                        if (session.Deadline is null)
                            continue;
                        sessions.CancelPending(slot, "The requested client opened a reconnect prompt; replacing it.");
                    }
                    var client = matches[0];
                    if (_failedPromptCloses.Contains(client.Identity))
                    {
                        session.Status = "The reconnect windows could not be closed automatically. Verify in Windows App.";
                        continue;
                    }
                    if (!_states.TryGetValue(slot.Id, out var reconnectState))
                        _states.Add(slot.Id, reconnectState = new SlotState { MachineId = machine.UniqueId });
                    var promptTime = _clock.GetUtcNow();
                    if (reconnectState.Attempts >= 3)
                    {
                        session.Status = "Keep connected paused after three requests. Use Re-apply to retry.";
                        continue;
                    }
                    if (promptTime < reconnectState.NextAttempt)
                        continue;
                    session.Status = $"Closing the disconnected {machine.EffectiveName} client before requesting a replacement.";
                    try
                    {
                        await windows.CloseReconnectPromptAsync(client, ct);
                        sessions.Observe();
                        if (sessions.Candidates(machine).Any(candidate => candidate.Identity == client.Identity))
                            throw new InvalidOperationException("The old client is still visible; no replacement was requested.");
                        if (sessions.Candidates(machine).Count > 0)
                        {
                            session.Status = "A matching client appeared while closing the old window; no duplicate was requested.";
                            continue;
                        }
                        reconnectState.MissingSince = _clock.GetUtcNow().AddSeconds(-3);
                        session.ReconnectCloseFailed = false;
                        session.Status = "Disconnected client closed; Keep connected will request a replacement.";
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _failedPromptCloses.Add(client.Identity);
                        session.ReconnectCloseFailed = true;
                        session.Status = "The reconnect windows could not be closed automatically. Verify in Windows App.";
                        throw;
                    }
                }
                else if (!session.Connecting && session.BoundWindow != matches[0].Identity &&
                    !sessions.ReconnectPromptCandidates.Any(candidate => candidate.Identity == matches[0].Identity))
                    sessions.Bind(slot, machine, matches[0].Identity, preservePosition: false);
                continue;
            }
            var now = _clock.GetUtcNow();
            if (!_states.TryGetValue(slot.Id, out var state))
                _states.Add(slot.Id, state = new SlotState { MachineId = machine.UniqueId });
            state.MissingSince ??= now;
            if (session.Connecting)
                continue;
            if (state.Attempts >= 3)
            {
                session.Status = "Keep connected paused after three attempts without a client window. " +
                    "Use Re-apply or toggle Keep on to retry.";
                continue;
            }
            if (now < state.MissingSince.Value.AddSeconds(3) || now < state.NextAttempt)
                continue;
            state.Attempts++;
            state.NextAttempt = now.AddSeconds(state.Attempts switch
            {
                1 => 5,
                2 => 30,
                _ => 120
            });
            session.Status = $"Keep connected: requesting {machine.EffectiveName} " +
                $"(attempt {state.Attempts}/3). Normal Windows App sign-in may be required.";
            var request = CancellationTokenSource.CreateLinkedTokenSource(ct);
            state.Request = request;
            try
            {
                await sessions.ConnectAsync(slot, machine, nameIsUnique: true, request.Token);
            }
            catch (OperationCanceledException) when (request.IsCancellationRequested) { return; }
            finally
            {
                if (ReferenceEquals(state.Request, request))
                    state.Request = null;
                request.Dispose();
            }
        }
    }

    public void Dispose() => Reset();
}
