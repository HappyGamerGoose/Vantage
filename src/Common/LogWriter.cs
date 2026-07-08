// SPDX-License-Identifier: MIT
// Vantage — Common/LogWriter.cs
//
// Long-lived StreamWriter wrapper that batches writes between explicit
// flushes. Used by CommonUtils to avoid the open / seek / write / close
// syscall storm that File.AppendAllText incurs at 8+ calls per agent step.

using System;
using System.IO;
using System.Text;

namespace Vantage.Common;

internal sealed class LogWriter : IDisposable
{
    private readonly object _gate;
    private readonly int _flushEvery;
    private readonly StreamWriter _writer;
    private int _sinceFlush;

    public bool Disposed { get; private set; }

    public LogWriter(string path, object gate, int flushEvery)
    {
        _gate = gate;
        _flushEvery = flushEvery;
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _writer = new StreamWriter(stream, Encoding.UTF8);
        _writer.AutoFlush = false;
    }

    /// <summary>Append a single line; flushes after N writes.</summary>
    public void WriteLine(string line)
    {
        lock (_gate)
        {
            if (Disposed) return;
            _writer.Write(line);
            _sinceFlush++;
            if (_sinceFlush >= _flushEvery)
            {
                _writer.Flush();
                _sinceFlush = 0;
            }
        }
    }

    /// <summary>Append a block (which may or may not end in \n); flushes after N writes.</summary>
    public void Write(string block)
    {
        lock (_gate)
        {
            if (Disposed) return;
            _writer.Write(block);
            _sinceFlush++;
            if (_sinceFlush >= _flushEvery)
            {
                _writer.Flush();
                _sinceFlush = 0;
            }
        }
    }

    /// <summary>Force a flush + dispose the underlying file handle.</summary>
    public void FlushNow()
    {
        lock (_gate)
        {
            if (Disposed) return;
            try { _writer.Flush(); } catch { }
            try { _writer.Dispose(); } catch { }
            Disposed = true;
        }
    }

    void IDisposable.Dispose() => FlushNow();
}
