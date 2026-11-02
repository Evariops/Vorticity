using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;

namespace Vorticity.Cluster.Tcp;

/// <summary>
/// A node of a cluster on the network (spec F, 6): it listens for the coordinator's frames (<see cref="TcpClusterTransport"/>)
/// and runs each part, or each warming, by its worker, a connection's requests one after the other and the connections at
/// once. It supposes a trusted network: it reads every frame it is sent.
/// </summary>
public sealed class TcpClusterServer : IAsyncDisposable
{
    private readonly ClusterWorker _worker;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stopping = new CancellationTokenSource();
    private readonly List<Task> _serving = [];
    private Task? _accepting;

    /// <summary>The server of <paramref name="worker"/>'s parts at <paramref name="endPoint"/>; port 0 for one the system gives.</summary>
    /// <param name="worker">The node's worker, which the server does not dispose.</param>
    /// <param name="endPoint">Where it listens.</param>
    public TcpClusterServer(ClusterWorker worker, IPEndPoint endPoint)
    {
        ArgumentNullException.ThrowIfNull(worker);
        ArgumentNullException.ThrowIfNull(endPoint);
        _worker = worker;
        _listener = new TcpListener(endPoint);
    }

    /// <summary>Where it listens: the port the system gave once started, for port 0.</summary>
    public IPEndPoint EndPoint => (IPEndPoint)_listener.LocalEndpoint;

    /// <summary>Starts listening, and serving the connections it accepts.</summary>
    /// <exception cref="InvalidOperationException">It was started already.</exception>
    /// <exception cref="SocketException">The address is taken, or not this machine's.</exception>
    public void Start()
    {
        if (_accepting is not null)
        {
            throw new InvalidOperationException("The server was started already.");
        }

        _listener.Start();
        _accepting = AcceptAsync(_stopping.Token);
    }

    /// <summary>Stops listening, and closes every connection once the request it reads is cancelled.</summary>
    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        _listener.Dispose();
        if (_accepting is not null)
        {
            // The accept loop this server started, which ends once the listener stops.
#pragma warning disable VSTHRD003
            await _accepting.ConfigureAwait(false);
#pragma warning restore VSTHRD003
        }

        Task[] serving;
        lock (_serving)
        {
            serving = [.. _serving];
        }

        await Task.WhenAll(serving).ConfigureAwait(false);
        _stopping.Dispose();
    }

    private async Task AcceptAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Socket socket;
            try
            {
                socket = await _listener.AcceptSocketAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception stopped) when (cancellationToken.IsCancellationRequested && stopped is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            socket.NoDelay = true;
            Task serving = ServeAsync(socket, cancellationToken);
            lock (_serving)
            {
                _serving.RemoveAll(static task => task.IsCompleted);
                _serving.Add(serving);
            }
        }
    }

    /// <summary>Reads the requests of one connection, each answered before the next is read, until the client closes it.</summary>
    private async Task ServeAsync(Socket socket, CancellationToken cancellationToken)
    {
        NetworkStream stream = new NetworkStream(socket, ownsSocket: true);
        await using (stream.ConfigureAwait(false))
        {
            byte[] head = new byte[TcpFrames.Head];
            try
            {
                while (await stream.ReadAtLeastAsync(head.AsMemory(0, 4), 4, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false) == 4)
                {
                    int length = BinaryPrimitives.ReadInt32LittleEndian(head);
                    if (length < 1 || length > TcpFrames.Longest)
                    {
                        return;
                    }

                    byte[] body = ArrayPool<byte>.Shared.Rent(length);
                    try
                    {
                        await stream.ReadExactlyAsync(body.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
                        await AnswerAsync(stream, head, body.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(body);
                    }
                }
            }
            catch (Exception closed) when (closed is IOException or SocketException or OperationCanceledException or InvalidDataException)
            {
                // The client went away, the server stops, or a frame is not this protocol's: the connection closes.
            }
        }
    }

    /// <summary>
    /// Runs the request <paramref name="body"/> and writes its reply: a part's partial, a warming done, or the error the worker
    /// threw, which the client throws in turn. A request this protocol does not read closes the connection.
    /// </summary>
    private async ValueTask AnswerAsync(NetworkStream stream, byte[] head, ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        FrameReader request = new FrameReader(body);
        ValueTask<ReadOnlyMemory<byte>> answering = request.Byte() switch
        {
            TcpFrames.Run => RunAsync(TcpFrames.ReadRun(ref request), cancellationToken),
            TcpFrames.Warm => WarmAsync(TcpFrames.ReadWarm(ref request), cancellationToken),
            byte kind => throw new InvalidDataException($"A request of kind {kind}, not this protocol's."),
        };

        ReadOnlyMemory<byte> payload;
        byte status = TcpFrames.Done;
        try
        {
            payload = await answering.ConfigureAwait(false);
        }
        catch (Exception failure) when (!cancellationToken.IsCancellationRequested)
        {
            // The question's own error: the client throws it, and the part is not run elsewhere.
            payload = TcpFrames.FailedPayload(failure);
            status = TcpFrames.Failed;
        }

        TcpFrames.WriteHead(head, status, payload.Length);
        await stream.WriteAsync(head.AsMemory(0, TcpFrames.Head), cancellationToken).ConfigureAwait(false);
        if (!payload.IsEmpty)
        {
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The bytes of the partial of <paramref name="task"/>'s part.</summary>
    private async ValueTask<ReadOnlyMemory<byte>> RunAsync(ClusterTask task, CancellationToken cancellationToken) =>
        (await _worker.RunAsync(task, cancellationToken).ConfigureAwait(false)).Bytes;

    /// <summary>Warms the objects this node owns in the version <paramref name="warming"/> names; no bytes to reply.</summary>
    private async ValueTask<ReadOnlyMemory<byte>> WarmAsync((string Location, ulong Version, string[] Nodes) warming, CancellationToken cancellationToken)
    {
        await _worker.WarmAsync(warming.Location, warming.Version, warming.Nodes, cancellationToken).ConfigureAwait(false);
        return ReadOnlyMemory<byte>.Empty;
    }
}
