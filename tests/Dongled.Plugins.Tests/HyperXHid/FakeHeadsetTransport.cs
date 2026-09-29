using Dongled.Plugin.HyperXHid;

namespace Dongled.Plugins.Tests.HyperXHid;

/// <summary>A dongle that is whatever the test says it is.</summary>
/// <remarks>
/// Past the end of its script the dongle is simply absent, so a provider that keeps looping does
/// something harmless rather than replaying the last entry.
/// </remarks>
internal sealed class FakeHeadsetTransport : IHeadsetTransport
{
    private readonly Queue<Func<IHeadsetSession?>> _script = new();
    private readonly List<ScriptedSession> _sessions = [];
    private readonly object _gate = new();
    private int _openAttempts;

    /// <summary>How many times the provider has tried to open a session.</summary>
    public int OpenAttempts
    {
        get
        {
            lock (_gate)
            {
                return _openAttempts;
            }
        }
    }

    /// <summary>Every session this transport handed out, in order.</summary>
    public IReadOnlyList<ScriptedSession> Sessions
    {
        get
        {
            lock (_gate)
            {
                return [.. _sessions];
            }
        }
    }

    /// <summary>The next open finds no dongle.</summary>
    public FakeHeadsetTransport ThenNoDongle()
    {
        lock (_gate)
        {
            _script.Enqueue(static () => null);
        }

        return this;
    }

    /// <summary>The next open throws, standing in for a device that vanished mid-scan.</summary>
    public FakeHeadsetTransport ThenThrows(Exception exception)
    {
        lock (_gate)
        {
            _script.Enqueue(() => throw exception);
        }

        return this;
    }

    /// <summary>
    /// The next open succeeds. <paramref name="answersBatteryProbe"/> decides whether the headset
    /// replies to the liveness probe, and <paramref name="reports"/> are delivered in order after it.
    /// </summary>
    public FakeHeadsetTransport ThenSession(bool answersBatteryProbe, params byte[][] reports)
    {
        lock (_gate)
        {
            _script.Enqueue(() =>
            {
                var session = new ScriptedSession(answersBatteryProbe, reports);
                lock (_gate)
                {
                    _sessions.Add(session);
                }

                return session;
            });
        }

        return this;
    }

    /// <summary>
    /// The next open succeeds and answers the battery probe with <paramref name="percentage"/>. Sugar
    /// over <see cref="ThenSession"/> for the tests that only care about what the reply carries.
    /// </summary>
    public FakeHeadsetTransport AnswerBatteryRequestsWith(int percentage)
    {
        lock (_gate)
        {
            _script.Enqueue(() =>
            {
                var session = new ScriptedSession(answersBatteryProbe: true, reports: [], (byte)percentage);
                lock (_gate)
                {
                    _sessions.Add(session);
                }

                return session;
            });
        }

        return this;
    }

    /// <summary>
    /// The next open succeeds and answers battery requests with <paramref name="percentages"/>, one
    /// per request and in order, going silent once they run out. <paramref name="reports"/> are
    /// delivered between the answers exactly as <see cref="ThenSession"/> delivers them.
    /// </summary>
    /// <remarks>
    /// The difference from <see cref="ThenSession"/> and <see cref="AnswerBatteryRequestsWith"/>,
    /// which both answer every request with the same figure forever, is that this can express a level
    /// that <em>changed</em> during one session, and a headset that <em>stopped</em> answering during
    /// one session. Neither is expressible against a transport that only ever repeats itself, and a
    /// provider that polled once per session and never again would pass against one.
    /// </remarks>
    public FakeHeadsetTransport ThenSessionAnswering(int[] percentages, params byte[][] reports)
    {
        ArgumentNullException.ThrowIfNull(percentages);

        lock (_gate)
        {
            _script.Enqueue(() =>
            {
                var session = new ScriptedSession(
                    answersBatteryProbe: false, reports, batteryAnswers: percentages);
                lock (_gate)
                {
                    _sessions.Add(session);
                }

                return session;
            });
        }

        return this;
    }

    /// <summary>
    /// The next open succeeds like <see cref="ThenSession"/>, except that once
    /// <paramref name="reports"/> is exhausted the session's stream ends -- <c>TryRead</c> returns
    /// false, the way a real dongle's stream does when it or the headset goes away mid-session. Without
    /// this, a scripted session never ends on its own, so the provider can never reopen a second one:
    /// this is what makes multi-session (disconnect-then-reconnect) scenarios reachable at all.
    /// </summary>
    public FakeHeadsetTransport ThenSessionThatCloses(bool answersBatteryProbe, params byte[][] reports)
    {
        lock (_gate)
        {
            _script.Enqueue(() =>
            {
                var session = new ScriptedSession(answersBatteryProbe, reports, closesAfterReports: true);
                lock (_gate)
                {
                    _sessions.Add(session);
                }

                return session;
            });
        }

        return this;
    }

    /// <summary>
    /// The next open succeeds like <see cref="ThenSession"/>, except that once
    /// <paramref name="reports"/> is exhausted every further read throws <paramref name="exception"/> --
    /// a session that failed mid-read, as opposed to <see cref="ThenSessionThatCloses"/>'s stream that
    /// merely ended, or <see cref="ThenThrows"/>'s open that never succeeded at all.
    /// </summary>
    public FakeHeadsetTransport ThenSessionThatThrowsAfter(
        bool answersBatteryProbe, Exception exception, params byte[][] reports)
    {
        lock (_gate)
        {
            _script.Enqueue(() =>
            {
                var session = new ScriptedSession(answersBatteryProbe, reports, throwsAfterReports: exception);
                lock (_gate)
                {
                    _sessions.Add(session);
                }

                return session;
            });
        }

        return this;
    }

    /// <inheritdoc />
    public IHeadsetSession? TryOpen()
    {
        Func<IHeadsetSession?> next;
        lock (_gate)
        {
            _openAttempts++;

            if (_script.Count == 0)
            {
                return null;
            }

            next = _script.Dequeue();
        }

        return next();
    }

    internal sealed class ScriptedSession(
        bool answersBatteryProbe,
        byte[][] reports,
        byte batteryPercentage = 72,
        bool closesAfterReports = false,
        Exception? throwsAfterReports = null,
        int[]? batteryAnswers = null)
        : IHeadsetSession
    {
        private readonly Queue<byte[]> _reports = new(reports);
        private readonly Queue<int>? _batteryAnswers = batteryAnswers is null ? null : new Queue<int>(batteryAnswers);
        private readonly object _gate = new();
        private bool _batteryRequested;
        private bool _disposed;

        public int InputReportLength => 64;

        public int OutputReportLength => 64;

        public bool Disposed
        {
            get
            {
                lock (_gate)
                {
                    return _disposed;
                }
            }
        }

        public void Write(ReadOnlySpan<byte> report)
        {
            if (!HeadsetProtocol.IsBatteryRequest(report))
            {
                return;
            }

            lock (_gate)
            {
                _batteryRequested = true;
            }
        }

        public bool TryRead(Span<byte> buffer, TimeSpan timeout, out int count)
        {
            lock (_gate)
            {
                if (_batteryRequested && TryTakeBatteryAnswer(out var percentage))
                {
                    _batteryRequested = false;
                    var reply = new byte[InputReportLength];
                    reply[0] = HeadsetProtocol.BatteryReportId;
                    reply[5] = HeadsetProtocol.BatteryCommand;
                    reply[6] = percentage;
                    reply.CopyTo(buffer);
                    count = reply.Length;
                    return true;
                }

                if (_reports.Count > 0)
                {
                    var report = _reports.Dequeue();
                    report.CopyTo(buffer);
                    count = report.Length;
                    return true;
                }

                if (throwsAfterReports is not null)
                {
                    throw throwsAfterReports;
                }

                if (closesAfterReports)
                {
                    // Mirrors a real dongle's stream closing: no more data is ever coming, and the
                    // caller should stop trying rather than keep polling.
                    count = 0;
                    return false;
                }
            }

            // Nothing left to say. A real dongle in this state times out, and a test that has finished
            // asserting stops the provider, which is what ends the loop. Sleeping the caller's timeout
            // is why the timings are injectable: the provider tests pass milliseconds.
            Thread.Sleep(timeout);
            count = 0;
            return true;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
            }
        }

        /// <summary>
        /// The figure to answer the pending battery request with, or false for a headset that is not
        /// answering. Called under <c>_gate</c>.
        /// </summary>
        private bool TryTakeBatteryAnswer(out byte percentage)
        {
            if (_batteryAnswers is null)
            {
                // The unlimited mode: every request gets the same figure, or none ever does.
                percentage = batteryPercentage;
                return answersBatteryProbe;
            }

            if (_batteryAnswers.Count == 0)
            {
                percentage = 0;
                return false;
            }

            percentage = (byte)_batteryAnswers.Dequeue();
            return true;
        }
    }
}
