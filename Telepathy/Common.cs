﻿// common code used by server and client
using System;
using System.Net.Sockets;

namespace Telepathy
{
    public abstract class Common
    {
        // common code /////////////////////////////////////////////////////////
        // connectionId counter
        // (right now we only use it from one listener thread, but we might have
        //  multiple threads later in case of WebSockets etc.)
        protected SafeCounter counter = new SafeCounter();

        // incoming message queue of <connectionId, message>
        // (not a HashSet because one connection can have multiple new messages)
        protected SafeQueue<Message> messageQueue = new SafeQueue<Message>(); // accessed from getmessage and listener thread

        // removes and returns the oldest message from the message queue.
        // (might want to call this until it doesn't return anything anymore)
        // only returns one message each time so it's more similar to LLAPI:
        // https://docs.unity3d.com/ScriptReference/Networking.NetworkTransport.ReceiveFromHost.html
        // -> Connected, Data, Disconnected can all be detected with this function. simple and stupid.
        // -> bool return makes while (GetMessage(out Message)) easier!
        public bool GetNextMessage(out Message message)
        {
            return messageQueue.TryDequeue(out message);
        }

        // static helper functions /////////////////////////////////////////////
        // fast ushort to byte[] conversion and vice versa
        // -> test with 100k conversions:
        //    BitConverter.GetBytes(ushort): 144ms
        //    bit shifting: 11ms
        // -> 10x speed improvement makes this optimization actually worth it
        // -> this way we don't need to allocate BinaryWriter/Reader either
        static byte[] UShortToBytes(ushort value)
        {
            return new byte[]
            {
                (byte)value,
                (byte)(value >> 8)
            };
        }
        static ushort BytesToUShort(byte[] bytes)
        {
            return (ushort)((bytes[1] << 8) + bytes[0]);
        }

        // send message (via stream) with the <size,content> message structure
        protected static bool SendMessage(NetworkStream stream, byte[] content)
        {
            //Logger.Log("SendMessage: " + BitConverter.ToString(data));

            // can we still write to this socket (not disconnected?)
            if (!stream.CanWrite)
            {
                Logger.LogWarning("Send: stream not writeable: " + stream);
                return false;
            }

            // check size
            if (content.Length > ushort.MaxValue)
            {
                Logger.LogError("Send: message too big(" + content.Length + ") max=" + ushort.MaxValue);
                return false;
            }

            // stream.Write throws exceptions if client sends with high frequency
            // and the server stops
            try
            {
                // write size header and content
                byte[] header = UShortToBytes((ushort)content.Length);
                stream.Write(header, 0, header.Length);
                stream.Write(content, 0, content.Length);
                stream.Flush();
                return true;
            }
            catch (Exception exception)
            {
                // log as regular message because servers do shut down sometimes
                Logger.Log("Send: stream.Write exception: " + exception);
                return false;
            }
        }

        // read message (via stream) with the <size,content> message structure
        protected static bool ReadMessageBlocking(NetworkStream stream, out byte[] content)
        {
            content = null;

            // read exactly 2 bytes for header (blocking)
            byte[] header = new byte[2];
            if (!stream.ReadExactly(header, 2))
                return false;
            ushort size = BytesToUShort(header);

            // read exactly 'size' bytes for content (blocking)
            content = new byte[size];
            if (!stream.ReadExactly(content, size))
                return false;

            return true;
        }

        // thread receive function is the same for client and server's clients
        protected static void ReceiveLoop(SafeQueue<Message> messageQueue, uint connectionId, TcpClient client)
        {
            // get NetworkStream from client
            NetworkStream stream = client.GetStream();

            // absolutely must wrap with try/catch, otherwise thread exceptions
            // are silent
            try
            {
                Logger.Log("Telepathy: started receive function for connectionId=" + connectionId);


                // add connected event to queue
                messageQueue.Enqueue(new Message(connectionId, EventType.Connected, null));

                // let's talk about reading data.
                // -> normally we would read as much as possible and then
                //    extract as many <size,content>,<size,content> messages
                //    as we received this time. this is really complicated
                //    and expensive to do though
                // -> instead we use a trick:
                //      Read(2) -> size
                //        Read(size) -> content
                //      repeat
                //    Read is blocking, but it doesn't matter since the
                //    best thing to do until the full message arrives,
                //    is to wait.
                // => this is the most elegant AND fast solution.
                //    + no resizing
                //    + no extra allocations, just one for the content
                //    + no crazy extraction logic
                while (true)
                {
                    // read the next message (blocking) or stop if stream closed
                    byte[] content;
                    if (!ReadMessageBlocking(stream, out content))
                        break;

                    // queue it and show a warning if the queue starts to get big
                    messageQueue.Enqueue(new Message(connectionId, EventType.Data, content));
                    if (messageQueue.Count > 10000)
                        Logger.LogWarning("Telepathy: messageQueue is getting big(" + messageQueue.Count + "), try calling GetNextMessage more often. You can call it more than once per frame!");
                }
            }
            catch (Exception exception)
            {
                // just catch it. something went wrong. the thread was interrupted
                // or the connection closed or we closed our own connection or ...
                // -> either way we should stop gracefully
                Logger.Log("Telepathy: finished receive function for connectionId=" + connectionId + " reason: " + exception);
            }

            // if we got here then either the client while loop ended, or an exception happened.
            // disconnect
            messageQueue.Enqueue(new Message(connectionId, EventType.Disconnected, null));

            // clean up no matter what
            stream.Close();
            client.Close();
        }
    }
}
