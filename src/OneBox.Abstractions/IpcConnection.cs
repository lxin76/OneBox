using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text.Json;

namespace OneBox.Abstractions;

/// <summary>
/// 长度前缀 + UTF-8 JSON 的帧协议连接。宿主与插件共用同一实现：
/// 双方都可以主动发请求、应答，或推送无需应答的通知。
/// </summary>
/// <remarks>
/// 帧格式：4 字节小端长度 + JSON 文本。所有写入串行化，接收由独立泵循环驱动。
/// </remarks>
public sealed class IpcConnection : IAsyncDisposable
{
    /// <summary>单帧上限，防止异常输入导致内存耗尽。</summary>
    private const int MaxFrameBytes = 16 * 1024 * 1024;

    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<IpcEnvelope>> _pending = new();
    private readonly CancellationTokenSource _cts = new();

    private Task? _pump;
    private int _disposed;

    public IpcConnection(Stream stream) => _stream = stream;

    /// <summary>收到非应答类消息（请求或单向通知）。</summary>
    public event Action<IpcEnvelope>? MessageReceived;

    /// <summary>连接结束（对端关闭或出错）；参数为异常，正常关闭为 null。</summary>
    public event Action<Exception?>? Closed;

    /// <summary>启动接收泵。必须在收发消息前调用一次。</summary>
    public void Start() => _pump ??= Task.Run(() => PumpAsync(_cts.Token));

    /// <summary>发送一条消息（不等待应答）。</summary>
    public async Task SendAsync(IpcEnvelope envelope, CancellationToken ct = default)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, Protocol.Json);
        if (bytes.Length > MaxFrameBytes)
            throw new InvalidOperationException($"帧过大: {bytes.Length} 字节");

        var frame = new byte[4 + bytes.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, bytes.Length);
        bytes.CopyTo(frame, 4);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(frame, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>发送请求并等待应答；超时或对端关闭时抛出。</summary>
    public async Task<IpcEnvelope> RequestAsync(IpcEnvelope envelope, TimeSpan timeout, CancellationToken ct = default)
    {
        var requestId = Guid.NewGuid().ToString("N");
        envelope.RequestId = requestId;

        var tcs = new TaskCompletionSource<IpcEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(requestId, tcs))
            throw new InvalidOperationException("请求 id 冲突");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await SendAsync(envelope, ct).ConfigureAwait(false);
            return await tcs.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"等待应答超时（{envelope.Type}，{timeout.TotalSeconds:0.#} 秒）");
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
        }
    }

    /// <summary>应答一个请求。</summary>
    public Task RespondAsync(string? requestId, bool ok, string? error = null, object? data = null)
    {
        var payload = new ResultPayload
        {
            Ok = ok,
            Error = error,
            Data = data is null ? null : JsonSerializer.SerializeToElement(data, Protocol.Json),
        };
        return SendAsync(IpcEnvelope.Create(ok ? IpcTypes.Ok : IpcTypes.Error, payload, requestId));
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        Exception? failure = null;
        try
        {
            var header = new byte[4];
            while (!ct.IsCancellationRequested)
            {
                if (!await ReadExactAsync(header, ct).ConfigureAwait(false)) break; // 对端正常关闭

                int length = BinaryPrimitives.ReadInt32LittleEndian(header);
                if (length is < 0 or > MaxFrameBytes)
                    throw new InvalidDataException($"非法的帧长度: {length}");

                var buffer = new byte[length];
                if (!await ReadExactAsync(buffer, ct).ConfigureAwait(false))
                    throw new EndOfStreamException("帧体不完整");

                IpcEnvelope? envelope;
                try
                {
                    envelope = JsonSerializer.Deserialize<IpcEnvelope>(buffer, Protocol.Json);
                }
                catch (JsonException ex)
                {
                    throw new InvalidDataException($"消息解析失败: {ex.Message}", ex);
                }

                if (envelope is null) continue;

                // 应答类消息先尝试配对到等待中的请求
                if (envelope.RequestId is { Length: > 0 } id
                    && (envelope.Type == IpcTypes.Ok || envelope.Type == IpcTypes.Error)
                    && _pending.TryRemove(id, out var tcs))
                {
                    if (envelope.Type == IpcTypes.Ok) tcs.TrySetResult(envelope);
                    else tcs.TrySetException(new PluginRequestException(
                        envelope.GetPayload<ResultPayload>()?.Error ?? "插件拒绝了请求"));
                    continue;
                }

                try
                {
                    MessageReceived?.Invoke(envelope);
                }
                catch (Exception ex)
                {
                    // 订阅者异常不能拖垮接收泵
                    failure = ex;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 主动释放
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            // 连接结束：唤醒所有等待中的请求，避免调用方永久挂起
            var pendingException = failure ?? new IOException("连接已关闭");
            foreach (var key in _pending.Keys)
            {
                if (_pending.TryRemove(key, out var tcs))
                    tcs.TrySetException(pendingException);
            }
            Closed?.Invoke(failure);
        }
    }

    private async Task<bool> ReadExactAsync(byte[] buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await _stream.ReadAsync(buffer.AsMemory(offset), ct).ConfigureAwait(false);
            if (read == 0) return offset == 0 ? false : throw new EndOfStreamException("帧体不完整");
            offset += read;
        }
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            if (_pump is not null) await _pump.ConfigureAwait(false);
        }
        catch
        {
            // 释放期间的异常无需上报
        }

        _cts.Dispose();
        _writeLock.Dispose();
        await _stream.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>对端以 Error 应答请求时抛出的异常。</summary>
public sealed class PluginRequestException(string message) : Exception(message);
