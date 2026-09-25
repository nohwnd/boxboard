using Bevdox.Models;
using Boxboard.Models;

namespace Boxboard.Services;

public sealed class CellSession
{
    public WindowIdentity? BoundWindow { get; internal set; }
    public bool ReconnectPromptSuspected { get; internal set; }
    private string _status = "No client window bound. Drag a machine here, then bind or connect.";
    internal Action<string>? Report { get; init; }
    public string Status
    {
        get => _status;
        internal set
        {
            if (_status == value) return;
            _status = value;
            Report?.Invoke(value);
        }
    }
    public bool Connecting { get; internal set; }
    internal HashSet<WindowIdentity> BeforeLaunch { get; set; } = [];
    internal DateTimeOffset? Deadline { get; set; }
    internal CancellationTokenSource? Request { get; set; }
    internal string? MachineId { get; set; }
    internal bool Failed { get; set; }
}

public sealed record ReconnectPromptCandidate(string ClientName, WindowIdentity Identity, int VisibleWindowCount);
public sealed record ExistingWindowBindings(int Bound, IReadOnlyList<string> Missing,
    IReadOnlyList<string> Ambiguous, IReadOnlyList<string> OtherDesktop, int Moved = 0)
{
    public bool Complete => Missing.Count == 0 && Ambiguous.Count == 0 && OtherDesktop.Count == 0;
}
public sealed record LayoutApplyResult(int Bound, int Moved, int ConnectionRequests);

public sealed class SessionCoordinator(
    ISessionWindows windows,
    Func<DevBoxInstance, CancellationToken, Task<Uri>> getConnection,
    Action<Uri> openConnection,
    TimeProvider? clock = null,
    Action<SessionWindow, DevBoxInstance, Guid>? moveToDesktop = null) : IDisposable
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Dictionary<Guid, CellSession> _sessions = [];
    private IReadOnlyList<SessionWindow> _observed = [];
    private readonly Dictionary<WindowIdentity, PixelRect> _placements = [];
    private readonly Dictionary<WindowIdentity, PlacementFailure> _failedPlacements = [];
    private readonly Dictionary<WindowIdentity, DateTimeOffset> _startupPlacements = [];
    private readonly Dictionary<WindowIdentity, DateTimeOffset> _nextStartupCorrection = [];
    private readonly HashSet<WindowIdentity> _preservedExisting = [];
    private readonly HashSet<WindowIdentity> _manuallyPositioned = [];
    public event Action<SlotAssignment, string>? Activity;
    public IReadOnlyList<ReconnectPromptCandidate> ReconnectPromptCandidates { get; private set; } = [];
    private sealed record PlacementFailure(PixelRect Bounds, int Attempts, DateTimeOffset NextRetry);

    private CellSession NewSession(SlotAssignment slot) => new()
    {
        MachineId = slot.MachineId,
        Report = message => Activity?.Invoke(slot, message)
    };

    public void InvalidatePlacements()
    {
        _placements.Clear();
        _failedPlacements.Clear();
        _startupPlacements.Clear();
        _nextStartupCorrection.Clear();
        _preservedExisting.Clear();
        _manuallyPositioned.Clear();
    }

    public CellSession For(SlotAssignment slot)
    {
        if (!_sessions.TryGetValue(slot.Id, out var session))
            _sessions.Add(slot.Id, session = NewSession(slot));
        if (!BoardSettings.SameId(session.MachineId, slot.MachineId))
        {
            if (session.BoundWindow is { } oldWindow)
            {
                _preservedExisting.Remove(oldWindow);
                _manuallyPositioned.Remove(oldWindow);
                _startupPlacements.Remove(oldWindow);
                _nextStartupCorrection.Remove(oldWindow);
            }
            session.Request?.Cancel();
            session.Request?.Dispose();
            _sessions[slot.Id] = session = NewSession(slot);
        }
        return session;
    }

    public IReadOnlyList<SessionWindow> Candidates(DevBoxInstance machine) => _observed
        .Where(w => string.Equals(w.Title, machine.OriginalName, StringComparison.OrdinalIgnoreCase)).ToList();

    public void Observe()
    {
        _observed = windows.Enumerate();
        if (!windows.GetEnvironment().CanInteract)
        {
            ReconnectPromptCandidates = [];
            CancelPending("Windows is locked or showing a secure desktop. No request was made during the lock.");
            return;
        }
        ReconnectPromptCandidates = _observed
            .GroupBy(w => (w.Identity.ProcessId, w.Identity.ProcessStartUtcTicks))
            .Where(group => group.Count() == 1)
            .Select(group =>
            {
                var client = group.Single();
                return new ReconnectPromptCandidate(client.Title, client.Identity,
                    windows.CountVisibleTopLevelWindows(client.Identity));
            })
            .Where(candidate => candidate.VisibleWindowCount > 1)
            .ToList();
    }

    public void CancelPending(string reason)
    {
        foreach (var session in _sessions.Values.Where(s => s.Connecting))
        {
            session.Request?.Cancel();
            session.Connecting = false;
            session.Deadline = null;
            session.Status = reason;
        }
    }

    public void CancelPending(SlotAssignment slot, string reason)
    {
        var session = For(slot);
        if (!session.Connecting)
            return;
        session.Request?.Cancel();
        session.Connecting = false;
        session.Deadline = null;
        session.Status = reason;
    }

    private BoardEnvironment RequireEnvironment(bool requireCurrentDesktop = true)
    {
        var environment = windows.GetEnvironment();
        if (!environment.CanInteract)
            throw new InvalidOperationException("Windows is locked or showing a secure desktop. No connection or layout change was made.");
        if (requireCurrentDesktop && !environment.IsCurrentDesktop)
            throw new InvalidOperationException("Switch to the target virtual desktop before connecting.");
        return environment;
    }

    public void Bind(SlotAssignment slot, DevBoxInstance machine, WindowIdentity identity,
        bool preservePosition = true)
    {
        var environment = RequireEnvironment(requireCurrentDesktop: false);
        var window = windows.Enumerate().SingleOrDefault(w => w.Identity == identity)
            ?? throw new InvalidOperationException("The selected window no longer exists. Choose its replacement.");
        if (!string.Equals(window.Title, machine.OriginalName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The selected window title no longer matches this Dev Box.");
        if (window.DesktopId != environment.DesktopId)
            throw new InvalidOperationException("That session is on another virtual desktop. Use Task View to move it or Boxboard; no window was moved.");
        var session = For(slot);
        if (_sessions.Values.Any(other => !ReferenceEquals(other, session) && other.BoundWindow == identity))
            throw new InvalidOperationException("That window is already bound to another cell. Clear that cell before rebinding.");
        if (session.BoundWindow is { } oldWindow)
        {
            _preservedExisting.Remove(oldWindow);
            _manuallyPositioned.Remove(oldWindow);
            _startupPlacements.Remove(oldWindow);
            _nextStartupCorrection.Remove(oldWindow);
        }
        session.BoundWindow = identity;
        if (preservePosition)
            _preservedExisting.Add(identity);
        else
            _preservedExisting.Remove(identity);
        session.ReconnectPromptSuspected = false;
        session.Failed = false;
        _failedPlacements.Remove(identity);
        session.Status = preservePosition
            ? $"Bound HWND {identity.Handle}, PID {identity.ProcessId}. Its current position is preserved until Re-apply."
            : $"Bound HWND {identity.Handle}, PID {identity.ProcessId}. Waiting for initial slot placement.";
    }

    public ExistingWindowBindings BindExisting(
        IReadOnlyList<(SlotAssignment Slot, DevBoxInstance Machine)> assignments,
        IReadOnlyList<DevBoxInstance> catalog, bool moveFromOtherDesktop = false)
    {
        if (moveFromOtherDesktop && moveToDesktop is null)
            throw new InvalidOperationException("Cross-desktop movement is unavailable.");
        var environment = RequireEnvironment(requireCurrentDesktop: false);
        var observed = windows.Enumerate();
        var planned = new List<(SlotAssignment Slot, DevBoxInstance Machine, SessionWindow Window)>();
        var missing = new List<string>();
        var ambiguous = new List<string>();
        var otherDesktop = new List<string>();
        foreach (var (slot, machine) in assignments)
        {
            if (!BoardSettings.SameId(slot.MachineId, machine.UniqueId))
                throw new InvalidOperationException($"{slot.Name} no longer identifies {machine.EffectiveName}.");
            var session = For(slot);
            if (catalog.Count(m => string.Equals(m.OriginalName, machine.OriginalName,
                StringComparison.OrdinalIgnoreCase)) != 1)
            {
                ambiguous.Add(machine.EffectiveName);
                session.Status = "Multiple Dev Boxes share this client title; bind the intended window explicitly.";
                continue;
            }
            var matches = observed.Where(w => string.Equals(w.Title, machine.OriginalName,
                StringComparison.OrdinalIgnoreCase)).ToList();
            var local = matches.Where(w => w.DesktopId == environment.DesktopId).ToList();
            if (local.Count > 1 || (moveFromOtherDesktop && matches.Count > 1))
            {
                ambiguous.Add(machine.EffectiveName);
                session.Status = "Multiple matching windows on this desktop; bind the intended one explicitly.";
            }
            else if (local.Count == 1)
                planned.Add((slot, machine, local[0]));
            else if (matches.Count == 1 && moveFromOtherDesktop)
                planned.Add((slot, machine, matches[0]));
            else if (matches.Count > 0)
            {
                otherDesktop.Add(machine.EffectiveName);
                session.Status = "Existing client is on another virtual desktop; no window was moved.";
            }
            else
            {
                missing.Add(machine.EffectiveName);
                session.Status = "No existing client window was found. No connection was requested.";
            }
        }
        if (planned.Select(p => p.Window.Identity).Distinct().Count() != planned.Count ||
            planned.Any(plan => _sessions.Any(entry => entry.Key != plan.Slot.Id &&
                entry.Value.BoundWindow == plan.Window.Identity)))
            throw new InvalidOperationException("An existing client window would be bound to multiple slots.");
        if (moveFromOtherDesktop && ambiguous.Count > 0)
            return new(0, missing, ambiguous, otherDesktop);
        int moved = 0;
        foreach (var plan in planned)
        {
            var window = plan.Window;
            if (window.DesktopId != environment.DesktopId)
            {
                moveToDesktop!(window, plan.Machine, environment.DesktopId);
                window = windows.Enumerate().SingleOrDefault(candidate =>
                    candidate.Identity == window.Identity && candidate.DesktopId == environment.DesktopId)
                    ?? throw new InvalidOperationException(
                        $"{plan.Machine.EffectiveName} did not appear on the target desktop after movement.");
                moved++;
            }
            Bind(plan.Slot, plan.Machine, window.Identity);
        }
        return new(planned.Count, missing, ambiguous, otherDesktop, moved);
    }

    public IReadOnlyList<SessionWindow?> BoundWindows(IReadOnlyList<SlotAssignment> slots)
    {
        var observed = windows.Enumerate();
        return slots.Select(slot => For(slot).BoundWindow is { } identity
            ? observed.SingleOrDefault(window => window.Identity == identity)
            : null).ToList();
    }

    public async Task<LayoutApplyResult> ApplyLayoutAsync(
        IReadOnlyList<(SlotAssignment Slot, DevBoxInstance Machine)> assignments,
        IReadOnlyList<DevBoxInstance> catalog, CancellationToken ct = default,
        bool skipPendingConnections = false)
    {
        if (assignments.Count == 0)
            throw new InvalidOperationException("Assign at least one Dev Box before applying the layout.");
        if (assignments.Select(item => item.Slot.Id).Distinct().Count() != assignments.Count)
            throw new InvalidOperationException("A slot was supplied more than once.");
        var existing = BindExisting(assignments, catalog, moveFromOtherDesktop: true);
        if (existing.Ambiguous.Count > 0 || existing.OtherDesktop.Count > 0)
            throw new InvalidOperationException("The layout has ambiguous clients or clients that could not be moved: " +
                string.Join(", ", existing.Ambiguous.Concat(existing.OtherDesktop)));
        InvalidatePlacements();
        int launched = 0;
        foreach (var (slot, machine) in assignments)
        {
            ct.ThrowIfCancellationRequested();
            if (existing.Missing.Contains(machine.EffectiveName, StringComparer.OrdinalIgnoreCase))
            {
                if (skipPendingConnections && For(slot).Connecting)
                    continue;
                await ConnectAsync(slot, machine, nameIsUnique: true, ct: ct);
                launched++;
            }
        }
        return new(existing.Bound, existing.Moved, launched);
    }

    public async Task<LayoutApplyResult> ApplyAssignedSlotAsync(SlotAssignment slot, DevBoxInstance machine,
        IReadOnlyList<DevBoxInstance> catalog, CancellationToken ct = default)
    {
        var existing = BindExisting([(slot, machine)], catalog, moveFromOtherDesktop: true);
        if (existing.Ambiguous.Count > 0 || existing.OtherDesktop.Count > 0)
            throw new InvalidOperationException("The assigned client is ambiguous or could not be moved: " +
                string.Join(", ", existing.Ambiguous.Concat(existing.OtherDesktop)));
        if (For(slot).BoundWindow is { } identity)
        {
            _placements.Remove(identity);
            _failedPlacements.Remove(identity);
            _preservedExisting.Remove(identity);
            _manuallyPositioned.Remove(identity);
        }
        if (existing.Missing.Count == 0 || For(slot).Connecting)
            return new(existing.Bound, existing.Moved, 0);
        await ConnectAsync(slot, machine, nameIsUnique: true, ct: ct);
        return new(existing.Bound, existing.Moved, 1);
    }

    public async Task ConnectAsync(SlotAssignment slot, DevBoxInstance machine, bool nameIsUnique,
        CancellationToken ct = default)
    {
        var session = For(slot);
        if (session.Connecting)
            throw new InvalidOperationException("A connection request is already pending for this cell.");
        RequireEnvironment(requireCurrentDesktop: moveToDesktop is null);
        Observe();
        var matches = Candidates(machine);
        if (!nameIsUnique || matches.Count > 1)
            throw new InvalidOperationException("Machine/window names are ambiguous. Select and bind the intended existing window explicitly.");
        if (matches.Count == 1 && matches[0].Identity != session.BoundWindow)
            throw new InvalidOperationException("A matching client window already exists. Bind it instead of launching another.");
        if (matches.Any(w => w.DesktopId != windows.GetEnvironment().DesktopId))
            throw new InvalidOperationException("The existing session is on another virtual desktop. Move it with Task View first.");

        session.Request?.Dispose();
        session.Request = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var request = session.Request;
        session.Connecting = true;
        session.Failed = false;
        session.BeforeLaunch = _observed.Select(w => w.Identity).ToHashSet();
        session.Status = "Requesting connection information. Normal sign-in/MFA may be required.";
        try
        {
            var uri = await getConnection(machine, request.Token);
            request.Token.ThrowIfCancellationRequested();
            RequireEnvironment(requireCurrentDesktop: moveToDesktop is null);
            Observe();
            if (Candidates(machine).Any(w => w.Identity != session.BoundWindow &&
                !session.BeforeLaunch.Contains(w.Identity)))
                throw new InvalidOperationException("A matching client appeared while preparing the request. Bind it instead of launching a duplicate.");
            session.Status = $"Launch requested via {uri.Scheme}. No connection success is assumed.";
            openConnection(uri);
            session.Deadline = _clock.GetUtcNow().AddMinutes(2);
            session.Status = "Connection requested; waiting for the client window. Windows App may be authenticating.";
        }
        catch (Exception ex)
        {
            session.Connecting = false;
            session.Deadline = null;
            session.Failed = true;
            session.Status = $"Connection request did not complete: {ex.Message}";
            throw;
        }
    }

    public async Task ArrangeAsync(SlotAssignment slot, DevBoxInstance machine, bool nameIsUnique, PixelRect bounds,
        CancellationToken ct = default)
    {
        var session = For(slot);
        var environment = windows.GetEnvironment();
        if (!environment.CanInteract)
        {
            CancelPending("Windows is locked or showing a secure desktop. No request was made during the lock.");
            return;
        }
        var candidates = Candidates(machine);
        var bound = _observed.SingleOrDefault(w => w.Identity == session.BoundWindow &&
            string.Equals(w.Title, machine.OriginalName, StringComparison.OrdinalIgnoreCase));
        var promptWindow = bound ?? (nameIsUnique && candidates.Count == 1 ? candidates[0] : null);
        var prompt = promptWindow is null ? null :
            ReconnectPromptCandidates.SingleOrDefault(candidate => candidate.Identity == promptWindow.Identity);
        if (prompt is not null)
        {
            session.ReconnectPromptSuspected = true;
            session.Status = $"Possible reconnect prompt for {machine.EffectiveName}: " +
                $"{prompt.VisibleWindowCount} visible windows from its client process. " +
                "Keep on may close this client and request a replacement; connection state remains unknown.";
            return;
        }
        if (session.ReconnectPromptSuspected)
        {
            session.ReconnectPromptSuspected = false;
            session.Status = "The extra client window is no longer visible. Connection state remains unknown.";
        }
        if (bounds.Width < 100 || bounds.Height < 100 || !environment.WorkArea.Contains(bounds))
        {
            session.Status = "Fit Boxboard to one monitor before arranging its windows.";
            return;
        }
        if (bound is null && nameIsUnique && candidates.Count == 1)
        {
            var candidate = candidates[0];
            var sameProcessReplacement = session.BoundWindow is { } prior &&
                candidate.Identity.ProcessId == prior.ProcessId &&
                candidate.Identity.ProcessStartUtcTicks == prior.ProcessStartUtcTicks;
            var requestedReplacement = session.Connecting && session.Deadline is not null &&
                !session.BeforeLaunch.Contains(candidate.Identity);
            if (requestedReplacement && candidate.DesktopId != environment.DesktopId)
            {
                if (moveToDesktop is null)
                {
                    session.Connecting = false;
                    session.Deadline = null;
                    session.Failed = false;
                    session.Status = "Windows App opened the requested client on another virtual desktop. No window was moved; use Task View before binding.";
                    return;
                }
                try
                {
                    moveToDesktop(candidate, machine, environment.DesktopId);
                    session.Status = $"Requested client moved to virtual desktop {environment.DesktopId}; verifying its identity.";
                    _observed = windows.Enumerate();
                    candidate = _observed.SingleOrDefault(w => w.Identity == candidate.Identity &&
                        w.DesktopId == environment.DesktopId)
                        ?? throw new InvalidOperationException("The requested client was not found on the target desktop after movement.");
                }
                catch (Exception ex)
                {
                    session.Connecting = false;
                    session.Deadline = null;
                    session.Failed = true;
                    session.Status = $"Could not move the requested client to its layout: {ex.Message}";
                    throw;
                }
            }
            if ((sameProcessReplacement || requestedReplacement) && candidate.DesktopId == environment.DesktopId)
            {
                session.BoundWindow = candidate.Identity;
                bound = candidate;
                if (requestedReplacement)
                    _startupPlacements[candidate.Identity] = _clock.GetUtcNow().AddSeconds(15);
            }
        }
        if (bound is null)
        {
            if (session.Connecting && session.Deadline is { } deadline && _clock.GetUtcNow() >= deadline)
            {
                session.Connecting = false;
                session.Deadline = null;
                session.Failed = true;
                session.Status = "No unambiguous client window appeared within 2 minutes. Check Windows App; no automatic retry.";
            }
            else if (!session.Connecting && !session.Failed)
                session.Status = candidates.Count > 1 ? "Multiple matching windows. Bind the intended window explicitly." :
                    candidates.Any(w => w.DesktopId != environment.DesktopId) ? "Session is on another virtual desktop. Move it with Task View first." :
                    candidates.Count == 1 ? "Client window found. Bind it to this cell." :
                    "No client window found. Connection state is unknown; use Connect / Reconnect.";
            return;
        }
        if (bound.DesktopId != environment.DesktopId)
        {
            session.Status = "Bound session is on another virtual desktop; layout is paused.";
            return;
        }
        if (bound.Fullscreen)
        {
            session.Connecting = false;
            session.Deadline = null;
            session.Failed = false;
            session.Status = "Client is fullscreen; placement is paused. Windows App launch route/display state needs verification.";
            return;
        }
        if (_preservedExisting.Contains(bound.Identity))
        {
            session.Status = "Existing client bound; its size and position are preserved. Use Re-apply to tile it.";
            return;
        }
        if (_manuallyPositioned.Contains(bound.Identity))
        {
            session.Status = "Client was moved manually. Its position is retained until Re-apply.";
            return;
        }
        var maxPlacementAttempts = _startupPlacements.ContainsKey(bound.Identity) ? 5 : 3;
        var attemptsLabel = maxPlacementAttempts == 3 ? "three" : "five";
        if (_failedPlacements.TryGetValue(bound.Identity, out var priorFailure))
        {
            if (priorFailure.Bounds != bounds)
                _failedPlacements.Remove(bound.Identity);
            else if (priorFailure.Attempts >= maxPlacementAttempts)
            {
                if (session.Failed)
                    return;
                session.Connecting = false;
                session.Deadline = null;
                session.Failed = true;
                session.Status = $"Window placement failed after {attemptsLabel} attempts. Use Re-apply to retry.";
                throw new WindowPlacementRejectedException(session.Status);
            }
            else if (_clock.GetUtcNow() < priorFailure.NextRetry)
                return;
        }
        try
        {
            if (_placements.TryGetValue(bound.Identity, out var previous) && previous == bounds)
            {
                var now = _clock.GetUtcNow();
                if (windows.GetVisibleBounds(bound) != bounds)
                {
                    if (_startupPlacements.TryGetValue(bound.Identity, out var settlingUntil) &&
                        now < settlingUntil)
                    {
                        if (_nextStartupCorrection.TryGetValue(bound.Identity, out var next) && now < next)
                            return;
                        _nextStartupCorrection[bound.Identity] = now.AddSeconds(1);
                        _placements.Remove(bound.Identity);
                        session.Status = "Windows App changed the new client's size during startup; restoring its slot.";
                    }
                    else
                    {
                        _startupPlacements.Remove(bound.Identity);
                        _nextStartupCorrection.Remove(bound.Identity);
                        _manuallyPositioned.Add(bound.Identity);
                        session.Status = "Client was moved manually. Its position is retained until Re-apply.";
                        return;
                    }
                }
                else if (_startupPlacements.TryGetValue(bound.Identity, out var expiresAt) &&
                    now >= expiresAt)
                {
                    _startupPlacements.Remove(bound.Identity);
                    _nextStartupCorrection.Remove(bound.Identity);
                }
            }
            if (!_placements.TryGetValue(bound.Identity, out previous) || previous != bounds)
            {
                if (windows.GetVisibleBounds(bound) != bounds)
                    await windows.PlaceAsync(bound, bounds, ct);
                _placements[bound.Identity] = bounds;
                _failedPlacements.Remove(bound.Identity);
            }
            session.Connecting = false;
            session.Deadline = null;
            session.Failed = false;
            session.Status = $"Placed HWND {bound.Identity.Handle}, PID {bound.Identity.ProcessId} at [{bounds.X}, {bounds.Y}, {bounds.Width}, {bounds.Height}]. Connection state is Windows App-owned.";
        }
        catch (WindowPlacementRejectedException ex)
        {
            _failedPlacements.TryGetValue(bound.Identity, out var failure);
            var attempts = failure?.Bounds == bounds ? failure.Attempts + 1 : 1;
            var retryDelay = attempts == 1 ? 2 : 4;
            _failedPlacements[bound.Identity] = new(bounds, attempts,
                _clock.GetUtcNow().AddSeconds(retryDelay));
            session.Status = attempts < maxPlacementAttempts
                ? $"Windows App did not accept the initial cell size (attempt {attempts}/{maxPlacementAttempts}). " +
                    $"Retrying in {retryDelay} seconds; no new connection will be launched. {ex.Message}"
                : $"Window placement failed after {attemptsLabel} attempts. Use Re-apply to retry. {ex.Message}";
            if (attempts >= maxPlacementAttempts)
            {
                session.Connecting = false;
                session.Deadline = null;
                session.Failed = true;
                throw;
            }
        }
        catch (Exception ex)
        {
            session.Status = $"Window placement failed: {ex.Message}";
            throw;
        }
    }

    public void Dispose()
    {
        foreach (var session in _sessions.Values)
        {
            session.Request?.Cancel();
            session.Request?.Dispose();
        }
    }
}
