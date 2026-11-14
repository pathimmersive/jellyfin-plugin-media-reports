using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaReports.Storage;

/// <summary>
/// Runs work one item at a time, in order, off the caller's thread. It holds at most a set
/// number of waiting items and drops the oldest when full, with one warning per backlog. A
/// failure is logged and never reaches the caller; while the database is unavailable that is
/// logged once, not per item.
/// </summary>
public sealed class WorkQueue
{
    private readonly int _capacity;
    private readonly string _what;
    private readonly ILogger _logger;
    private readonly Queue<Action> _waiting = new();
    private readonly object _lock = new();
    private Task _runner = Task.CompletedTask;
    private bool _running;
    private bool _warnedFull;
    private bool _unavailableLogged;

    /// <summary>
    /// Initializes a new instance of the <see cref="WorkQueue"/> class.
    /// </summary>
    /// <param name="capacity">The most items waiting at once.</param>
    /// <param name="what">What the work is, for log lines ("library changes").</param>
    /// <param name="logger">The logger.</param>
    public WorkQueue(int capacity, string what, ILogger logger)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
        _what = what;
        _logger = logger;
    }

    /// <summary>
    /// Gets how many items have been dropped because the queue was full.
    /// </summary>
    public int Dropped { get; private set; }

    /// <summary>
    /// Gets a task that completes when the work queued so far has run.
    /// </summary>
    public Task WhenIdle
    {
        get
        {
            lock (_lock)
            {
                return _runner;
            }
        }
    }

    /// <summary>
    /// Queues work, dropping the oldest waiting item when the queue is full.
    /// </summary>
    /// <param name="work">The work.</param>
    public void Enqueue(Action work)
    {
        lock (_lock)
        {
            if (_waiting.Count >= _capacity)
            {
                _waiting.Dequeue();
                Dropped++;
                if (!_warnedFull)
                {
                    _warnedFull = true;
                    _logger.LogWarning("Media Reports is behind on {What}: more than {Capacity} are waiting, so the oldest are dropped", _what, _capacity);
                }
            }

            _waiting.Enqueue(work);
            if (!_running)
            {
                _running = true;
                _runner = Task.Run(Drain);
            }
        }
    }

    private void Drain()
    {
        while (true)
        {
            Action work;
            lock (_lock)
            {
                if (_waiting.Count == 0)
                {
                    _running = false;
                    _warnedFull = false;
                    return;
                }

                work = _waiting.Dequeue();
            }

            try
            {
                work();
                _unavailableLogged = false;
            }
            catch (StorageUnavailableException ex)
            {
                if (!_unavailableLogged)
                {
                    _unavailableLogged = true;
                    _logger.LogError(ex, "Media Reports can't handle {What} while its database is unavailable; this is logged once until it works again", _what);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Media Reports couldn't handle one of its {What}", _what);
            }
        }
    }
}
