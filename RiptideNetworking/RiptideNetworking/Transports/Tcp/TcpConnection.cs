// This file is provided under The MIT License as part of RiptideNetworking.
// Copyright (c) Tom Weiland
// For additional information please see the included LICENSE.md file or view it on GitHub:
// https://github.com/RiptideNetworking/Riptide/blob/main/LICENSE.md

using Riptide.Utils;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace Riptide.Transports.Tcp
{
    /// <summary>Represents a connection to a <see cref="TcpServer"/> or <see cref="TcpClient"/>.</summary>
    public class TcpConnection : Connection, IEquatable<TcpConnection>
    {
        /// <summary>The endpoint representing the other end of the connection.</summary>
        public readonly IPEndPoint RemoteEndPoint;

        /// <summary>Whether or not the server has received a connection attempt from this connection.</summary>
        internal bool DidReceiveConnect;

        /// <summary>The socket to use for sending and receiving.</summary>
        private readonly Socket socket;
        /// <summary>The local peer this connection is associated with.</summary>
        private readonly TcpPeer peer;
        /// <summary>An array to receive message size values into.</summary>
        private readonly byte[] sizeBytes = new byte[sizeof(int)];
        /// <summary>The size of the next message to be received.</summary>
        private int nextMessageSize;

        /// <summary>Initializes the connection.</summary>
        /// <param name="socket">The socket to use for sending and receiving.</param>
        /// <param name="remoteEndPoint">The endpoint representing the other end of the connection.</param>
        /// <param name="peer">The local peer this connection is associated with.</param>
        internal TcpConnection(Socket socket, IPEndPoint remoteEndPoint, TcpPeer peer)
        {
            RemoteEndPoint = remoteEndPoint;
            this.socket = socket;
            this.peer = peer;
        }

        /// <inheritdoc/>
        protected internal override void Send(byte[] dataBuffer, int amount)
        {
            try
            {
                if (socket.Connected)
                {
					byte[] amountBytes = BitConverter.GetBytes(amount);
					socket.Send(amountBytes, sizeof(int), SocketFlags.None);
                    socket.Send(dataBuffer, amount, SocketFlags.None);
                }
            }
            catch (SocketException)
            {
                // May want to consider triggering a disconnect here (perhaps depending on the type
                // of SocketException)? Timeout should catch disconnections, but disconnecting
                // explicitly might be better...
            }
        }

        /// <summary>Polls the socket and checks if any data was received.</summary>
        internal void Receive()
        {
            while (TryRecieve(ref nextMessageSize))
			{
				peer.OnDataReceived(nextMessageSize, this);
				nextMessageSize = 0;
            }
        }

        private bool TryRecieve(ref int nextMessageSize) {
			try
			{
				if (nextMessageSize == 0 && socket.Available >= sizeof(int))
				{
					// We have enough bytes for a complete size value
					socket.Receive(sizeBytes, sizeof(int), SocketFlags.None);
					nextMessageSize = Converter.ToInt(sizeBytes, 0);
					if(nextMessageSize == 0) return true;
				}
				if(nextMessageSize == 0 || socket.Available < nextMessageSize) return false;
				socket.Receive(Peer.ReceiveBuffer, nextMessageSize, SocketFlags.None);
				return true;
			}
			catch (SocketException ex)
			{
				switch (ex.SocketErrorCode)
				{
					case SocketError.Interrupted:
					case SocketError.NotSocket:
						peer.OnDisconnected(this, DisconnectReason.TransportError);
						break;
					case SocketError.ConnectionReset:
						peer.OnDisconnected(this, DisconnectReason.Disconnected);
						break;
					case SocketError.TimedOut:
						peer.OnDisconnected(this, DisconnectReason.TimedOut);
						break;
					case SocketError.MessageSize:
						break;
					default:
						break;
				}
				return false;
			}
			catch (ObjectDisposedException)
			{
				peer.OnDisconnected(this, DisconnectReason.TransportError);
				return false;
			}
			catch (NullReferenceException)
			{
				peer.OnDisconnected(this, DisconnectReason.TransportError);
				return false;
			}
		}

        /// <summary>Closes the connection.</summary>
        internal void Close()
        {
            socket.Close();
        }

        /// <inheritdoc/>
        public override string ToString() => RemoteEndPoint.ToStringBasedOnIPFormat();

        /// <inheritdoc/>
        public override bool Equals(object obj) => Equals(obj as TcpConnection);
        /// <inheritdoc/>
        public bool Equals(TcpConnection other)
        {
            if (other is null)
                return false;

            if (ReferenceEquals(this, other))
                return true;

            return RemoteEndPoint.Equals(other.RemoteEndPoint);
        }

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            return -288961498 + EqualityComparer<IPEndPoint>.Default.GetHashCode(RemoteEndPoint);
        }

#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
        public static bool operator ==(TcpConnection left, TcpConnection right)
#pragma warning restore CS1591 // Missing XML comment for publicly visible type or member
        {
            if (left is null)
            {
                if (right is null)
                    return true;

                return false; // Only the left side is null
            }

            // Equals handles case of null on right side
            return left.Equals(right);
        }

#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
        public static bool operator !=(TcpConnection left, TcpConnection right) => !(left == right);
#pragma warning restore CS1591 // Missing XML comment for publicly visible type or member
    }
}
