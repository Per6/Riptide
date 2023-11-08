﻿// This file is provided under The MIT License as part of RiptideNetworking.
// Copyright (c) Tom Weiland
// For additional information please see the included LICENSE.md file or view it on GitHub:
// https://github.com/RiptideNetworking/Riptide/blob/main/LICENSE.md

using Riptide.Transports;
using Riptide.Utils;
using System;
using System.Collections.Generic;
using System.Text;

namespace Riptide
{
    /// <summary>The send mode of a <see cref="Message"/>.</summary>
    public enum MessageSendMode : byte
    {
        /// <summary>Unreliable send mode.</summary>
        Unreliable = MessageHeader.Unreliable,
        /// <summary>Reliable send mode.</summary>
        Reliable = MessageHeader.Reliable,
    }

    /// <summary>Provides functionality for converting data to bytes and vice versa.</summary>
    public class Message
    {
        /// <summary>The minimum number of bytes contained in an unreliable message.</summary>
        internal const int MinUnreliableBytes = 1;
        /// <summary>The minimum number of bytes contained in a reliable message.</summary>
        internal const int MinReliableBytes = 3;
        /// <summary>The minimum number of bytes contained in a notify message.</summary>
        internal const int MinNotifyBytes = 6;
        /// <summary>The number of bits in a byte.</summary>
        internal const int BitsPerByte = 8;
        /// <summary>The header size for unreliable messages. Does not count the 2 bytes used for the message ID.</summary>
        /// <remarks>1 byte - header.</remarks>
        internal const int UnreliableHeaderBits = 1 * BitsPerByte;
        /// <summary>The header size for reliable messages. Does not count the 2 bytes used for the message ID.</summary>
        /// <remarks>1 byte - header, 2 bytes - sequence ID.</remarks>
        internal const int ReliableHeaderBits = 3 * BitsPerByte;
        /// <summary>The header size for notify messages.</summary>
        /// <remarks>1 byte - header, 3 bytes - ack, 2 bytes - sequence ID.</remarks>
        internal const int NotifyHeaderBits = 6 * BitsPerByte;
        /// <summary>The maximum number of bits required for a message's header.</summary>
        public const int MaxHeaderSize = NotifyHeaderBits;
        /// <summary>The maximum number of bytes that a message can contain, including the <see cref="MaxHeaderSize"/>.</summary>
        public static int MaxSize { get; private set; } = MaxHeaderSize / BitsPerByte + (MaxHeaderSize % BitsPerByte == 0 ? 0 : 1) + 1225;
        /// <summary>The maximum number of bytes of payload data that a message can contain. This value represents how many bytes can be added to a message <i>on top of</i> the <see cref="MaxHeaderSize"/>.</summary>
        public static int MaxPayloadSize
        {
            get => MaxSize - (MaxHeaderSize / BitsPerByte + (MaxHeaderSize % BitsPerByte == 0 ? 0 : 1));
            set
            {
                if (Peer.ActiveCount > 0)
                    throw new InvalidOperationException($"Changing the '{nameof(MaxPayloadSize)}' is not allowed while a {nameof(Server)} or {nameof(Client)} is running!");
                
                if (value < 0)
                    throw new ArgumentOutOfRangeException(nameof(value), $"'{nameof(MaxPayloadSize)}' cannot be negative!");

                MaxSize = MaxHeaderSize / BitsPerByte + (MaxHeaderSize % BitsPerByte == 0 ? 0 : 1) + value;
                maxBitCount = MaxSize * BitsPerByte;
                TrimPool(); // When ActiveSocketCount is 0, this clears the pool
            }
        }
        /// <summary>The maximum number of bits a message can contain.</summary>
        private static int maxBitCount = MaxSize * BitsPerByte;

        /// <summary>How many messages to add to the pool for each <see cref="Server"/> or <see cref="Client"/> instance that is started.</summary>
        /// <remarks>Changes will not affect <see cref="Server"/> and <see cref="Client"/> instances which are already running until they are restarted.</remarks>
        public static byte InstancesPerPeer { get; set; } = 4;
        /// <summary>A pool of reusable message instances.</summary>
        private static readonly List<Message> pool = new List<Message>(InstancesPerPeer * 2);

        /// <summary>The message's send mode.</summary>
        public MessageSendMode SendMode { get; private set; }
        /// <summary>How many bits have been retrieved from the message.</summary>
        public int ReadBits => readBit;
        /// <summary>How many unretrieved bits remain in the message.</summary>
        public int UnreadBits => writeBit - readBit;
        /// <summary>How many bits have been added to the message.</summary>
        public int WrittenBits => writeBit;
        /// <summary>How many more bits can be added to the message.</summary>
        public int UnwrittenBits => maxBitCount - writeBit;
        /// <summary>How many of this message's bytes are in use. Rounds up to the next byte because only whole bytes can be sent.</summary>
        public int BytesInUse => writeBit / BitsPerByte + (writeBit % BitsPerByte == 0 ? 0 : 1);
        /// <summary>How many bytes have been retrieved from the message.</summary>
        [Obsolete("Use ReadBits instead.")] public int ReadLength => ReadBits / BitsPerByte + (ReadBits % BitsPerByte == 0 ? 0 : 1);
        /// <summary>How many more bytes can be retrieved from the message.</summary>
        [Obsolete("Use UnreadBits instead.")] public int UnreadLength => UnreadBits / BitsPerByte + (UnreadBits % BitsPerByte == 0 ? 0 : 1);
        /// <summary>How many bytes have been added to the message.</summary>
        [Obsolete("Use WrittenBits instead.")] public int WrittenLength => WrittenBits / BitsPerByte + (WrittenBits % BitsPerByte == 0 ? 0 : 1);
        /// <summary>How many more bytes can be added to the message.</summary>
        [Obsolete("Use UnwrittenBits instead.")] public int UnwrittenLength => UnwrittenBits / BitsPerByte + (UnwrittenBits % BitsPerByte == 0 ? 0 : 1);
        /// <summary>The message's data.</summary>
        internal byte[] Bytes { get; private set; }

        /// <summary>The next bit to be read.</summary>
        private int readBit;
        /// <summary>The next bit to be written.</summary>
        private int writeBit;

        /// <summary>Initializes a reusable <see cref="Message"/> instance.</summary>
        /// <param name="maxSize">The maximum amount of bytes the message can contain.</param>
        private Message(int maxSize) => Bytes = new byte[maxSize];

        #region Pooling
        /// <summary>Trims the message pool to a more appropriate size for how many <see cref="Server"/> and/or <see cref="Client"/> instances are currently running.</summary>
        public static void TrimPool()
        {
            if (Peer.ActiveCount == 0)
            {
                // No Servers or Clients are running, empty the list and reset the capacity
                pool.Clear();
                pool.Capacity = InstancesPerPeer * 2; // x2 so there's some buffer room for extra Message instances in the event that more are needed
            }
            else
            {
                // Reset the pool capacity and number of Message instances in the pool to what is appropriate for how many Servers & Clients are active
                int idealInstanceAmount = Peer.ActiveCount * InstancesPerPeer;
                if (pool.Count > idealInstanceAmount)
                {
                    pool.RemoveRange(Peer.ActiveCount * InstancesPerPeer, pool.Count - idealInstanceAmount);
                    pool.Capacity = idealInstanceAmount * 2;
                }
            }
        }

        /// <summary>Gets a completely empty message instance with no header.</summary>
        /// <returns>An empty message instance.</returns>
        public static Message Create()
        {
            return RetrieveFromPool().PrepareForUse();
        }
        /// <summary>Gets a message instance that can be used for sending.</summary>
        /// <param name="sendMode">The mode in which the message should be sent.</param>
        /// <param name="id">The message ID.</param>
        /// <returns>A message instance ready to be sent.</returns>
        public static Message Create(MessageSendMode sendMode, ushort id)
        {
            return RetrieveFromPool().PrepareForUse((MessageHeader)sendMode).AddUShort(id);
        }
        /// <inheritdoc cref="Create(MessageSendMode, ushort)"/>
        /// <remarks>NOTE: <paramref name="id"/> will be cast to a <see cref="ushort"/>. You should ensure that its value never exceeds that of <see cref="ushort.MaxValue"/>, otherwise you'll encounter unexpected behaviour when handling messages.</remarks>
        public static Message Create(MessageSendMode sendMode, Enum id)
        {
            return Create(sendMode, (ushort)(object)id);
        }
        /// <summary>Gets a message instance that can be used for sending.</summary>
        /// <param name="header">The message's header type.</param>
        /// <returns>A message instance ready to be sent.</returns>
        internal static Message Create(MessageHeader header)
        {
            return RetrieveFromPool().PrepareForUse(header);
        }
        /// <summary>Gets a message instance that can be used for receiving/handling.</summary>
        /// <param name="header">The message's header type.</param>
        /// <param name="contentLength">The number of bytes which this message will contain.</param>
        /// <returns>A message instance ready to be populated with received data.</returns>
        internal static Message Create(MessageHeader header, int contentLength)
        {
            return RetrieveFromPool().PrepareForUse(header, contentLength);
        }

        /// <summary>Gets a notify message instance that can be used for sending.</summary>
        /// <returns>A notify message instance ready to be sent.</returns>
        public static Message CreateNotify()
        {
            return RetrieveFromPool().PrepareForUse(MessageHeader.Notify);
        }

        /// <summary>Retrieves a message instance from the pool. If none is available, a new instance is created.</summary>
        /// <returns>A message instance ready to be used for sending or handling.</returns>
        private static Message RetrieveFromPool()
        {
            Message message;
            if (pool.Count > 0)
            {
                message = pool[0];
                pool.RemoveAt(0);
            }
            else
                message = new Message(MaxSize);

            return message;
        }

        /// <summary>Returns the message instance to the internal pool so it can be reused.</summary>
        public void Release()
        {
            if (pool.Count < pool.Capacity)
            {
                // Pool exists and there's room
                if (!pool.Contains(this))
                    pool.Add(this); // Only add it if it's not already in the list, otherwise this method being called twice in a row for whatever reason could cause *serious* issues
            }
        }
        #endregion

        #region Functions
        /// <summary>Prepares the message to be used.</summary>
        /// <returns>The message, ready to be used.</returns>
        private Message PrepareForUse()
        {
            readBit = 0;
            writeBit = 0;
            return this;
        }
        /// <summary>Prepares the message to be used for sending.</summary>
        /// <param name="header">The header of the message.</param>
        /// <returns>The message, ready to be used for sending.</returns>
        private Message PrepareForUse(MessageHeader header)
        {
            SetHeader(header);
            return this;
        }
        /// <summary>Prepares the message to be used for handling.</summary>
        /// <param name="header">The header of the message.</param>
        /// <param name="contentLength">The number of bytes that this message will contain and which can be retrieved.</param>
        /// <returns>The message, ready to be used for handling.</returns>
        private Message PrepareForUse(MessageHeader header, int contentLength)
        {
            SetHeader(header);
            writeBit = contentLength * BitsPerByte;
            return this;
        }

        /// <summary>Sets the message's header bits to the given <paramref name="header"/> and determines the appropriate <see cref="MessageSendMode"/> and read/write positions.</summary>
        /// <param name="header">The header to use for this message.</param>
        private void SetHeader(MessageHeader header)
        {
            Bytes[0] = (byte)header;
            if (header == MessageHeader.Notify)
            {
                readBit = NotifyHeaderBits;
                writeBit = NotifyHeaderBits;
                SendMode = MessageSendMode.Unreliable; // Technically it's different but notify messages *are* still unreliable
            }
            else if (header >= MessageHeader.Reliable)
            {
                readBit = ReliableHeaderBits;
                writeBit = ReliableHeaderBits;
                SendMode = MessageSendMode.Reliable;
            }
            else
            {
                readBit = UnreliableHeaderBits;
                writeBit = UnreliableHeaderBits;
                SendMode = MessageSendMode.Unreliable;
            }
        }
        #endregion

        #region Add & Retrieve Data
        #region Byte & SByte
        /// <summary>Adds a <see cref="byte"/> to the message.</summary>
        /// <param name="value">The <see cref="byte"/> to add.</param>
        /// <returns>The message that the <see cref="byte"/> was added to.</returns>
        public Message AddByte(byte value)
        {
            if (UnwrittenBits < BitsPerByte)
                throw new InsufficientCapacityException(this, ByteName, BitsPerByte);

            Converter.ByteToBits(value, Bytes, writeBit);
            writeBit += BitsPerByte;
            return this;
        }

        /// <summary>Adds an <see cref="sbyte"/> to the message.</summary>
        /// <param name="value">The <see cref="sbyte"/> to add.</param>
        /// <returns>The message that the <see cref="sbyte"/> was added to.</returns>
        public Message AddSByte(sbyte value)
        {
            if (UnwrittenBits < BitsPerByte)
                throw new InsufficientCapacityException(this, SByteName, BitsPerByte);

            Converter.SByteToBits(value, Bytes, writeBit);
            writeBit += BitsPerByte;
            return this;
        }

        /// <summary>Retrieves a <see cref="byte"/> from the message.</summary>
        /// <returns>The <see cref="byte"/> that was retrieved.</returns>
        public byte GetByte()
        {
            if (UnreadBits < BitsPerByte)
            {
                RiptideLogger.Log(LogType.Error, NotEnoughBitsError(ByteName));
                return 0;
            }

            byte value = Converter.ByteFromBits(Bytes, readBit);
            readBit += BitsPerByte;
            return value;
        }

        /// <summary>Retrieves an <see cref="sbyte"/> from the message.</summary>
        /// <returns>The <see cref="sbyte"/> that was retrieved.</returns>
        public sbyte GetSByte()
        {
            if (UnreadBits < BitsPerByte)
            {
                RiptideLogger.Log(LogType.Error, NotEnoughBitsError(SByteName));
                return 0;
            }

            sbyte value = Converter.SByteFromBits(Bytes, readBit);
            readBit += BitsPerByte;
            return value;
        }

        /// <summary>Adds a <see cref="byte"/> array to the message.</summary>
        /// <param name="array">The array to add.</param>
        /// <param name="includeLength">Whether or not to include the length of the array in the message.</param>
        /// <returns>The message that the array was added to.</returns>
        public Message AddBytes(byte[] array, bool includeLength = true)
        {
            if (includeLength)
                AddArrayLength(array.Length);

            if (UnwrittenBits < array.Length * BitsPerByte)
                throw new InsufficientCapacityException(this, array.Length, ByteName, BitsPerByte);

            if (writeBit % BitsPerByte == 0)
            {
                Array.Copy(array, 0, Bytes, writeBit / BitsPerByte, array.Length);
                writeBit += array.Length * BitsPerByte;
            }
            else
            {
                for (int i = 0; i < array.Length; i++)
                {
                    Converter.ByteToBits(array[i], Bytes, writeBit);
                    writeBit += BitsPerByte;
                }
            }
            
            return this;
        }

        /// <summary>Adds an <see cref="sbyte"/> array to the message.</summary>
        /// <param name="array">The array to add.</param>
        /// <param name="includeLength">Whether or not to include the length of the array in the message.</param>
        /// <returns>The message that the array was added to.</returns>
        public Message AddSBytes(sbyte[] array, bool includeLength = true)
        {
            if (includeLength)
                AddArrayLength(array.Length);

            if (UnwrittenBits < array.Length * BitsPerByte)
                throw new InsufficientCapacityException(this, array.Length, SByteName, BitsPerByte);

            for (int i = 0; i < array.Length; i++)
            {
                Converter.SByteToBits(array[i], Bytes, writeBit);
                writeBit += BitsPerByte;
            }
            
            return this;
        }

        /// <summary>Retrieves a <see cref="byte"/> array from the message.</summary>
        /// <returns>The array that was retrieved.</returns>
        public byte[] GetBytes() => GetBytes(GetArrayLength());
        /// <summary>Retrieves a <see cref="byte"/> array from the message.</summary>
        /// <param name="amount">The amount of bytes to retrieve.</param>
        /// <returns>The array that was retrieved.</returns>
        public byte[] GetBytes(int amount)
        {
            byte[] array = new byte[amount];
            ReadBytes(amount, array);
            return array;
        }
        /// <summary>Populates a <see cref="byte"/> array with bytes retrieved from the message.</summary>
        /// <param name="amount">The amount of bytes to retrieve.</param>
        /// <param name="intoArray">The array to populate.</param>
        /// <param name="startIndex">The position at which to start populating the array.</param>
        public void GetBytes(int amount, byte[] intoArray, int startIndex = 0)
        {
            if (startIndex + amount > intoArray.Length)
                throw new ArgumentException(nameof(amount), ArrayNotLongEnoughError(amount, intoArray.Length, startIndex, ByteName));

            ReadBytes(amount, intoArray, startIndex);
        }

        /// <summary>Retrieves an <see cref="sbyte"/> array from the message.</summary>
        /// <returns>The array that was retrieved.</returns>
        public sbyte[] GetSBytes() => GetSBytes(GetArrayLength());
        /// <summary>Retrieves an <see cref="sbyte"/> array from the message.</summary>
        /// <param name="amount">The amount of sbytes to retrieve.</param>
        /// <returns>The array that was retrieved.</returns>
        public sbyte[] GetSBytes(int amount)
        {
            sbyte[] array = new sbyte[amount];
            ReadSBytes(amount, array);
            return array;
        }
        /// <summary>Populates a <see cref="sbyte"/> array with bytes retrieved from the message.</summary>
        /// <param name="amount">The amount of sbytes to retrieve.</param>
        /// <param name="intArray">The array to populate.</param>
        /// <param name="startIndex">The position at which to start populating <paramref name="intArray"/>.</param>
        public void GetSBytes(int amount, sbyte[] intArray, int startIndex = 0)
        {
            if (startIndex + amount > intArray.Length)
                throw new ArgumentException(nameof(amount), ArrayNotLongEnoughError(amount, intArray.Length, startIndex, SByteName));

            ReadSBytes(amount, intArray, startIndex);
        }

        /// <summary>Reads a number of bytes from the message and writes them into the given array.</summary>
        /// <param name="amount">The amount of bytes to read.</param>
        /// <param name="intoArray">The array to write the bytes into.</param>
        /// <param name="startIndex">The position at which to start writing into the array.</param>
        private void ReadBytes(int amount, byte[] intoArray, int startIndex = 0)
        {
            if (UnreadBits < amount * BitsPerByte)
            {
                RiptideLogger.Log(LogType.Error, NotEnoughBitsError(intoArray.Length, ByteName));
                amount = UnreadBits / BitsPerByte;
            }

            if (readBit % BitsPerByte == 0)
            {
                Array.Copy(Bytes, readBit / BitsPerByte, intoArray, startIndex, amount);
                readBit += amount * BitsPerByte;
            }
            else
            {
                for (int i = 0; i < amount; i++)
                {
                    intoArray[startIndex + i] = Converter.ByteFromBits(Bytes, readBit);
                    readBit += BitsPerByte;
                }
            }
        }

        /// <summary>Reads a number of sbytes from the message and writes them into the given array.</summary>
        /// <param name="amount">The amount of sbytes to read.</param>
        /// <param name="intoArray">The array to write the sbytes into.</param>
        /// <param name="startIndex">The position at which to start writing into the array.</param>
        private void ReadSBytes(int amount, sbyte[] intoArray, int startIndex = 0)
        {
            if (UnreadBits < amount * BitsPerByte)
            {
                RiptideLogger.Log(LogType.Error, NotEnoughBitsError(intoArray.Length, SByteName));
                amount = UnreadBits / BitsPerByte;
            }

            for (int i = 0; i < amount; i++)
            {
                intoArray[startIndex + i] = Converter.SByteFromBits(Bytes, readBit);
                readBit += BitsPerByte;
            }
        }
        #endregion

        #region Bool
        /// <summary>Adds a <see cref="bool"/> to the message.</summary>
        /// <param name="value">The <see cref="bool"/> to add.</param>
        /// <returns>The message that the <see cref="bool"/> was added to.</returns>
        public Message AddBool(bool value)
        {
            if (UnwrittenBits < 1)
                throw new InsufficientCapacityException(this, BoolName, 1);

            Converter.BoolToBit(value, Bytes, writeBit++);
            return this;
        }

        /// <summary>Retrieves a <see cref="bool"/> from the message.</summary>
        /// <returns>The <see cref="bool"/> that was retrieved.</returns>
        public bool GetBool()
        {
            if (UnreadBits < 1)
            {
                RiptideLogger.Log(LogType.Error, NotEnoughBitsError(BoolName, "false"));
                return false;
            }

            return Converter.BoolFromBit(Bytes, readBit++);
        }

        /// <summary>Adds a <see cref="bool"/> array to the message.</summary>
        /// <param name="array">The array to add.</param>
        /// <param name="includeLength">Whether or not to include the length of the array in the message.</param>
        /// <returns>The message that the array was added to.</returns>
        public Message AddBools(bool[] array, bool includeLength = true)
        {
            if (includeLength)
                AddArrayLength(array.Length);

            if (UnwrittenBits < array.Length)
                throw new InsufficientCapacityException(this, array.Length, BoolName, 1);

            for (int i = 0; i < array.Length; i++)
                Converter.BoolToBit(array[i], Bytes, writeBit++);

            return this;
        }

        /// <summary>Retrieves a <see cref="bool"/> array from the message.</summary>
        /// <returns>The array that was retrieved.</returns>
        public bool[] GetBools() => GetBools(GetArrayLength());
        /// <summary>Retrieves a <see cref="bool"/> array from the message.</summary>
        /// <param name="amount">The amount of bools to retrieve.</param>
        /// <returns>The array that was retrieved.</returns>
        public bool[] GetBools(int amount)
        {
            bool[] array = new bool[amount];
            ReadBools(amount, array);
            return array;
        }
        /// <summary>Populates a <see cref="bool"/> array with bools retrieved from the message.</summary>
        /// <param name="amount">The amount of bools to retrieve.</param>
        /// <param name="intoArray">The array to populate.</param>
        /// <param name="startIndex">The position at which to start populating the array.</param>
        public void GetBools(int amount, bool[] intoArray, int startIndex = 0)
        {
            if (startIndex + amount > intoArray.Length)
                throw new ArgumentException(nameof(amount), ArrayNotLongEnoughError(amount, intoArray.Length, startIndex, BoolName));

            ReadBools(amount, intoArray, startIndex);
        }

        /// <summary>Reads a number of bools from the message and writes them into the given array.</summary>
        /// <param name="amount">The amount of bools to read.</param>
        /// <param name="intoArray">The array to write the bools into.</param>
        /// <param name="startIndex">The position at which to start writing into the array.</param>
        private void ReadBools(int amount, bool[] intoArray, int startIndex = 0)
        {
            if (UnreadBits < amount)
            {
                RiptideLogger.Log(LogType.Error, NotEnoughBitsError(amount, BoolName));
                amount = UnreadBits;
            }
            
            for (int i = 0; i < amount; i++)
                intoArray[startIndex + i] = Converter.BoolFromBit(Bytes, readBit++);
        }
        #endregion

        #region Short & UShort
        /// <summary>Adds a <see cref="short"/> to the message.</summary>
        /// <param name="value">The <see cref="short"/> to add.</param>
        /// <returns>The message that the <see cref="short"/> was added to.</returns>
        public Message AddShort(short value)
        {
            if (UnwrittenBits < sizeof(short) * BitsPerByte)
                throw new InsufficientCapacityException(this, ShortName, sizeof(short) * BitsPerByte);

            Converter.ShortToBits(value, Bytes, writeBit);
            writeBit += sizeof(short) * BitsPerByte;
            return this;
        }

        /// <summary>Adds a <see cref="ushort"/> to the message.</summary>
        /// <param name="value">The <see cref="ushort"/> to add.</param>
        /// <returns>The message that the <see cref="ushort"/> was added to.</returns>
        public Message AddUShort(ushort value)
        {
            if (UnwrittenBits < sizeof(ushort) * BitsPerByte)
                throw new InsufficientCapacityException(this, UShortName, sizeof(ushort) * BitsPerByte);

            Converter.UShortToBits(value, Bytes, writeBit);
            writeBit += sizeof(ushort) * BitsPerByte;
            return this;
        }

        /// <summary>Retrieves a <see cref="short"/> from the message.</summary>
        /// <returns>The <see cref="short"/> that was retrieved.</returns>
        public short GetShort()
        {
            if (UnreadBits < sizeof(short) * BitsPerByte)
            {
                RiptideLogger.Log(LogType.Error, NotEnoughBitsError(ShortName));
                return 0;
            }

            short value = Converter.ShortFromBits(Bytes, readBit);
            readBit += sizeof(short) * BitsPerByte;
            return value;
        }

        /// <summary>Retrieves a <see cref="ushort"/> from the message.</summary>
        /// <returns>The <see cref="ushort"/> that was retrieved.</returns>
        public ushort GetUShort()
        {
            if (UnreadBits < sizeof(ushort) * BitsPerByte)
            {
                RiptideLogger.Log(LogType.Error, NotEnoughBitsError(UShortName));
                return 0;
            }

            ushort value = Converter.UShortFromBits(Bytes, readBit);
            readBit += sizeof(ushort) * BitsPerByte;
            return value;
        }
        
        /// <summary>Adds a <see cref="short"/> array to the message.</summary>
        /// <param name="array">The array to add.</param>
        /// <param name="includeLength">Whether or not to include the length of the array in the message.</param>
        /// <returns>The message that the array was added to.</returns>
        public Message AddShorts(short[] array, bool includeLength = true)
        {
            if (includeLength)
                AddArrayLength(array.Length);

            if (UnwrittenBits < array.Length * sizeof(short) * BitsPerByte)
                throw new InsufficientCapacityException(this, array.Length, ShortName, sizeof(short) * BitsPerByte);

            for (int i = 0; i < array.Length; i++)
            {
                array[i] = Converter.ShortFromBits(Bytes, readBit);
                readBit += sizeof(short) * BitsPerByte;
            }

            return this;
        }

        /// <summary>Adds a <see cref="ushort"/> array to the message.</summary>
        /// <param name="array">The array to add.</param>
        /// <param name="includeLength">Whether or not to include the length of the array in the message.</param>
        /// <returns>The message that the array was added to.</returns>
        public Message AddUShorts(ushort[] array, bool includeLength = true)
        {
            if (includeLength)
                AddArrayLength(array.Length);

            if (UnwrittenBits < array.Length * sizeof(ushort) * BitsPerByte)
                throw new InsufficientCapacityException(this, array.Length, UShortName, sizeof(ushort) * BitsPerByte);

            for (int i = 0; i < array.Length; i++)
            {
                array[i] = Converter.UShortFromBits(Bytes, readBit);
                readBit += sizeof(ushort) * BitsPerByte;
            }

            return this;
        }

        /// <summary>Retrieves a <see cref="short"/> array from the message.</summary>
        /// <returns>The array that was retrieved.</returns>
        public short[] GetShorts() => GetShorts(GetArrayLength());
        /// <summary>Retrieves a <see cref="short"/> array from the message.</summary>
        /// <param name="amount">The amount of shorts to retrieve.</param>
        /// <returns>The array that was retrieved.</returns>
        public short[] GetShorts(int amount)
        {
            short[] array = new short[amount];
            ReadShorts(amount, array);
            return array;
        }
        /// <summary>Populates a <see cref="short"/> array with shorts retrieved from the message.</summary>
        /// <param name="amount">The amount of shorts to retrieve.</param>
        /// <param name="intoArray">The array to populate.</param>
        /// <param name="startIndex">The position at which to start populating the array.</param>
        public void GetShorts(int amount, short[] intoArray, int startIndex = 0)
        {
            if (startIndex + amount > intoArray.Length)
                throw new ArgumentException(nameof(amount), ArrayNotLongEnoughError(amount, intoArray.Length, startIndex, ShortName));

            ReadShorts(amount, intoArray, startIndex);
        }

        /// <summary>Retrieves a <see cref="ushort"/> array from the message.</summary>
        /// <returns>The array that was retrieved.</returns>
        public ushort[] GetUShorts() => GetUShorts(GetArrayLength());
        /// <summary>Retrieves a <see cref="ushort"/> array from the message.</summary>
        /// <param name="amount">The amount of ushorts to retrieve.</param>
        /// <returns>The array that was retrieved.</returns>
        public ushort[] GetUShorts(int amount)
        {
            ushort[] array = new ushort[amount];
            ReadUShorts(amount, array);
            return array;
        }
        /// <summary>Populates a <see cref="ushort"/> array with ushorts retrieved from the message.</summary>
        /// <param name="amount">The amount of ushorts to retrieve.</param>
        /// <param name="intoArray">The array to populate.</param>
        /// <param name="startIndex">The position at which to start populating the array.</param>
        public void GetUShorts(int amount, ushort[] intoArray, int startIndex = 0)
        {
            if (startIndex + amount > intoArray.Length)
                throw new ArgumentException(nameof(amount), ArrayNotLongEnoughError(amount, intoArray.Length, startIndex, UShortName));

            ReadUShorts(amount, intoArray, startIndex);
        }

        /// <summary>Reads a number of shorts from the message and writes them into the given array.</summary>
        /// <param name="amount">The amount of shorts to read.</param>
        /// <param name="intoArray">The array to write the shorts into.</param>
        /// <param name="startIndex">The position at which to start writing into the array.</param>
        private void ReadShorts(int amount, short[] intoArray, int startIndex = 0)
        {
            if (UnreadBits < amount * sizeof(short) * BitsPerByte)
            {
                RiptideLogger.Log(LogType.Error, NotEnoughBitsError(intoArray.Length, ShortName));
                amount = UnreadBits / (sizeof(short) * BitsPerByte);
            }

            for (int i = 0; i < amount; i++)
            {
                intoArray[startIndex + i] = Converter.ShortFromBits(Bytes, readBit);
                readBit += sizeof(short) * BitsPerByte;
            }
        }

        /// <summary>Reads a number of ushorts from the message and writes them into the given array.</summary>
        /// <param name="amount">The amount of ushorts to read.</param>
        /// <param name="intoArray">The array to write the ushorts into.</param>
        /// <param name="startIndex">The position at which to start writing into the array.</param>
        private void ReadUShorts(int amount, ushort[] intoArray, int startIndex = 0)
        {
            if (UnreadBits < amount * sizeof(ushort) * BitsPerByte)
            {
                RiptideLogger.Log(LogType.Error, NotEnoughBitsError(intoArray.Length, UShortName));
                amount = UnreadBits / (sizeof(ushort) * BitsPerByte);
            }

            for (int i = 0; i < amount; i++)
            {
                intoArray[startIndex + i] = Converter.UShortFromBits(Bytes, readBit);
                readBit += sizeof(ushort) * BitsPerByte;
            }
        }
        #endregion

        #region Int & UInt
        /// <summary>Adds an <see cref="int"/> to the message.</summary>
        /// <param name="value">The <see cref="int"/> to add.</param>
        /// <returns>The message that the <see cref="int"/> was added to.</returns>
        public Message AddInt(int value)
        {
            if (UnwrittenBits < sizeof(int) * BitsPerByte)
                throw new InsufficientCapacityException(this, IntName, sizeof(int) * BitsPerByte);

            Converter.IntToBits(value, Bytes, writeBit);
            writeBit += sizeof(int) * BitsPerByte;
            return this;
        }

        /// <summary>Adds a <see cref="uint"/> to the message.</summary>
        /// <param name="value">The <see cref="uint"/> to add.</param>
        /// <returns>The message that the <see cref="uint"/> was added to.</returns>
        public Message AddUInt(uint value)
        {
            if (UnwrittenBits < sizeof(uint) * BitsPerByte)
                throw new InsufficientCapacityException(this, UIntName, sizeof(uint) * BitsPerByte);

            Converter.UIntToBits(value, Bytes, writeBit);
            writeBit += sizeof(uint) * BitsPerByte;
            return this;
        }

        /// <summary>Retrieves an <see cref="int"/> from the message.</summary>
        /// <returns>The <see cref="int"/> that was retrieved.</returns>
        public int GetInt()
        {
            if (UnreadBits < sizeof(int) * BitsPerByte)
            {
                RiptideLogger.Log(LogType.Error, NotEnoughBitsError(IntName));
                return 0;
            }

            int value = Converter.IntFromBits(Bytes, readBit);
            readBit += sizeof(int) * BitsPerByte;
            return value;
        }

        /// <summary>Retrieves a <see cref="uint"/> from the message.</summary>
        /// <returns>The <see cref="uint"/> that was retrieved.</returns>
        public uint GetUInt()
        {
            if (UnreadBits < sizeof(uint) * BitsPerByte)
            {
                RiptideLogger.Log(LogType.Error, NotEnoughBitsError(UIntName));
                return 0;
            }

            uint value = Converter.UIntFromBits(Bytes, readBit);
            readBit += sizeof(uint) * BitsPerByte;
            return value;
        }

        /// <summary>Adds an <see cref="int"/> array message.</summary>
        /// <param name="array">The array to add.</param>
        /// <param name="includeLength">Whether or not to include the length of the array in the message.</param>
        /// <returns>The message that the array was added to.</returns>
        public Message AddInts(int[] array, bool includeLength = true)
        {
            if (includeLength)
                AddArrayLength(array.Length);

            if (UnwrittenBits < array.Length * sizeof(int) * BitsPerByte)
                throw new InsufficientCapacityException(this, array.Length, IntName, sizeof(int) * BitsPerByte);

            for (int i = 0; i < array.Length; i++)
            {
                Converter.IntToBits(array[i], Bytes, writeBit);
                writeBit += sizeof(int) * BitsPerByte;
            }

            return this;
        }

        /// <summary>Adds a <see cref="uint"/> array to the message.</summary>
        /// <param name="array">The array to add.</param>
        /// <param name="includeLength">Whether or not to include the length of the array in the message.</param>
        /// <returns>The message that the array was added to.</returns>
        public Message AddUInts(uint[] array, bool includeLength = true)
        {
            if (includeLength)
                AddArrayLength(array.Length);

            if (UnwrittenBits < array.Length * sizeof(uint) * BitsPerByte)
                throw new InsufficientCapacityException(this, array.Length, UIntName, sizeof(uint) * BitsPerByte);

            for (int i = 0; i < array.Length; i++)
            {
                Converter.UIntToBits(array[i], Bytes, writeBit);
                writeBit += sizeof(uint) * BitsPerByte;
            }

            return this;
        }

        /// <summary>Retrieves an <see cref="int"/> array from the message.</summary>
        /// <returns>The array that was retrieved.</returns>
        public int[] GetInts() => GetInts(GetArrayLength());
        /// <summary>Retrieves an <see cref="int"/> array from the message.</summary>
        /// <param name="amount">The amount of ints to retrieve.</param>
        /// <returns>The array that was retrieved.</returns>
        public int[] GetInts(int amount)
        {
            int[] array = new int[amount];
            ReadInts(amount, array);
            return array;
        }
        /// <summary>Populates an <see cref="int"/> array with ints retrieved from the message.</summary>
        /// <param name="amount">The amount of ints to retrieve.</param>
        /// <param name="intoArray">The array to populate.</param>
        /// <param name="startIndex">The position at which to start populating the array.</param>
        public void GetInts(int amount, int[] intoArray, int startIndex = 0)
        {
            if (startIndex + amount > intoArray.Length)
                throw new ArgumentException(nameof(amount), ArrayNotLongEnoughError(amount, intoArray.Length, startIndex, IntName));

            ReadInts(amount, intoArray, startIndex);
        }

        /// <summary>Retrieves a <see cref="uint"/> array from the message.</summary>
        /// <returns>The array that was retrieved.</returns>
        public uint[] GetUInts() => GetUInts(GetArrayLength());
        /// <summary>Retrieves a <see cref="uint"/> array from the message.</summary>
        /// <param name="amount">The amount of uints to retrieve.</param>
        /// <returns>The array that was retrieved.</returns>
        public uint[] GetUInts(int amount)
        {
            uint[] array = new uint[amount];
            ReadUInts(amount, array);
            return array;
        }
        /// <summary>Populates a <see cref="uint"/> array with uints retrieved from the message.</summary>
        /// <param name="amount">The amount of uints to retrieve.</param>
        /// <param name="intoArray">The array to populate.</param>
        /// <param name="startIndex">The position at which to start populating the array.</param>
        public void GetUInts(int amount, uint[] intoArray, int startIndex = 0)
        {
            if (startIndex + amount > intoArray.Length)
                throw new ArgumentException(nameof(amount), ArrayNotLongEnoughError(amount, intoArray.Length, startIndex, UIntName));

            ReadUInts(amount, intoArray, startIndex);
        }

        /// <summary>Reads a number of ints from the message and writes them into the given array.</summary>
        /// <param name="amount">The amount of ints to read.</param>
        /// <param name="intoArray">The array to write the ints into.</param>
        /// <param name="startIndex">The position at which to start writing into the array.</param>
        private void ReadInts(int amount, int[] intoArray, int startIndex = 0)
        {
            if (UnreadBits < amount * sizeof(int) * BitsPerByte)
            {
                RiptideLogger.Log(LogType.Error, NotEnoughBitsError(intoArray.Length, IntName));
                amount = UnreadBits / (sizeof(int) * BitsPerByte);
            }

            for (int i = 0; i < amount; i++)
            {
                intoArray[startIndex + i] = Converter.IntFromBits(Bytes, readBit);
                readBit += sizeof(int) * BitsPerByte;
            }
        }

        /// <summary>Reads a number of uints from the message and writes them into the given array.</summary>
        /// <param name="amount">The amount of uints to read.</param>
        /// <param name="intoArray">The array to write the uints into.</param>
        /// <param name="startIndex">The position at which to start writing into the array.</param>
        private void ReadUInts(int amount, uint[] intoArray, int startIndex = 0)
        {
            if (UnreadBits < amount * sizeof(uint) * BitsPerByte)
            {
                RiptideLogger.Log(LogType.Error, NotEnoughBitsError(intoArray.Length, UIntName));
                amount = UnreadBits / (sizeof(uint) * BitsPerByte);
            }

            for (int i = 0; i < amount; i++)
            {
                intoArray[startIndex + i] = Converter.UIntFromBits(Bytes, readBit);
                readBit += sizeof(uint) * BitsPerByte;
            }
        }
        #endregion

        #region Long & ULong
        /// <summary>Adds a <see cref="long"/> to the message.</summary>
        /// <param name="value">The <see cref="long"/> to add.</param>
        /// <returns>The message that the <see cref="long"/> was added to.</returns>
        public Message AddLong(long value)
        {
            if (UnwrittenBits < sizeof(long) * BitsPerByte)
                throw new InsufficientCapacityException(this, LongName, sizeof(long) * BitsPerByte);

            Converter.LongToBits(value, Bytes, writeBit);
            writeBit += sizeof(long) * BitsPerByte;
            return this;
        }

        /// <summary>Adds a <see cref="ulong"/> to the message.</summary>
        /// <param name="value">The <see cref="ulong"/> to add.</param>
        /// <returns>The message that the <see cref="ulong"/> was added to.</returns>
        public Message AddULong(ulong value)
        {
            if (UnwrittenBits < sizeof(ulong) * BitsPerByte)
                throw new InsufficientCapacityException(this, ULongName, sizeof(ulong) * BitsPerByte);

            Converter.ULongToBits(value, Bytes, writeBit);
            writeBit += sizeof(ulong) * BitsPerByte;
            return this;
        }

        /// <summary>Retrieves a <see cref="long"/> from the message.</summary>
        /// <returns>The <see cref="long"/> that was retrieved.</returns>
        public long GetLong()
        {
            if (UnreadBits < sizeof(long) * BitsPerByte)
            {
                RiptideLogger.Log(LogType.Error, NotEnoughBitsError(LongName));
                return 0;
            }

            long value = Converter.LongFromBits(Bytes, readBit);
            readBit += sizeof(long) * BitsPerByte;
            return value;
        }

        /// <summary>Retrieves a <see cref="ulong"/> from the message.</summary>
        /// <returns>The <see cref="ulong"/> that was retrieved.</returns>
        public ulong GetULong()
        {
            if (UnreadBits < sizeof(ulong) * BitsPerByte)
            {
                RiptideLogger.Log(LogType.Error, NotEnoughBitsError(ULongName));
                return 0;
            }

            ulong value = Converter.ULongFromBits(Bytes, readBit);
            readBit += sizeof(ulong) * BitsPerByte;
            return value;
        }

        /// <summary>Adds a <see cref="long"/> array to the message.</summary>
        /// <param name="array">The array to add.</param>
        /// <param name="includeLength">Whether or not to include the length of the array in the message.</param>
        /// <returns>The message that the array was added to.</returns>
        public Message AddLongs(long[] array, bool includeLength = true)
        {
            if (includeLength)
                AddArrayLength(array.Length);

            if (UnwrittenBits < array.Length * sizeof(long) * BitsPerByte)
                throw new InsufficientCapacityException(this, array.Length, LongName, sizeof(long) * BitsPerByte);

            for (int i = 0; i < array.Length; i++)
            {
                Converter.LongToBits(array[i], Bytes, writeBit);
                writeBit += sizeof(long) * BitsPerByte;
            }

            return this;
        }

        /// <summary>Adds a <see cref="ulong"/> array to the message.</summary>
        /// <param name="array">The array to add.</param>
        /// <param name="includeLength">Whether or not to include the length of the array in the message.</param>
        /// <returns>The message that the array was added to.</returns>
        public Message AddULongs(ulong[] array, bool includeLength = true)
        {
            if (includeLength)
                AddArrayLength(array.Length);

            if (UnwrittenBits < array.Length * sizeof(ulong) * BitsPerByte)
                throw new InsufficientCapacityException(this, array.Length, ULongName, sizeof(ulong) * BitsPerByte);

            for (int i = 0; i < array.Length; i++)
            {
                Converter.ULongToBits(array[i], Bytes, writeBit);
                writeBit += sizeof(ulong) * BitsPerByte;
            }

            return this;
        }

        /// <summary>Retrieves a <see cref="long"/> array from the message.</summary>
        /// <returns>The array that was retrieved.</returns>
        public long[] GetLongs() => GetLongs(GetArrayLength());
        /// <summary>Retrieves a <see cref="long"/> array from the message.</summary>
        /// <param name="amount">The amount of longs to retrieve.</param>
        /// <returns>The array that was retrieved.</returns>
        public long[] GetLongs(int amount)
        {
            long[] array = new long[amount];
            ReadLongs(amount, array);
            return array;
        }
        /// <summary>Populates a <see cref="long"/> array with longs retrieved from the message.</summary>
        /// <param name="amount">The amount of longs to retrieve.</param>
        /// <param name="intoArray">The array to populate.</param>
        /// <param name="startIndex">The position at which to start populating the array.</param>
        public void GetLongs(int amount, long[] intoArray, int startIndex = 0)
        {
            if (startIndex + amount > intoArray.Length)
                throw new ArgumentException(nameof(amount), ArrayNotLongEnoughError(amount, intoArray.Length, startIndex, LongName));

            ReadLongs(amount, intoArray, startIndex);
        }

        /// <summary>Retrieves a <see cref="ulong"/> array from the message.</summary>
        /// <returns>The array that was retrieved.</returns>
        public ulong[] GetULongs() => GetULongs(GetArrayLength());
        /// <summary>Retrieves a <see cref="ulong"/> array from the message.</summary>
        /// <param name="amount">The amount of ulongs to retrieve.</param>
        /// <returns>The array that was retrieved.</returns>
        public ulong[] GetULongs(int amount)
        {
            ulong[] array = new ulong[amount];
            ReadULongs(amount, array);
            return array;
        }
        /// <summary>Populates a <see cref="ulong"/> array with ulongs retrieved from the message.</summary>
        /// <param name="amount">The amount of ulongs to retrieve.</param>
        /// <param name="intoArray">The array to populate.</param>
        /// <param name="startIndex">The position at which to start populating the array.</param>
        public void GetULongs(int amount, ulong[] intoArray, int startIndex = 0)
        {
            if (startIndex + amount > intoArray.Length)
                throw new ArgumentException(nameof(amount), ArrayNotLongEnoughError(amount, intoArray.Length, startIndex, ULongName));

            ReadULongs(amount, intoArray, startIndex);
        }

        /// <summary>Reads a number of longs from the message and writes them into the given array.</summary>
        /// <param name="amount">The amount of longs to read.</param>
        /// <param name="intoArray">The array to write the longs into.</param>
        /// <param name="startIndex">The position at which to start writing into the array.</param>
        private void ReadLongs(int amount, long[] intoArray, int startIndex = 0)
        {
            if (UnreadBits < amount * sizeof(long) * BitsPerByte)
            {
                RiptideLogger.Log(LogType.Error, NotEnoughBitsError(intoArray.Length, LongName));
                amount = UnreadBits / (sizeof(long) * BitsPerByte);
            }

            for (int i = 0; i < amount; i++)
            {
                intoArray[startIndex + i] = Converter.LongFromBits(Bytes, readBit);
                readBit += sizeof(long) * BitsPerByte;
            }
        }

        /// <summary>Reads a number of ulongs from the message and writes them into the given array.</summary>
        /// <param name="amount">The amount of ulongs to read.</param>
        /// <param name="intoArray">The array to write the ulongs into.</param>
        /// <param name="startIndex">The position at which to start writing into the array.</param>
        private void ReadULongs(int amount, ulong[] intoArray, int startIndex = 0)
        {
            if (UnreadBits < amount * sizeof(ulong) * BitsPerByte)
            {
                RiptideLogger.Log(LogType.Error, NotEnoughBitsError(intoArray.Length, ULongName));
                amount = UnreadBits / (sizeof(ulong) * BitsPerByte);
            }

            for (int i = 0; i < amount; i++)
            {
                intoArray[startIndex + i] = Converter.ULongFromBits(Bytes, readBit);
                readBit += sizeof(ulong) * BitsPerByte;
            }
        }
        #endregion

        #region Float
        /// <summary>Adds a <see cref="float"/> to the message.</summary>
        /// <param name="value">The <see cref="float"/> to add.</param>
        /// <returns>The message that the <see cref="float"/> was added to.</returns>
        public Message AddFloat(float value)
        {
            if (UnwrittenBits < sizeof(float) * BitsPerByte)
                throw new InsufficientCapacityException(this, FloatName, sizeof(float) * BitsPerByte);

            Converter.FloatToBits(value, Bytes, writeBit);
            writeBit += sizeof(float) * BitsPerByte;
            return this;
        }

        /// <summary>Retrieves a <see cref="float"/> from the message.</summary>
        /// <returns>The <see cref="float"/> that was retrieved.</returns>
        public float GetFloat()
        {
            if (UnreadBits < sizeof(float) * BitsPerByte)
            {
                RiptideLogger.Log(LogType.Error, NotEnoughBitsError(FloatName));
                return 0;
            }

            float value = Converter.FloatFromBits(Bytes, readBit);
            readBit += sizeof(float) * BitsPerByte;
            return value;
        }

        /// <summary>Adds a <see cref="float"/> array to the message.</summary>
        /// <param name="array">The array to add.</param>
        /// <param name="includeLength">Whether or not to include the length of the array in the message.</param>
        /// <returns>The message that the array was added to.</returns>
        public Message AddFloats(float[] array, bool includeLength = true)
        {
            if (includeLength)
                AddArrayLength(array.Length);

            if (UnwrittenBits < array.Length * sizeof(float) * BitsPerByte)
                throw new InsufficientCapacityException(this, array.Length, FloatName, sizeof(float) * BitsPerByte);

            for (int i = 0; i < array.Length; i++)
            {
                Converter.FloatToBits(array[i], Bytes, writeBit);
                writeBit += sizeof(float) * BitsPerByte;
            }

            return this;
        }

        /// <summary>Retrieves a <see cref="float"/> array from the message.</summary>
        /// <returns>The array that was retrieved.</returns>
        public float[] GetFloats() => GetFloats(GetArrayLength());
        /// <summary>Retrieves a <see cref="float"/> array from the message.</summary>
        /// <param name="amount">The amount of floats to retrieve.</param>
        /// <returns>The array that was retrieved.</returns>
        public float[] GetFloats(int amount)
        {
            float[] array = new float[amount];
            ReadFloats(amount, array);
            return array;
        }
        /// <summary>Populates a <see cref="float"/> array with floats retrieved from the message.</summary>
        /// <param name="amount">The amount of floats to retrieve.</param>
        /// <param name="intoArray">The array to populate.</param>
        /// <param name="startIndex">The position at which to start populating the array.</param>
        public void GetFloats(int amount, float[] intoArray, int startIndex = 0)
        {
            if (startIndex + amount > intoArray.Length)
                throw new ArgumentException(nameof(amount), ArrayNotLongEnoughError(amount, intoArray.Length, startIndex, FloatName));

            ReadFloats(amount, intoArray, startIndex);
        }

        /// <summary>Reads a number of floats from the message and writes them into the given array.</summary>
        /// <param name="amount">The amount of floats to read.</param>
        /// <param name="intoArray">The array to write the floats into.</param>
        /// <param name="startIndex">The position at which to start writing into the array.</param>
        private void ReadFloats(int amount, float[] intoArray, int startIndex = 0)
        {
            if (UnreadBits < amount * sizeof(float) * BitsPerByte)
            {
                RiptideLogger.Log(LogType.Error, NotEnoughBitsError(intoArray.Length, FloatName));
                amount = UnreadBits / (sizeof(float) * BitsPerByte);
            }

            for (int i = 0; i < amount; i++)
            {
                intoArray[startIndex + i] = Converter.FloatFromBits(Bytes, readBit);
                readBit += sizeof(float) * BitsPerByte;
            }
        }
        #endregion

        #region Double
        /// <summary>Adds a <see cref="double"/> to the message.</summary>
        /// <param name="value">The <see cref="double"/> to add.</param>
        /// <returns>The message that the <see cref="double"/> was added to.</returns>
        public Message AddDouble(double value)
        {
            if (UnwrittenBits < sizeof(double) * BitsPerByte)
                throw new InsufficientCapacityException(this, DoubleName, sizeof(double) * BitsPerByte);

            Converter.DoubleToBits(value, Bytes, writeBit);
            writeBit += sizeof(double) * BitsPerByte;
            return this;
        }

        /// <summary>Retrieves a <see cref="double"/> from the message.</summary>
        /// <returns>The <see cref="double"/> that was retrieved.</returns>
        public double GetDouble()
        {
            if (UnreadBits < sizeof(double) * BitsPerByte)
            {
                RiptideLogger.Log(LogType.Error, NotEnoughBitsError(DoubleName));
                return 0;
            }

            double value = Converter.DoubleFromBits(Bytes, readBit);
            readBit += sizeof(double) * BitsPerByte;
            return value;
        }

        /// <summary>Adds a <see cref="double"/> array to the message.</summary>
        /// <param name="array">The array to add.</param>
        /// <param name="includeLength">Whether or not to include the length of the array in the message.</param>
        /// <returns>The message that the array was added to.</returns>
        public Message AddDoubles(double[] array, bool includeLength = true)
        {
            if (includeLength)
                AddArrayLength(array.Length);

            if (UnwrittenBits < array.Length * sizeof(double) * BitsPerByte)
                throw new InsufficientCapacityException(this, array.Length, DoubleName, sizeof(double) * BitsPerByte);

            for (int i = 0; i < array.Length; i++)
            {
                Converter.DoubleToBits(array[i], Bytes, writeBit);
                writeBit += sizeof(double) * BitsPerByte;
            }

            return this;
        }

        /// <summary>Retrieves a <see cref="double"/> array from the message.</summary>
        /// <returns>The array that was retrieved.</returns>
        public double[] GetDoubles() => GetDoubles(GetArrayLength());
        /// <summary>Retrieves a <see cref="double"/> array from the message.</summary>
        /// <param name="amount">The amount of doubles to retrieve.</param>
        /// <returns>The array that was retrieved.</returns>
        public double[] GetDoubles(int amount)
        {
            double[] array = new double[amount];
            ReadDoubles(amount, array);
            return array;
        }
        /// <summary>Populates a <see cref="double"/> array with doubles retrieved from the message.</summary>
        /// <param name="amount">The amount of doubles to retrieve.</param>
        /// <param name="intoArray">The array to populate.</param>
        /// <param name="startIndex">The position at which to start populating the array.</param>
        public void GetDoubles(int amount, double[] intoArray, int startIndex = 0)
        {
            if (startIndex + amount > intoArray.Length)
                throw new ArgumentException(nameof(amount), ArrayNotLongEnoughError(amount, intoArray.Length, startIndex, DoubleName));

            ReadDoubles(amount, intoArray, startIndex);
        }

        /// <summary>Reads a number of doubles from the message and writes them into the given array.</summary>
        /// <param name="amount">The amount of doubles to read.</param>
        /// <param name="intoArray">The array to write the doubles into.</param>
        /// <param name="startIndex">The position at which to start writing into the array.</param>
        private void ReadDoubles(int amount, double[] intoArray, int startIndex = 0)
        {
            if (UnreadBits < amount * sizeof(double) * BitsPerByte)
            {
                RiptideLogger.Log(LogType.Error, NotEnoughBitsError(intoArray.Length, DoubleName));
                amount = UnreadBits / (sizeof(double) * BitsPerByte);
            }

            for (int i = 0; i < amount; i++)
            {
                intoArray[startIndex + i] = Converter.DoubleFromBits(Bytes, readBit);
                readBit += sizeof(double) * BitsPerByte;
            }
        }
        #endregion

        #region String
        /// <summary>Adds a <see cref="string"/> to the message.</summary>
        /// <param name="value">The <see cref="string"/> to add.</param>
        /// <returns>The message that the <see cref="string"/> was added to.</returns>
        public Message AddString(string value)
        {
            byte[] stringBytes = Encoding.UTF8.GetBytes(value);
            int requiredBytes = stringBytes.Length + (stringBytes.Length <= OneByteLengthThreshold ? 1 : 2);
            if (UnwrittenBits < requiredBytes * BitsPerByte)
                throw new InsufficientCapacityException(this, StringName, requiredBytes * BitsPerByte);

            AddBytes(stringBytes);
            return this;
        }

        /// <summary>Retrieves a <see cref="string"/> from the message.</summary>
        /// <returns>The <see cref="string"/> that was retrieved.</returns>
        public string GetString()
        {
            int length = GetArrayLength(); // Get the length of the string (in bytes, NOT characters)
            if (UnreadBits < length * BitsPerByte)
            {
                RiptideLogger.Log(LogType.Error, NotEnoughBitsError(StringName, "shortened string"));
                length = UnreadBits / BitsPerByte;
            }

            string value = Encoding.UTF8.GetString(GetBytes(length), 0, length);
            return value;
        }

        /// <summary>Adds a <see cref="string"/> array to the message.</summary>
        /// <param name="array">The array to add.</param>
        /// <param name="includeLength">Whether or not to include the length of the array in the message.</param>
        /// <returns>The message that the array was added to.</returns>
        public Message AddStrings(string[] array, bool includeLength = true)
        {
            if (includeLength)
                AddArrayLength(array.Length);

            // It'd be ideal to throw an exception here (instead of in AddString) if the entire array isn't going to fit, but since each string could
            // be (and most likely is) a different length and some characters use more than a single byte, the only way of doing that would be to loop
            // through the whole array here and convert each string to bytes ahead of time, just to get the required byte count. Then if they all fit
            // into the message, they would all be converted again when actually being written into the byte array, which is obviously inefficient.

            for (int i = 0; i < array.Length; i++)
                AddString(array[i]);

            return this;
        }

        /// <summary>Retrieves a <see cref="string"/> array from the message.</summary>
        /// <returns>The array that was retrieved.</returns>
        public string[] GetStrings() => GetStrings(GetArrayLength());
        /// <summary>Retrieves a <see cref="string"/> array from the message.</summary>
        /// <param name="amount">The amount of strings to retrieve.</param>
        /// <returns>The array that was retrieved.</returns>
        public string[] GetStrings(int amount)
        {
            string[] array = new string[amount];
            for (int i = 0; i < array.Length; i++)
                array[i] = GetString();

            return array;
        }
        /// <summary>Populates a <see cref="string"/> array with strings retrieved from the message.</summary>
        /// <param name="amount">The amount of strings to retrieve.</param>
        /// <param name="intoArray">The array to populate.</param>
        /// <param name="startIndex">The position at which to start populating the array.</param>
        public void GetStrings(int amount, string[] intoArray, int startIndex = 0)
        {
            if (startIndex + amount > intoArray.Length)
                throw new ArgumentException(nameof(amount), ArrayNotLongEnoughError(amount, intoArray.Length, startIndex, StringName));

            for (int i = 0; i < amount; i++)
                intoArray[startIndex + i] = GetString();
        }
        #endregion

        #region Array Lengths
        /// <summary>The maximum number of elements an array can contain where the length still fits into a single byte.</summary>
        private const int OneByteLengthThreshold = 0b_0111_1111;
        /// <summary>The maximum number of elements an array can contain where the length still fits into two byte2.</summary>
        private const int TwoByteLengthThreshold = 0b_0111_1111_1111_1111;

        /// <summary>Adds the length of an array to the message, using either 8 or 16 bits depending on how large the array is. Does not support arrays with more than 32,767 elements.</summary>
        /// <param name="length">The length of the array.</param>
        private void AddArrayLength(int length)
        {
            if (UnwrittenBits < BitsPerByte)
                throw new InsufficientCapacityException(this, ArrayLengthName, (length <= OneByteLengthThreshold ? 1 : 2) * BitsPerByte);

            if (length <= OneByteLengthThreshold)
            {
                Converter.ByteToBits((byte)(length << 1), Bytes, writeBit);
                writeBit += BitsPerByte;
            }
            else
            {
                if (length > TwoByteLengthThreshold)
                    throw new ArgumentOutOfRangeException(nameof(length), $"Messages do not support auto-inclusion of array lengths for arrays with more than {TwoByteLengthThreshold} elements! Either send a smaller array or set the 'includeLength' paremeter to false in the Add method and manually pass the array length to the Get method.");
                
                if (UnwrittenBits < sizeof(ushort) * BitsPerByte)
                    throw new InsufficientCapacityException(this, ArrayLengthName, sizeof(ushort) * BitsPerByte);

                length = (length << 1) | 1; // OR in the big array flag
                Converter.UShortToBits((ushort)length, Bytes, writeBit);
                writeBit += sizeof(ushort) * BitsPerByte;
            }
        }

        /// <summary>Retrieves the length of an array from the message, using either 8 or 16 bits depending on how large the array is.</summary>
        /// <returns>The length of the array.</returns>
        private int GetArrayLength()
        {
            if (UnreadBits < BitsPerByte)
            {
                RiptideLogger.Log(LogType.Error, NotEnoughBitsError(ArrayLengthName));
                return 0;
            }

            int pos = readBit / BitsPerByte;
            int bit = readBit % BitsPerByte;
            int value;
            if ((Bytes[pos] & (1 << bit)) == 0)
            {
                value = Converter.ByteFromBits(Bytes, readBit);
                readBit += BitsPerByte;
                return value >> 1; // Get rid of the flag bit
            }
            
            if (UnreadBits < sizeof(ushort) * BitsPerByte)
            {
                RiptideLogger.Log(LogType.Error, NotEnoughBitsError(ArrayLengthName));
                return 0;
            }

            value = Converter.UShortFromBits(Bytes, readBit);
            readBit += sizeof(ushort) * BitsPerByte;
            return value >> 1; // Get rid of the flag bit
        }
        #endregion

        #region IMessageSerializable Types
        /// <summary>Adds a serializable to the message.</summary>
        /// <param name="value">The serializable to add.</param>
        /// <returns>The message that the serializable was added to.</returns>
        public Message AddSerializable<T>(T value) where T : IMessageSerializable
        {
            value.Serialize(this);
            return this;
        }

        /// <summary>Retrieves a serializable from the message.</summary>
        /// <returns>The serializable that was retrieved.</returns>
        public T GetSerializable<T>() where T : IMessageSerializable, new()
        {
            T t = new T();
            t.Deserialize(this);
            return t;
        }

        /// <summary>Adds an array of serializables to the message.</summary>
        /// <param name="array">The array to add.</param>
        /// <param name="includeLength">Whether or not to include the length of the array in the message.</param>
        /// <returns>The message that the array was added to.</returns>
        public Message AddSerializables<T>(T[] array, bool includeLength = true) where T : IMessageSerializable
        {
            if (includeLength)
                AddArrayLength(array.Length);

            for (int i = 0; i < array.Length; i++)
                AddSerializable(array[i]);

            return this;
        }

        /// <summary>Retrieves an array of serializables from the message.</summary>
        /// <returns>The array that was retrieved.</returns>
        public T[] GetSerializables<T>() where T : IMessageSerializable, new() => GetSerializables<T>(GetArrayLength());
        /// <summary>Retrieves an array of serializables from the message.</summary>
        /// <param name="amount">The amount of serializables to retrieve.</param>
        /// <returns>The array that was retrieved.</returns>
        public T[] GetSerializables<T>(int amount) where T : IMessageSerializable, new()
        {
            T[] array = new T[amount];
            ReadSerializables(amount, array);
            return array;
        }
        /// <summary>Populates an array of serializables retrieved from the message.</summary>
        /// <param name="amount">The amount of serializables to retrieve.</param>
        /// <param name="intoArray">The array to populate.</param>
        /// <param name="startIndex">The position at which to start populating the array.</param>
        public void GetSerializables<T>(int amount, T[] intoArray, int startIndex = 0) where T : IMessageSerializable, new()
        {
            if (startIndex + amount > intoArray.Length)
                throw new ArgumentException(nameof(amount), ArrayNotLongEnoughError(amount, intoArray.Length, startIndex, typeof(T).Name));

            ReadSerializables(amount, intoArray, startIndex);
        }

        /// <summary>Reads a number of serializables from the message and writes them into the given array.</summary>
        /// <param name="amount">The amount of serializables to read.</param>
        /// <param name="intArray">The array to write the serializables into.</param>
        /// <param name="startIndex">The position at which to start writing into <paramref name="intArray"/>.</param>
        private void ReadSerializables<T>(int amount, T[] intArray, int startIndex = 0) where T : IMessageSerializable, new()
        {
            for (int i = 0; i < amount; i++)
            {
                intArray[startIndex + i] = GetSerializable<T>();
            }
        }
        #endregion

        #region Overload Versions
        /// <inheritdoc cref="AddByte(byte)"/>
        /// <remarks>This method is simply an alternative way of calling <see cref="AddByte(byte)"/>.</remarks>
        public Message Add(byte value) => AddByte(value);
        /// <inheritdoc cref="AddSByte(sbyte)"/>
        /// <remarks>This method is simply an alternative way of calling <see cref="AddSByte(sbyte)"/>.</remarks>
        public Message Add(sbyte value) => AddSByte(value);
        /// <inheritdoc cref="AddBool(bool)"/>
        /// <remarks>This method is simply an alternative way of calling <see cref="AddBool(bool)"/>.</remarks>
        public Message Add(bool value) => AddBool(value);
        /// <inheritdoc cref="AddShort(short)"/>
        /// <remarks>This method is simply an alternative way of calling <see cref="AddShort(short)"/>.</remarks>
        public Message Add(short value) => AddShort(value);
        /// <inheritdoc cref="AddUShort(ushort)"/>
        /// <remarks>This method is simply an alternative way of calling <see cref="AddUShort(ushort)"/>.</remarks>
        public Message Add(ushort value) => AddUShort(value);
        /// <inheritdoc cref="AddInt(int)"/>
        /// <remarks>This method is simply an alternative way of calling <see cref="AddInt(int)"/>.</remarks>
        public Message Add(int value) => AddInt(value);
        /// <inheritdoc cref="AddUInt(uint)"/>
        /// <remarks>This method is simply an alternative way of calling <see cref="AddUInt(uint)"/>.</remarks>
        public Message Add(uint value) => AddUInt(value);
        /// <inheritdoc cref="AddLong(long)"/>
        /// <remarks>This method is simply an alternative way of calling <see cref="AddLong(long)"/>.</remarks>
        public Message Add(long value) => AddLong(value);
        /// <inheritdoc cref="AddULong(ulong)"/>
        /// <remarks>This method is simply an alternative way of calling <see cref="AddULong(ulong)"/>.</remarks>
        public Message Add(ulong value) => AddULong(value);
        /// <inheritdoc cref="AddFloat(float)"/>
        /// <remarks>This method is simply an alternative way of calling <see cref="AddFloat(float)"/>.</remarks>
        public Message Add(float value) => AddFloat(value);
        /// <inheritdoc cref="AddDouble(double)"/>
        /// <remarks>This method is simply an alternative way of calling <see cref="AddDouble(double)"/>.</remarks>
        public Message Add(double value) => AddDouble(value);
        /// <inheritdoc cref="AddString(string)"/>
        /// <remarks>This method is simply an alternative way of calling <see cref="AddString(string)"/>.</remarks>
        public Message Add(string value) => AddString(value);
        /// <inheritdoc cref="AddSerializable{T}(T)"/>
        /// <remarks>This method is simply an alternative way of calling <see cref="AddSerializable{T}(T)"/>.</remarks>
        public Message Add<T>(T value) where T : IMessageSerializable => AddSerializable(value);

        /// <inheritdoc cref="AddBytes(byte[], bool)"/>
        /// <remarks>This method is simply an alternative way of calling <see cref="AddBytes(byte[], bool)"/>.</remarks>
        public Message Add(byte[] array, bool includeLength = true) => AddBytes(array, includeLength);
        /// <inheritdoc cref="AddSBytes(sbyte[], bool)"/>
        /// <remarks>This method is simply an alternative way of calling <see cref="AddSBytes(sbyte[], bool)"/>.</remarks>
        public Message Add(sbyte[] array, bool includeLength = true) => AddSBytes(array, includeLength);
        /// <inheritdoc cref="AddBools(bool[], bool)"/>
        /// <remarks>This method is simply an alternative way of calling <see cref="AddBools(bool[], bool)"/>.</remarks>
        public Message Add(bool[] array, bool includeLength = true) => AddBools(array, includeLength);
        /// <inheritdoc cref="AddShorts(short[], bool)"/>
        /// <remarks>This method is simply an alternative way of calling <see cref="AddShorts(short[], bool)"/>.</remarks>
        public Message Add(short[] array, bool includeLength = true) => AddShorts(array, includeLength);
        /// <inheritdoc cref="AddUShorts(ushort[], bool)"/>
        /// <remarks>This method is simply an alternative way of calling <see cref="AddUShorts(ushort[], bool)"/>.</remarks>
        public Message Add(ushort[] array, bool includeLength = true) => AddUShorts(array, includeLength);
        /// <inheritdoc cref="AddInts(int[], bool)"/>
        /// <remarks>This method is simply an alternative way of calling <see cref="AddInts(int[], bool)"/>.</remarks>
        public Message Add(int[] array, bool includeLength = true) => AddInts(array, includeLength);
        /// <inheritdoc cref="AddUInts(uint[], bool)"/>
        /// <remarks>This method is simply an alternative way of calling <see cref="AddUInts(uint[], bool)"/>.</remarks>
        public Message Add(uint[] array, bool includeLength = true) => AddUInts(array, includeLength);
        /// <inheritdoc cref="AddLongs(long[], bool)"/>
        /// <remarks>This method is simply an alternative way of calling <see cref="AddLongs(long[], bool)"/>.</remarks>
        public Message Add(long[] array, bool includeLength = true) => AddLongs(array, includeLength);
        /// <inheritdoc cref="AddULongs(ulong[], bool)"/>
        /// <remarks>This method is simply an alternative way of calling <see cref="AddULongs(ulong[], bool)"/>.</remarks>
        public Message Add(ulong[] array, bool includeLength = true) => AddULongs(array, includeLength);
        /// <inheritdoc cref="AddFloats(float[], bool)"/>
        /// <remarks>This method is simply an alternative way of calling <see cref="AddFloats(float[], bool)"/>.</remarks>
        public Message Add(float[] array, bool includeLength = true) => AddFloats(array, includeLength);
        /// <inheritdoc cref="AddDoubles(double[], bool)"/>
        /// <remarks>This method is simply an alternative way of calling <see cref="AddDoubles(double[], bool)"/>.</remarks>
        public Message Add(double[] array, bool includeLength = true) => AddDoubles(array, includeLength);
        /// <inheritdoc cref="AddStrings(string[], bool)"/>
        /// <remarks>This method is simply an alternative way of calling <see cref="AddStrings(string[], bool)"/>.</remarks>
        public Message Add(string[] array, bool includeLength = true) => AddStrings(array, includeLength);
        /// <inheritdoc cref="AddSerializables{T}(T[], bool)"/>
        /// <remarks>This method is simply an alternative way of calling <see cref="AddSerializables{T}(T[], bool)"/>.</remarks>
        public Message Add<T>(T[] array, bool includeLength = true) where T : IMessageSerializable, new() => AddSerializables(array, includeLength);
        #endregion
        #endregion

        #region Error Messaging
        /// <summary>The name of a <see cref="byte"/> value.</summary>
        private const string ByteName        = "byte";
        /// <summary>The name of a <see cref="sbyte"/> value.</summary>
        private const string SByteName       = "sbyte";
        /// <summary>The name of a <see cref="bool"/> value.</summary>
        private const string BoolName        = "bool";
        /// <summary>The name of a <see cref="short"/> value.</summary>
        private const string ShortName       = "short";
        /// <summary>The name of a <see cref="ushort"/> value.</summary>
        private const string UShortName      = "ushort";
        /// <summary>The name of an <see cref="int"/> value.</summary>
        private const string IntName         = "int";
        /// <summary>The name of a <see cref="uint"/> value.</summary>
        private const string UIntName        = "uint";
        /// <summary>The name of a <see cref="long"/> value.</summary>
        private const string LongName        = "long";
        /// <summary>The name of a <see cref="ulong"/> value.</summary>
        private const string ULongName       = "ulong";
        /// <summary>The name of a <see cref="float"/> value.</summary>
        private const string FloatName       = "float";
        /// <summary>The name of a <see cref="double"/> value.</summary>
        private const string DoubleName      = "double";
        /// <summary>The name of a <see cref="string"/> value.</summary>
        private const string StringName      = "string";
        /// <summary>The name of an array length value.</summary>
        private const string ArrayLengthName = "array length";

        /// <summary>Constructs an error message for when a message contains insufficient unread bits to retrieve a certain value.</summary>
        /// <param name="valueName">The name of the value type for which the retrieval attempt failed.</param>
        /// <param name="defaultReturn">Text describing the value which will be returned.</param>
        /// <returns>The error message.</returns>
        private string NotEnoughBitsError(string valueName, string defaultReturn = "0")
        {
            return $"Message only contains {UnreadBits} unread {Helper.CorrectForm(UnreadBits, "bit")}, which is not enough to retrieve a value of type '{valueName}'! Returning {defaultReturn}.";
        }
        /// <summary>Constructs an error message for when a message contains insufficient unread bits to retrieve an array of values.</summary>
        /// <param name="arrayLength">The expected length of the array.</param>
        /// <param name="valueName">The name of the value type for which the retrieval attempt failed.</param>
        /// <returns>The error message.</returns>
        private string NotEnoughBitsError(int arrayLength, string valueName)
        {
            return $"Message only contains {UnreadBits} unread {Helper.CorrectForm(UnreadBits, "bit")}, which is not enough to retrieve {arrayLength} {Helper.CorrectForm(arrayLength, valueName)}! Returned array will contain default elements.";
        }

        /// <summary>Constructs an error message for when a number of retrieved values do not fit inside the bounds of the provided array.</summary>
        /// <param name="amount">The number of values being retrieved.</param>
        /// <param name="arrayLength">The length of the provided array.</param>
        /// <param name="startIndex">The position in the array at which to begin writing values.</param>
        /// <param name="valueName">The name of the value type which is being retrieved.</param>
        /// <param name="pluralValueName">The name of the value type in plural form. If left empty, this will be set to <paramref name="valueName"/> with an <c>s</c> appended to it.</param>
        /// <returns>The error message.</returns>
        private string ArrayNotLongEnoughError(int amount, int arrayLength, int startIndex, string valueName, string pluralValueName = "")
        {
            if (string.IsNullOrEmpty(pluralValueName))
                pluralValueName = $"{valueName}s";

            return $"The amount of {pluralValueName} to retrieve ({amount}) is greater than the number of elements from the start index ({startIndex}) to the end of the given array (length: {arrayLength})!";
        }
        #endregion
    }
}
