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
/// How a coordinator reaches its nodes over TCP (spec F, 6): each node by its address, where a <see cref="TcpClusterServer"/>
/// listens; a request a connection, kept for the next once its reply is read. A node not reached, or a connection that
/// breaks, throws <see cref="ClusterTransportException"/>, and the part runs on another node; the node's own error throws
/// <see cref="ClusterPartException"/>.
/// </summary>
public sealed class TcpClusterTransport : IClusterTransport, IAsyncDisposable
{
    /// <summary>The idle connections kept to a node at most, as many as the parts a node reads at once by default, and more.</summary>
    internal const int IdleConnections = 16;

    private readonly Dictionary<string, Node> _nodes = new Dictionary<string, Node>(StringComparer.Ordinal);

    /// <summary>The transport to <paramref name="nodes"/>, each node's name to its address.</summary>
    public TcpClusterTransport(IReadOnlyDictionary<string, EndPoint> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        foreach ((string name, EndPoint endPoint) in nodes)
        {
            ArgumentNullException.ThrowIfNull(endPoint);
            _nodes.Add(name, new Node(name, endPoint));
        }
    }

    /// <inheritdoc/>
    public async ValueTask<AggregationPartial> RunAsync(string node, ClusterTask task, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        byte[] frame = TcpFrames.RunRequest(task, out int length);
        try
        {
            return AggregationPartial.FromBytes(await Of(node).AskAsync(frame.AsMemory(0, length), cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(frame);
        }
    }

    /// <inheritdoc/>
    public async ValueTask WarmAsync(string node, string location, ulong version, IReadOnlyList<string> nodes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(nodes);
        byte[] frame = TcpFrames.WarmRequest(location, version, nodes, out int length);
        try
        {
            await Of(node).AskAsync(frame.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(frame);
        }
    }

    /// <summary>Closes the idle connections; one in use closes when its request ends.</summary>
    public async ValueTask DisposeAsync()
    {
        foreach (Node node in _nodes.Values)
        {
            await node.DisposeAsync().ConfigureAwait(false);
        }
    }

    private Node Of(string node) =>
        _nodes.TryGetValue(node, out Node? known) ? known : throw new ClusterTransportException($"No address is known for the node '{node}'.");

    /// <summary>A node, by its address, and the connections to it kept idle.</summary>
    private sealed class Node(string name, EndPoint endPoint) : IAsyncDisposable
    {
        private readonly Stack<Connection> _idle = new Stack<Connection>();
        private bool _disposed;

        /// <summary>
        /// The payload of the reply to <paramref name="request"/>: on an idle connection, or a new one when none is, or when the
        /// idle one the node closed meanwhile breaks.
        /// </summary>
        /// <exception cref="ClusterTransportException">The node is not reached, or the connection broke.</exception>
        /// <exception cref="ClusterPartException">The node failed the request.</exception>
        internal async ValueTask<byte[]> AskAsync(ReadOnlyMemory<byte> request, CancellationToken cancellationToken)
        {
            for (bool fresh = false; ; fresh = true)
            {
                Connection? connection = fresh ? null : Take();
                bool reused = connection is not null;
                connection ??= await ConnectAsync(cancellationToken).ConfigureAwait(false);
                byte status;
                byte[] payload;
                try
                {
                    (status, payload) = await connection.AskAsync(request, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception broken) when (broken is IOException or SocketException)
                {
                    connection.Dispose();
                    if (reused)
                    {
                        continue;
                    }

                    throw new ClusterTransportException($"The connection to the node '{name}' at {endPoint} broke: {broken.Message}", broken);
                }
                catch
                {
                    // Cancelled amid a request: the connection is in no state to carry another.
                    connection.Dispose();
                    throw;
                }

                Give(connection);
                if (status == TcpFrames.Done)
                {
                    return payload;
                }

                (string type, string message) = TcpFrames.ReadFailed(payload);
                throw new ClusterPartException($"The node '{name}' failed the request: {type}: {message}") { Node = name, ErrorType = type };
            }
        }

        public ValueTask DisposeAsync()
        {
            lock (_idle)
            {
                _disposed = true;
                while (_idle.TryPop(out Connection? idle))
                {
                    idle.Dispose();
                }
            }

            return ValueTask.CompletedTask;
        }

        private async ValueTask<Connection> ConnectAsync(CancellationToken cancellationToken)
        {
            Socket socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(endPoint, cancellationToken).ConfigureAwait(false);
                return new Connection(socket);
            }
            catch (SocketException unreached)
            {
                socket.Dispose();
                throw new ClusterTransportException($"The node '{name}' at {endPoint} is not reached: {unreached.Message}", unreached);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        private Connection? Take()
        {
            lock (_idle)
            {
                return _idle.TryPop(out Connection? idle) ? idle : null;
            }
        }

        private void Give(Connection connection)
        {
            lock (_idle)
            {
                if (!_disposed && _idle.Count < IdleConnections)
                {
                    _idle.Push(connection);
                    return;
                }
            }

            connection.Dispose();
        }
    }

    /// <summary>A connection to a node, which carries one request at a time.</summary>
    private sealed class Connection(Socket socket) : IDisposable
    {
        private readonly NetworkStream _stream = new NetworkStream(socket, ownsSocket: true);
        private readonly byte[] _head = new byte[TcpFrames.Head];

        /// <summary>Sends <paramref name="request"/>, and reads its reply's status and payload.</summary>
        /// <exception cref="IOException">The connection broke, or the reply is not this protocol's.</exception>
        internal async ValueTask<(byte Status, byte[] Payload)> AskAsync(ReadOnlyMemory<byte> request, CancellationToken cancellationToken)
        {
            await _stream.WriteAsync(request, cancellationToken).ConfigureAwait(false);
            await _stream.ReadExactlyAsync(_head, cancellationToken).ConfigureAwait(false);
            int length = BinaryPrimitives.ReadInt32LittleEndian(_head);
            if (length < 1 || length > TcpFrames.Longest)
            {
                throw new IOException($"A reply of {length} bytes, not this protocol's.");
            }

            byte[] payload = length == 1 ? [] : new byte[length - 1];
            await _stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
            return (_head[4], payload);
        }

        public void Dispose() => _stream.Dispose();
    }
}
