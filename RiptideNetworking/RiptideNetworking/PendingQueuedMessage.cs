// This file is provided under The MIT License as part of RiptideNetworking.
// Copyright (c) not Tom Weiland but me https://github.com/Per6
// For additional information please see the included LICENSE.md file or view it on GitHub:
// https://github.com/RiptideNetworking/Riptide/blob/main/LICENSE.md

using Riptide.Utils;
using System;

namespace Riptide
{
    /// <summary>Represents a currently pending queued sent message whose delivery has not been acknowledged yet.</summary>
    internal class PendingQueuedMessage
    {
        /// <summary>The data of the message.</summary>
        private readonly byte[] data;
		/// <summary>The <see cref="Connection"/> to use to send (and resend) the pending queued message.</summary>
        private readonly Connection connection;
		/// <summary>The SequenceId of the message.</summary>
		internal readonly ushort SequenceId;

		/// <summary>Initializes the message.</summary>
		/// <param name="message">The message to send.</param>
		/// <param name="connection">The <see cref="Connection"/> to use to send (and resend) the pending queued message.</param>
		internal PendingQueuedMessage(Message message, Connection connection) {
			int byteAmount = message.BytesInUse;
			data = new byte[byteAmount];
			Buffer.BlockCopy(message.Data, 0, data, 0, data.Length);
			if(byteAmount >= 1 && data[byteAmount - 1] == 0) throw new Exception("Message data should not be ending with a zero byte!");
			this.connection = connection;
			SequenceId = message.SequenceId;
		}

		/// <summary>Sends the pending queued message.</summary>
		internal void TrySend() => connection.Send(data, data.Length);
	}
}