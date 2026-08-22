using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;
using LamSims.Core.Catalogs;
using LamSims.Core.Downloading;
using LamSims.Core.Installing;

namespace LamSims.Core.Queueing;

/// <summary>
/// Runs packs one at a time and publishes the whole queue's state after every change.
///
/// Sequencing is required for correctness: two runs of one pack overwrite each other's chunk
/// sidecar, one can delete an archive the other is still extracting, and two concurrent packs
/// share a single connection budget. A second instance of the application is held off by
/// <see cref="PackLock"/> rather than by this ordering.
///
/// Controls may be called from any thread while <see cref="RunAsync"/> runs.
///
/// <b>A queue that has been run must eventually be disposed.</b> <see cref="RunAsync"/> leaves its
/// linked cancellation source alive on purpose and <see cref="DisposeAsync"/> is the only thing
/// that ever releases it.
/// </summary>
public sealed class PackQueue : IAsyncDisposable
{
    private sealed class Item
    {
        /// <summary>
        /// Settable, not init-only: re-enqueueing a terminal item is the retry mechanism, and a
        /// retry after "mirror is down" is precisely the case where the shell has refreshed the
        /// catalog. Reset swaps in the new entry so the retry uses the new mirrors, size and
        /// digest rather than the dead ones it just failed against.
        /// </summary>
        public required PackEntry Pack { get; set; }
        public required string GameDirectory { get; set; }
        public QueueItemState State { get; set; } = QueueItemState.Queued;
        public long BytesCompleted { get; set; }
        public long TotalBytes { get; set; }
        public double BytesPerSecond { get; set; }
        public TimeSpan? Eta { get; set; }
        public string? CurrentEntry { get; set; }
        public string? Error { get; set; }
        public IReadOnlyList<string> Warnings { get; set; } = Array.Empty<string>();
        public int BlockedAttempts { get; set; }

        /// <summary>
        /// What <see cref="_completedCount"/> read when this item was last refused its lock. The
        /// retry pass compares against it to tell "another pack has run since" from "nothing has
        /// happened at all", so the one free retry is spent on a wait that could plausibly have
        /// helped.
        /// </summary>
        public long BlockedAtCount { get; set; }

        /// <summary>
        /// Set when a pause, rather than the user, cancelled this item, so its Cancelled result
        /// puts it back in the queue instead of ending it.
        /// </summary>
        public bool PausedOut { get; set; }

        /// <summary>
        /// Zero, not long.MinValue. Environment.TickCount64 is milliseconds since boot, so
        /// `now - 0` exceeds any interval except on a machine that booted less than one interval
        /// ago. A MinValue sentinel would make `now - LastProgressTicks` overflow (the project
        /// does not set CheckForOverflowUnderflow, so it wraps to about -9.2e18), the guard would
        /// return before assigning, and the sentinel would never be cleared.
        /// </summary>
        public long LastProgressTicks { get; set; }

        public QueueItemSnapshot ToSnapshot() => new(
            Pack.Code, Pack.Name, State, BytesCompleted, TotalBytes,
            BytesPerSecond, Eta, CurrentEntry, Error, Warnings)
        {
            IsFinal = IsTerminal(this),
        };
    }

    /// <summary>
    /// One iteration's cancellation source, wrapped so that tripping it and disposing it are
    /// serialised. A control must not call Cancel while holding <see cref="_gate"/>: Cancel runs
    /// every registered callback synchronously on the calling thread, and under the real workflow
    /// that is a linked failure source, one deadline source per in-flight range request with
    /// SocketsHttpHandler's connection teardown hanging off each, the per-read deadlines, and the
    /// timer and semaphore registrations, all serial, all of it blocking every progress sink,
    /// Publish, Settle and every other control for as long as it takes. Moving the bare Cancel
    /// outside the lock instead would race the loop's disposal of the source, and Cancel racing
    /// Dispose is undefined behaviour rather than an ObjectDisposedException.
    ///
    /// <see cref="_g"/> is a leaf lock: nothing that holds it ever takes <see cref="_gate"/>, and
    /// nothing that holds <see cref="_gate"/> ever takes it. That second half is why
    /// <see cref="IsCancellationRequested"/> is a bare field read: a control reads it under
    /// <see cref="_gate"/>, so taking <see cref="_g"/> there would invert the order against a
    /// cancellation callback that re-enters the queue. The read is safe after Dispose; Token is
    /// not, and is only ever read by the loop that owns the run.
    /// </summary>
    private sealed class ActiveRun(CancellationTokenSource cts)
    {
        private readonly Lock _g = new();
        private bool _retired;

        public CancellationToken Token => cts.Token;

        public bool IsCancellationRequested => cts.IsCancellationRequested;

        /// <summary>A no-op once the run has been retired, rather than undefined behaviour.</summary>
        public void Cancel()
        {
            lock (_g)
                if (!_retired)
                    cts.Cancel();
        }

        public void Retire()
        {
            lock (_g)
            {
                _retired = true;
                cts.Dispose();
            }
        }
    }

    private readonly IPackRunner _runner;
    private readonly DownloadPaths _paths;
    private readonly QueueOptions _options;

    private readonly Channel<QueueUpdate> _updates =
        Channel.CreateUnbounded<QueueUpdate>(new UnboundedChannelOptions { SingleReader = true });

    private readonly Lock _gate = new();
    private readonly List<Item> _items = new();
    private readonly SemaphoreSlim _signal = new(0);

    private QueueState _state = QueueState.Idle;
    private bool _completed;
    private int _started;
    private int _disposed;

    /// <summary>
    /// The run's own cancellation source, linked to the token <see cref="RunAsync"/> was handed.
    /// Written once by <see cref="RunAsync"/> and read by <see cref="DisposeAsync"/> from whatever
    /// thread disposes, hence the volatile access on both sides. Null until the run reaches the
    /// assignment, and <see cref="DisposeAsync"/> copes with that rather than assuming it.
    /// </summary>
    private CancellationTokenSource? _loop;

    /// <summary>
    /// Completed by <see cref="RunAsync"/>'s finally, as its very last act, so awaiting it means
    /// the loop has stopped, the item in flight has released its pack lock and the channel is
    /// closed. That is the whole of what <see cref="DisposeAsync"/> waits for.
    /// </summary>
    private readonly TaskCompletionSource _finished =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// The item in flight and the run that stops it, or null between items. Both are written
    /// under <see cref="_gate"/> by the same lock acquisition that selects the item, and cleared
    /// together in <see cref="RunItemAsync"/>'s finally, so a control never sees one without the
    /// other.
    ///
    /// Nothing is claimed about where the loop has got to by the time a control calls
    /// <see cref="ActiveRun.Cancel"/>: the control has released <see cref="_gate"/> by then, and
    /// the loop may already have retired the run. Cancel and Retire are serialised by the run's
    /// own leaf lock, so either ordering is safe.
    /// </summary>
    private ActiveRun? _active;
    private Item? _activeItem;

    /// <summary>
    /// A pause the loop has not acted on yet. Distinct from <c>_state == Pausing</c> because
    /// Resume clears it: an install that finishes after the user changed their mind must not
    /// park the queue.
    /// </summary>
    private bool _pauseRequested;

    /// <summary>
    /// How many run attempts have ended, counted on every exit from RunItemAsync including
    /// cancellation and an exception, not only on a clean finish. RequeueBlocked uses it to tell
    /// "the queue went quiet because work happened" from "the queue went quiet immediately", so a
    /// blocked pack's one free retry is not burned microseconds after it was refused.
    /// </summary>
    private long _completedCount;

    public PackQueue(IPackRunner runner, DownloadPaths paths, QueueOptions? options = null)
    {
        _runner = runner;
        _paths = paths;
        _options = options ?? new QueueOptions();
    }

    public IAsyncEnumerable<QueueUpdate> Updates => _updates.Reader.ReadAllAsync();

    public void Enqueue(PackEntry pack, string gameDirectory)
    {
        lock (_gate)
        {
            if (_completed) return;

            var existing = _items.FirstOrDefault(i => i.Pack.Code == pack.Code);

            if (existing is null)
            {
                _items.Add(new Item { Pack = pack, GameDirectory = gameDirectory });
            }
            else if (IsTerminal(existing) || existing.State == QueueItemState.Blocked)
            {
                // Re-enqueueing a finished item is how a failed pack is retried. It goes to the
                // back with its history cleared; a second list entry for one pack would not be
                // meaningful.
                //
                // A once-blocked item is pending rather than terminal, but it is treated as an
                // explicit retry request. RequeueBlocked only retries when the wait could
                // plausibly have helped, so in the long-lived shell (no Complete()) a lone
                // blocked pack rests indefinitely, and with two contending packs neither block
                // raises _completedCount and both rest. Ignoring the re-enqueue would make the
                // one action the user can take a silent no-op, without even a Publish, because
                // that return sits inside the lock.
                Reset(existing, pack, gameDirectory);
                _items.Remove(existing);
                _items.Add(existing);
            }
            else
            {
                return;   // pending or active: already going to run
            }
        }

        Publish();
        _signal.Release();
    }

    /// <summary>
    /// No more work is coming, so finish what is queued and stop, including a pack a pause
    /// cancelled out and put back in the queue.
    ///
    /// This clears the pause rather than respecting it. Left standing, a pause with a Queued
    /// item parks the loop on its signal forever: Finished() is false, nothing releases the
    /// signal again, RunAsync never returns, its finally never runs, and every consumer sitting
    /// in `await foreach (var u in queue.Updates)` waits on a channel nobody will close.
    /// </summary>
    public void Complete()
    {
        // Only on the transition: an unconditional publish would emit a redundant update on the
        // ordinary "shut down an idle queue" path. TakeNext publishes the Idle that follows when
        // it turns out there was nothing queued after all.
        if (StopAcceptingWork()) Publish();

        _signal.Release();
    }

    /// <summary>
    /// The "no more work is coming" transition, shared by <see cref="Complete"/> and
    /// <see cref="DisposeAsync"/>. Clearing the pause is the half that matters: see
    /// <see cref="Complete"/> for why a standing pause with a Queued item parks the loop forever.
    ///
    /// Returns whether the queue came out of a pause, which is the only case worth publishing.
    /// <see cref="DisposeAsync"/> ignores that and publishes nothing, because the last update a
    /// consumer should see is the Idle that <see cref="RunAsync"/>'s finally writes.
    /// </summary>
    private bool StopAcceptingWork()
    {
        lock (_gate)
        {
            _completed = true;
            _pauseRequested = false;

            var resumed = _state is QueueState.Paused or QueueState.Pausing;
            if (resumed) _state = QueueState.Running;
            return resumed;
        }
    }

    /// <summary>
    /// Stops the queue. A download stops now, since cancellation plus the resumable .part file
    /// is the only pause the engine has, and the pack goes back to Queued so Resume picks it up.
    /// An extract is allowed to finish, because extraction is not resumable and the pause lands
    /// at the item boundary instead.
    ///
    /// NEVER cache a ProgressTracker across a pause and resume. Under cancellation,
    /// OperationCanceledException matches neither catch filter in ChunkFetcher, so Abandoned is
    /// never reported and _provisional[worker] keeps the bytes that attempt had read.
    /// SegmentedDownloader builds a fresh tracker per DownloadAsync call and a resume re-enters
    /// through that same call, so those stranded bytes die with the tracker. Anything holding one
    /// across a resume turns that into permanent progress inflation.
    ///
    /// Refused after Complete(), the reciprocal of Complete() clearing a standing pause: a pause
    /// with queued work parks the loop on its signal, and once no more work is coming there is no
    /// Enqueue left to release it. Only a Resume could, and a shell that called Complete() is on
    /// its way out and will not send one.
    /// </summary>
    public void Pause()
    {
        ActiveRun? run = null;

        lock (_gate)
        {
            if (_completed) return;
            if (_state is QueueState.Pausing or QueueState.Paused) return;

            _pauseRequested = true;

            if (_activeItem is null)
            {
                // Nothing in flight, so there is no boundary to wait for.
                _state = QueueState.Paused;
            }
            else if (_activeItem.State == QueueItemState.Installing || IsTerminal(_activeItem))
            {
                // Installing: extraction is not resumable, so the pause lands at the item
                // boundary. Terminal: Settle has already decided how this run ended and
                // RunItemAsync's finally is on its way to the same boundary, so stamping
                // PausedOut now would requeue a pack that finished on its own.
                _state = QueueState.Pausing;
            }
            else
            {
                _state = QueueState.Pausing;

                // A token that is already tripped was tripped by someone whose intent outranks a
                // pause: a user Cancel or CancelAll, which clear PausedOut and leave the item
                // unwinding, or the RunAsync token during shutdown. Re-stamping PausedOut here
                // would make Settle read that ending as a requeue and put a pack the user
                // cancelled back in the queue, to install itself on the next Resume. The window
                // is the whole runner unwind, not an instant. After a landed pause and a Resume
                // there is nothing to lose: TakeNext hands out a fresh run.
                if (!_active!.IsCancellationRequested)
                {
                    _activeItem.PausedOut = true;
                    run = _active;
                }
            }
        }

        Publish();

        // Deliberately not atomic with the PausedOut write above, and it must stay that way: see
        // ActiveRun for why Cancel cannot run under _gate. Nothing reads PausedOut in between,
        // since Settle reads it exactly once, long after both.
        run?.Cancel();
    }

    /// <summary>
    /// Also cancels a pause that has not landed yet, so a user who changes their mind during an
    /// install does not have to press this twice.
    /// </summary>
    public void Resume()
    {
        lock (_gate)
        {
            if (_state is not (QueueState.Paused or QueueState.Pausing)) return;

            _pauseRequested = false;
            _state = QueueState.Running;
        }

        Publish();
        _signal.Release();
    }

    /// <summary>
    /// Ends one pack. The active one is stopped through its token so the runner unwinds and
    /// Settle records the ending; a pending one is marked here. A terminal one is ignored,
    /// because it has already ended and rewriting a Completed pack as Cancelled would misreport
    /// work that actually happened.
    /// </summary>
    public void Cancel(string code)
    {
        ActiveRun? run = null;

        lock (_gate)
        {
            var item = _items.FirstOrDefault(i => i.Pack.Code == code);
            if (item is null || IsTerminal(item)) return;

            if (ReferenceEquals(item, _activeItem))
            {
                // Cleared, not merely left alone: a pause that cancelled this same item moments
                // ago set it, and Settle would then turn the user's cancel into a requeue.
                item.PausedOut = false;
                run = _active;
            }
            else
            {
                item.State = QueueItemState.Cancelled;
            }
        }

        Publish();

        // Cancelling the last Queued item can flip Finished() from false to true, and a loop
        // parked on the signal has nothing else to wake it. Spurious when the active item was the
        // one cancelled, which costs nothing: TakeNext absorbs the permit and publishes nothing
        // unless the state actually moved.
        _signal.Release();
        run?.Cancel();
    }

    public void CancelAll()
    {
        ActiveRun? run = null;
        var changed = false;

        lock (_gate)
        {
            foreach (var item in _items)
            {
                if (IsTerminal(item)) continue;

                changed = true;

                if (ReferenceEquals(item, _activeItem))
                {
                    item.PausedOut = false;
                    run = _active;
                }
                else
                {
                    // Pending includes a blocked item still awaiting its retry: leaving it
                    // Blocked would let RequeueBlocked resurrect something the user just
                    // cancelled.
                    item.State = QueueItemState.Cancelled;
                }
            }
        }

        if (!changed) return;

        Publish();
        _signal.Release();
        run?.Cancel();
    }

    /// <summary>
    /// Takes a pack out of the list entirely. Refuses the active one, since removing it would
    /// leave it running as a ghost, holding its lock and moving bytes while invisible in every
    /// update, and refuses a terminal one, which the user keeps in view to read its error or
    /// retry it.
    /// </summary>
    public bool Remove(string code)
    {
        bool removed;

        lock (_gate)
        {
            var item = _items.FirstOrDefault(i => i.Pack.Code == code);
            removed = item is not null && !IsTerminal(item) && !ReferenceEquals(item, _activeItem);
            if (removed) _items.Remove(item!);
        }

        if (removed)
        {
            Publish();

            // Removing the last Queued item flips Finished() the same way a cancel does.
            _signal.Release();
        }

        return removed;
    }

    /// <summary>
    /// Drives the queue until it drains after <see cref="Complete"/>, until <paramref name="ct"/>
    /// trips, or until <see cref="DisposeAsync"/> stops it. Returns normally in all three cases,
    /// since a queue told to stop has not failed, and may be called only once.
    ///
    /// <b>A queue that has been run must eventually be disposed.</b> The linked source created
    /// here is deliberately not disposed here, because <see cref="DisposeAsync"/> cancels through
    /// it and <c>Cancel()</c> on a disposed source throws, and disposal routinely follows a clean
    /// <c>Complete(); await run;</c>. So the source outlives this method, and with a real
    /// cancellable <paramref name="ct"/> it holds a registration on it. A caller that never
    /// disposes leaks exactly that: one CancellationTokenSource plus its registration node on the
    /// shell's token, per run. It does not leak the queue, because CreateLinkedTokenSource
    /// registers a callback whose state is the linked source itself, and nothing in that closure
    /// reaches this instance, its items or its update backlog. Each is small but the count is
    /// unbounded: every queue ever run against one long-lived token adds another, and none goes
    /// away before that token's source does.
    ///
    /// Refused once <see cref="DisposeAsync"/> has run.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        // Outside the try on purpose, and the only thing that is: the second caller must not
        // run the finally and complete a channel the first caller owns.
        if (Interlocked.Exchange(ref _started, 1) == 1)
            throw new InvalidOperationException("This queue has already been run.");

        // Declared out here and assigned inside the try, rather than built before it: this method
        // has already set _started, and DisposeAsync waits on _finished for as long as _started is
        // set. Any throw between the exchange above and the try would leave every later disposal
        // hanging on a _finished nobody completes, and CreateLinkedTokenSource against an
        // already-disposed source can throw.
        CancellationTokenSource? loop = null;

        try
        {
            // A run after disposal is refused rather than quietly started: items enqueued before
            // disposal are still Queued, so the loop would take pack locks and install packs for a
            // queue the shell has already let go of, through a linked source nothing will ever
            // dispose. Inside the try, unlike the _started guard above, precisely because this
            // caller *is* the finally's owner: the exchange made it so, and a disposer concurrent
            // with this call is already waiting on the _finished the finally completes.
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);

            // Ownership belongs to DisposeAsync, per the remark above. Published volatile because a
            // concurrent DisposeAsync reads it; a disposal that lands in the gap since _started was
            // set finds null and degrades to "drain what is queued", which still terminates because it
            // has already set _completed and released the signal.
            loop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Volatile.Write(ref _loop, loop);

            // Inside the try because Directory.CreateDirectory does throw: a read-only root, a
            // denied permission, or a plain file sitting where the directory should be. Outside
            // it, that fault would skip the finally and leave every consumer already inside
            // `await foreach (var u in queue.Updates)` waiting on a channel nobody will close.
            // Still the first statement, because DownloadPaths.Root is otherwise created inside
            // SegmentedDownloader.DownloadAsync, which runs after PackLock would need it.
            _paths.EnsureCreated();

            await LoopAsync(loop.Token);
        }
        catch (OperationCanceledException) when (loop?.IsCancellationRequested == true)
        {
            // A queue told to stop has not failed. Filtered on the loop source rather than on ct,
            // because a queue stopped by DisposeAsync has not failed either, and the loop only
            // ever sees the linked token, so ct alone would let a disposal's own cancellation
            // escape as a fault out of `await run`.
        }
        finally
        {
            // RunAsync owns completion, on every exit path. Two closers would race and one
            // would take a ChannelClosedException out of the loop.
            lock (_gate) _state = QueueState.Idle;
            Publish();
            _updates.Writer.TryComplete();

            // Last, so that a DisposeAsync released by this has nothing left to wait for: the item
            // in flight has already unwound through RunItemAsync's finally and let go of its pack
            // lock, and the channel is closed.
            _finished.TrySetResult();
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            // Created before selection and retired at the end of every iteration, whether an item
            // was taken or not: TakeNext registers it as _active under the same lock that picks
            // the item, so a control that arrives an instant later already has a handle on the
            // run it is meant to stop. One run per iteration, never reused, because a source that
            // a pause already cancelled would stop the resumed run before it began. The finally is
            // what makes Retire unmissable, including on the exception paths, and Retire is what
            // makes a control's Cancel a no-op instead of a race against Dispose.
            var run = new ActiveRun(CancellationTokenSource.CreateLinkedTokenSource(ct));

            try
            {
                var next = TakeNext(run, out var stateChanged);

                // Both directions matter, and neither is observable without this. In the
                // long-lived shell (no Complete(), RunAsync running until its token trips)
                // RunAsync's finally never arrives, so without publishing here the last update a
                // consumer ever sees says Running with every item terminal, indefinitely.
                if (stateChanged) Publish();

                if (next is null)
                {
                    // Before the Finished check and before the wait: the queue going quiet is
                    // precisely the moment a pack refused its lock earlier is owed its retry, and
                    // a true return means something is Queued again for the next pass to take.
                    if (RequeueBlocked()) continue;
                    if (Finished()) return;

                    await _signal.WaitAsync(ct);
                    continue;
                }

                await RunItemAsync(next, run);
            }
            finally
            {
                run.Retire();
            }
        }
    }

    /// <summary>
    /// The next item to run, or null when there is nothing runnable right now, and the point
    /// where the run is registered so the controls can reach it.
    ///
    /// The registration happens under the same lock acquisition that selects the item, and must
    /// stay there. Any gap between selecting and registering is a window where a control acts on
    /// the wrong world: Pause would see no active item and jump straight to Paused while the
    /// item ran on un-cancelled, Cancel would mark it Cancelled only for Settle to overwrite
    /// that, and Remove would delete it from the list while it kept running as a ghost.
    ///
    /// The pause check leads, and is what keeps the loop from spinning. Pause is the one thing
    /// that legitimately returns an item to Queued, so without it an iteration would run,
    /// consume no work, and find the same item still Queued: a hot loop appending a QueueUpdate
    /// per turn to an unbounded channel.
    ///
    /// <paramref name="stateChanged"/> reports whether the queue's own state moved, so the
    /// caller publishes the transition and nothing else: an unconditional publish would emit a
    /// redundant Idle update on every spurious wake-up. It is assigned in a finally because this
    /// method has three exits and none of them may strand the state.
    /// </summary>
    private Item? TakeNext(ActiveRun run, out bool stateChanged)
    {
        lock (_gate)
        {
            var previous = _state;

            try
            {
                if (_pauseRequested || _state is QueueState.Paused or QueueState.Pausing) return null;

                var next = _items.FirstOrDefault(i => i.State == QueueItemState.Queued);
                if (next is null)
                {
                    _state = QueueState.Idle;
                    return null;
                }

                _state = QueueState.Running;
                _active = run;
                _activeItem = next;

                // A stale flag from an earlier pause of this same pack would make Settle read
                // this run's ending as a requeue.
                next.PausedOut = false;
                return next;
            }
            finally
            {
                stateChanged = _state != previous;
            }
        }
    }

    /// <summary>
    /// Nothing left to do and no more coming. The blocked clause is reachable: RequeueBlocked and
    /// this method are two separate <see cref="_gate"/> acquisitions, and Complete() runs on
    /// whatever thread the shell calls it from. A lone Blocked-at-one-attempt item whose wait
    /// could not plausibly have helped yet is skipped by RequeueBlocked with <see cref="_completed"/>
    /// still false, the gate is released, Complete() flips it, and this method then sees a completed
    /// queue with nothing Queued. Without the clause the loop returns there: the channel closes with
    /// the pack Blocked at one attempt, carrying an error that promises a retry it never got, and
    /// RunAsync returns normally so nothing reports the loss. With it, Finished is false,
    /// WaitAsync consumes the permit Complete() has just released, and the next pass requeues and
    /// retries. The window is only a few instructions wide.
    ///
    /// It cannot park the loop instead. Reaching here with <see cref="_completed"/> set and a
    /// once-blocked item present means exactly that window, and Complete()'s permit is already
    /// pending; every other route has RequeueBlocked returning true and never reaches this check.
    ///
    /// Flipping the two calls in the idle branch breaks it in the other direction: the queue
    /// would return while a pack was still owed its retry.
    /// </summary>
    /// </summary>
    private bool Finished()
    {
        lock (_gate)
            return _completed
                && !_items.Any(i => i.State == QueueItemState.Queued)
                && !_items.Any(i => i.State == QueueItemState.Blocked && i.BlockedAttempts == 1);
    }

    /// <summary>
    /// Runs one item under its pack lock, or records that another copy of the application holds
    /// it. The lock covers verify, download and install together, because a second instance that
    /// took the pack between phases would overwrite the same chunk sidecar and delete an archive
    /// this one is still extracting.
    ///
    /// The acquisition itself is *inside* the try, and both its outcomes branch inside it rather
    /// than returning early, because deregistration, the deferred pause and the publish live in the
    /// finally and there must go on being exactly one place that does them. A blocked pack has ended
    /// its attempt just as surely as a completed one, so the controls must see a deregistered queue
    /// immediately afterwards.
    ///
    /// A *throwing* acquire (a missing root, a permission that changed mid-run, a directory where
    /// the lock file should be) becomes a Failed item with the message, like any other I/O failure,
    /// rather than killing the queue. Above the try it would skip the finally instead, and the
    /// damage is the deregistration that never happens rather than the fault itself: _activeItem
    /// goes on naming a retired run, so Remove refuses the pack forever, Cancel no-ops, Pause
    /// reaches Pausing and never Paused, and the last snapshot the user sees says Queued with no
    /// error at all.
    /// </summary>
    /// </summary>
    private async Task RunItemAsync(Item item, ActiveRun run)
    {
        PackLock? held = null;
        var blocked = false;

        try
        {
            // TryAcquire returning null is the only signal of contention there is. The lock file is
            // deliberately left on disk when a lock is released (see PackLock), so its existence
            // says nothing about whether the lock is held, and nothing here may infer state from it
            // or delete one.
            held = PackLock.TryAcquire(_paths, item.Pack.Code);
            blocked = held is null;

            if (blocked) MarkBlocked(item);
            else Settle(item, await InvokeRunnerAsync(item, run.Token));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Cancellation is excluded because it is already handled and is not a failure:
            // InvokeRunnerAsync turns a trip of this run's own token into a Cancelled *result* for
            // Settle to read, and routes any other OperationCanceledException, an HTTP read
            // timeout for instance, through its own catch as a Failed. So nothing reachable throws
            // one past here; if something ever does, it belongs to a shutdown and must not land
            // "The operation was canceled." in the user's error column.
            lock (_gate)
            {
                item.State = QueueItemState.Failed;
                item.Error = e.Message;
            }
        }
        finally
        {
            // Before the state update and the publish, so the snapshot a consumer reads after this
            // item cannot claim the pack is done while its lock is still held.
            held?.Dispose();

            lock (_gate)
            {
                _active = null;
                _activeItem = null;

                // Stamped, not incremented, on the blocked path; see MarkBlocked. The throwing
                // acquire counts as an attempt: blocked stays false there, which is what this field's
                // "an attempt happened, including cancellation and an exception" reading requires.
                if (!blocked)
                {
                    _completedCount++;

                    // The one free retry is per contiguous run of refusals, not per item lifetime.
                    // A pack that got its lock and then went back to Queued, as a pause does, must
                    // not carry a spent attempt into a block that happens later for an unrelated
                    // reason, or it goes terminal with no retry and an error claiming the queue
                    // drained when nothing drained.
                    item.BlockedAttempts = 0;
                    item.BlockedAtCount = 0;
                }

                // The pause deferred through an install takes effect here, at the item boundary,
                // and a pause that already cancelled a download settles here too. Reading
                // _pauseRequested rather than _state is what lets a Resume during Pausing undo
                // the pause without having to reach into the item that is still running.
                if (_pauseRequested) _state = QueueState.Paused;
            }

            Publish();
        }
    }

    private void MarkBlocked(Item item)
    {
        lock (_gate)
        {
            item.BlockedAttempts++;
            item.State = QueueItemState.Blocked;

            // Stamped, not incremented: _completedCount must NOT rise here. Bumping it would make
            // `_completedCount != item.BlockedAtCount` true on the very next pass and burn the one
            // free retry microseconds after the block, against the same still-held lock, which is
            // exactly the long-hold case the retry cannot win.
            item.BlockedAtCount = _completedCount;

            // Neither half may promise a retry that is not scheduled. The first attempt's retry
            // waits on the rest of the queue, and in a long-lived shell the rest of the queue may
            // never produce anything to wait for, so the message offers the action that always
            // works, which Enqueue now honours for a blocked pack. The second says what happened
            // rather than "when the queue drained", which is only one of the ways a retry is earned.
            item.Error =
                $"Another copy of this application is working on '{item.Pack.Code}'. "
                + (item.BlockedAttempts >= 2
                    ? "It was still busy when this pack was retried, so the pack was left alone; queue it again once that copy has finished."
                    : "It will be retried once the rest of the queue has run — or queue it again to retry it right away.");
        }
    }

    /// <summary>
    /// Gives a once-blocked item one more go, but only when the wait could plausibly have helped:
    /// another item has finished since it was refused, or the caller has said no more work is
    /// coming. Retrying the instant it is blocked would burn the single free attempt microseconds
    /// later against the same still-held lock, which is exactly the long-hold case the retry cannot
    /// win. An item that is the only thing in a still-open queue therefore waits until more work
    /// arrives or Complete() is called. In a long-lived shell neither may ever come, which is why
    /// Enqueue treats a re-enqueue of a blocked pack as an explicit "retry it now".
    ///
    /// Terminates because the attempt count only rises and an item this pass re-queues can be
    /// blocked at most once more before becoming terminal.
    /// </summary>
    /// </summary>
    private bool RequeueBlocked()
    {
        var requeued = false;

        lock (_gate)
        {
            if (_items.Any(i => i.State == QueueItemState.Queued)) return false;

            foreach (var item in _items)
            {
                if (item.State != QueueItemState.Blocked || item.BlockedAttempts != 1) continue;
                if (!_completed && _completedCount == item.BlockedAtCount) continue;

                item.State = QueueItemState.Queued;
                item.Error = null;
                requeued = true;
            }
        }

        if (requeued) Publish();
        return requeued;
    }

    private async Task<PackWorkflowResult?> InvokeRunnerAsync(Item item, CancellationToken ct)
    {
        try
        {
            var result = await _runner.RunAsync(
                item.Pack,
                item.GameDirectory,
                new PhaseSink(this, item),
                new DownloadSink(this, item),
                new InstallSink(this, item),
                ct);

            // A null result is not "already handled": nothing has touched the item yet, and
            // returning null would leave it Queued for TakeNext to pick straight back up, with
            // every await completing synchronously, appending a QueueUpdate per turn to an
            // unbounded channel until the process runs out of memory. Fail it here so there is
            // exactly one meaning for the null Settle sees.
            if (result is null)
            {
                lock (_gate)
                {
                    item.State = QueueItemState.Failed;
                    item.Error = "The runner returned no result.";
                }
            }

            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Result-shaped, so Settle stays the single place that decides what a cancellation
            // means. The filter discriminates on *which* token tripped, not on exception type:
            // a cancellation from the queue's own token is a stop, while an
            // OperationCanceledException from anything else, an HTTP read timeout for instance,
            // is a genuine failure and must fall through to the catch below. A pause hands the
            // runner a linked token whose trip has to become a requeue rather than a permanent
            // Failed.
            return new PackWorkflowResult(
                PackStage.Downloading, DownloadResult.Cancelled(), null, Array.Empty<string>());
        }
        catch (Exception e)
        {
            // A runner exception never escapes the loop and never kills the queue: neither the
            // real workflow nor a test double is provably result-shaped.
            lock (_gate)
            {
                item.State = QueueItemState.Failed;
                item.Error = e.Message;
            }

            return null;
        }
    }

    private void Settle(Item item, PackWorkflowResult? result)
    {
        // Null now has exactly one meaning: InvokeRunnerAsync already marked the item Failed,
        // whether the runner threw or handed back nothing.
        if (result is null) return;

        lock (_gate)
        {
            item.Warnings = result.Warnings;

            item.State = result switch
            {
                { Install.Outcome: InstallOutcome.Installed } => QueueItemState.Completed,
                { Install.Outcome: InstallOutcome.Cancelled } => QueueItemState.Cancelled,
                { Install: not null } => QueueItemState.Failed,
                { Download.Outcome: DownloadOutcome.Cancelled } => QueueItemState.Cancelled,
                { Download: not null } => QueueItemState.Failed,
                // Unreachable backstop: PackWorkflow always returns a non-null Download or
                // Install. Failed rather than a throw, so a future stage that forgets one
                // degrades into a visible error instead of killing the loop.
                _ => QueueItemState.Failed,
            };

            item.Error ??= result.Install?.Error ?? result.Download?.Error;

            // A pause is not an ending. This is the single place the conversion happens, and it
            // happens under the same lock acquisition that set Cancelled, so no consumer ever
            // sees a snapshot claiming the paused pack was cancelled. Deliberately not
            // duplicated in InvokeRunnerAsync's OperationCanceledException catch: that catch
            // returns a Cancelled *result*, which flows through here like any other, and a
            // second conversion site would give one item two places that could requeue it.
            if (item.State == QueueItemState.Cancelled && item.PausedOut)
            {
                item.State = QueueItemState.Queued;
                item.PausedOut = false;

                // A resumed SegmentedDownloader rebuilds its baseline from committed chunks
                // alone, and ChunkFetcher reports Abandoned only for a validator mismatch or a
                // retryable error, never for the cancellation a pause raises. So every byte read
                // into an uncommitted chunk is refetched, and keeping the figure here would make
                // a caller watch BytesCompleted fall on resume. The re-enqueue path already draws
                // this boundary in Reset.
                item.BytesCompleted = 0;
                item.TotalBytes = 0;
                item.BytesPerSecond = 0;
                item.Eta = null;
                item.CurrentEntry = null;
            }
        }
    }

    private static bool IsTerminal(Item item) =>
        item.State is QueueItemState.Completed or QueueItemState.Failed or QueueItemState.Cancelled
        || (item.State == QueueItemState.Blocked && item.BlockedAttempts >= 2);

    private static void Reset(Item item, PackEntry pack, string gameDirectory)
    {
        // The retry runs against the entry the caller just handed us, not the one it failed
        // against: "mirror is down" is the motivating case for a retry, and by the time the
        // user presses it the shell may well have refreshed the catalog with live mirrors, a
        // corrected size and a corrected digest.
        item.Pack = pack;
        item.GameDirectory = gameDirectory;
        item.State = QueueItemState.Queued;
        item.BytesCompleted = 0;
        item.TotalBytes = 0;
        item.BytesPerSecond = 0;
        item.Eta = null;
        item.CurrentEntry = null;
        item.Error = null;
        item.Warnings = Array.Empty<string>();
        item.BlockedAttempts = 0;
        item.BlockedAtCount = 0;
        item.PausedOut = false;
        item.LastProgressTicks = 0;
    }

    /// <summary>
    /// Builds and writes under one lock acquisition. Building inside and writing outside lets
    /// two threads (a progress sink and the loop) each build a consistent snapshot and then
    /// write them transposed, so a consumer that replaces its state wholesale sees a newer
    /// state followed by an older one. TryWrite on an unbounded channel never blocks, so
    /// holding the lock across it costs nothing, and it must not throw back into the loop once
    /// the channel is complete.
    /// </summary>
    /// </summary>
    private void Publish()
    {
        lock (_gate)
            _updates.Writer.TryWrite(
                new QueueUpdate(_state, _items.Select(i => i.ToSnapshot()).ToArray()));
    }

    /// <summary>
    /// The interval check must be the FIRST thing this does, and must short-circuit before any
    /// snapshot is built. With 8 workers delivering once per ReadAsync return, the reporting
    /// worker can be pinned inside ProgressTracker.Deliver's drain loop for an unbounded number
    /// of *sibling* deposits rather than the single handler invocation it looks like. That is not
    /// a correctness problem, since ReadTimeout bounds the read and not the gap before it, but
    /// this only mitigates it if the cheap TickCount64 comparison happens before the expensive
    /// work.
    /// </summary>
    private void PublishProgress(Item item)
    {
        if (_options.ProgressInterval > TimeSpan.Zero)
        {
            var now = Environment.TickCount64;
            lock (_gate)
            {
                if (now - item.LastProgressTicks < (long)_options.ProgressInterval.TotalMilliseconds) return;
                item.LastProgressTicks = now;
            }
        }

        Publish();
    }

    private sealed class PhaseSink(PackQueue queue, Item item) : IProgress<PackPhase>
    {
        public void Report(PackPhase value)
        {
            lock (queue._gate)
                item.State = value switch
                {
                    PackPhase.Verifying => QueueItemState.Verifying,
                    PackPhase.Downloading => QueueItemState.Downloading,
                    _ => QueueItemState.Installing,
                };

            queue.Publish();   // a transition is never rate-limited
        }
    }

    private sealed class DownloadSink(PackQueue queue, Item item) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value)
        {
            lock (queue._gate)
            {
                item.BytesCompleted = value.BytesCompleted;
                item.TotalBytes = value.TotalBytes;
                item.BytesPerSecond = value.BytesPerSecond;
                item.Eta = value.Eta;
            }

            queue.PublishProgress(item);
        }
    }

    private sealed class InstallSink(PackQueue queue, Item item) : IProgress<InstallProgress>
    {
        public void Report(InstallProgress value)
        {
            lock (queue._gate)
            {
                item.BytesCompleted = value.BytesWritten;
                item.TotalBytes = value.TotalBytes;
                item.CurrentEntry = value.CurrentEntry;
            }

            queue.PublishProgress(item);
        }
    }

    /// <summary>
    /// Stops the queue and waits for the item in flight to actually stop before letting go of its
    /// pack lock. Releasing on Cancel alone would not be true to that: cancellation is not
    /// synchronous, and ZipInstaller observes its token once per entry, so a multi-gigabyte entry
    /// keeps writing well after Cancel returns. A second instance of the application that
    /// acquired the lock in that window would extract into the same game directory, which is the
    /// collision the lock exists to prevent.
    /// </summary>
    ///
    /// Safe to call more than once, before the queue has run, and while it is running. It never
    /// completes the channel once the queue has run: <see cref="RunAsync"/>'s finally owns that on
    /// every exit path, so the two cannot race and hand each other a ChannelClosedException.
    ///
    /// A queue that has been run must eventually get here. <see cref="RunAsync"/> leaves its linked
    /// cancellation source alive on purpose, and this is the only thing that releases it.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            // Required rather than tidiness: _loop is never nulled, so a second pass would
            // Cancel() the very source the first pass disposed below, and Cancel() throws on a
            // disposed source.
            //
            // The await makes this the same promise the first caller got. Sequential
            // double-dispose is harmless either way, but two threads disposing at once (a shell
            // closing a window while the app-exit path runs) would otherwise tell the loser
            // "disposal is done" while the runner is still mid-entry and the pack lock is still
            // held. Guarded on _started because a first caller that took the never-run branch
            // leaves _finished uncompleted forever, and an unguarded await would hang on it.
            if (Volatile.Read(ref _started) == 1) await _finished.Task;
            return;
        }

        // Not merely _completed: a queue paused with a Queued item parks its loop on the signal
        // forever, which is why Complete() clears the pause, and disposal inherits that. It matters
        // on the degraded path below, where _loop is null and there is no cancellation to fall back
        // on. Return value ignored on purpose; see StopAcceptingWork.
        StopAcceptingWork();

        // Read before _started, and the same reference is what gets disposed at the end: reading it
        // again later could hand back a source a racing RunAsync is still using. A disposal landing
        // between _started being set and the assignment therefore sees null here, which is the
        // intended no-op; _completed plus the permit below still bring the loop down, by draining
        // rather than by cancelling.
        var loop = Volatile.Read(ref _loop);

        loop?.Cancel();
        _signal.Release();

        if (Volatile.Read(ref _started) == 1)
        {
            await _finished.Task;

            // Re-read only now. A disposal that saw null above raced the assignment; the source
            // RunAsync went on to create is ours to release, and its finally has run, so nothing is
            // still reading its token.
            loop = Volatile.Read(ref _loop);
        }
        else
        {
            // Never run, so RunAsync's finally will never close the channel and a consumer already
            // in `await foreach` would wait on it forever.
            _updates.Writer.TryComplete();
        }

        loop?.Dispose();

        // _signal is deliberately NOT disposed. Complete() or any other control arriving after
        // disposal would then throw ObjectDisposedException out of a method whose contract is to be
        // safe at any time, and SemaphoreSlim holds no unmanaged resource unless
        // AvailableWaitHandle is used, which it never is here.
    }
}
