using System.IO;

namespace OneBox.Tests;

/// <summary>
/// 双向内存流：写入一端的数据可被另一端读出。
/// 用于在无真实命名管道的情况下测试 IPC 两端（宿主侧与插件侧）的通信契约。
/// </summary>
internal sealed class InMemoryDuplexStream : Stream
{
    private readonly SemaphoreSlim _available = new(0);
    private readonly Queue<byte> _buffer = new();
    private readonly object _sync = new();
    private bool _completed;

    public InMemoryDuplexStream? Peer { get; set; }

    /// <summary>建立一对互相对接的流。</summary>
    public static (InMemoryDuplexStream A, InMemoryDuplexStream B) CreatePair()
    {
        var a = new InMemoryDuplexStream();
        var b = new InMemoryDuplexStream();
        a.Peer = b;
        b.Peer = a;
        return (a, b);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush() { }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            lock (_sync)
            {
                if (_buffer.Count > 0)
                {
                    int n = Math.Min(buffer.Length, _buffer.Count);
                    for (int i = 0; i < n; i++) buffer.Span[i] = _buffer.Dequeue();
                    return n;
                }
                if (_completed) return 0;
            }
            await _available.WaitAsync(cancellationToken);
        }
    }

    public override void Write(byte[] buffer, int offset, int count)
        => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var peer = Peer ?? throw new InvalidOperationException("未连接对端");
        lock (peer._sync)
        {
            foreach (var b in buffer.Span) peer._buffer.Enqueue(b);
        }
        peer._available.Release();
        return ValueTask.CompletedTask;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_sync) _completed = true;
            _available.Release();

            // 真实管道在任一端关闭后，另一端也会读到 EOF；内存流需要显式模拟，
            // 否则对端等待中的读取会永久挂起
            if (Peer is { } peer)
            {
                lock (peer._sync) peer._completed = true;
                peer._available.Release();
            }
        }
        base.Dispose(disposing);
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
