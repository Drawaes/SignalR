// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Channels;
using Microsoft.AspNetCore.Sockets.Internal;

namespace Microsoft.AspNetCore.Sockets
{
    public class ConnectionManager
    {
        private readonly ConcurrentDictionary<string, ConnectionState> _connections = new ConcurrentDictionary<string, ConnectionState>();
        private readonly Timer _timer;

        public ConnectionManager()
        {
            _timer = new Timer(Scan, this, 0, 1000);
        }

        public bool TryGetConnection(string id, out ConnectionState state)
        {
            return _connections.TryGetValue(id, out state);
        }

        public ConnectionState CreateConnection()
        {
            var id = MakeNewConnectionId();

            var transportToApplication = Channel.CreateUnbounded<Message>();
            var applicationToTransport = Channel.CreateUnbounded<Message>();

            var transportSide = new ChannelConnection<Message>(applicationToTransport, transportToApplication);
            var applicationSide = new ChannelConnection<Message>(transportToApplication, applicationToTransport);

            var state = new ConnectionState(
                new Connection(id, applicationSide),
                transportSide);

            _connections.TryAdd(id, state);
            return state;
        }

        public void RemoveConnection(string id)
        {
            ConnectionState state;
            _connections.TryRemove(id, out state);

            // Remove the connection completely
        }

        private static string MakeNewConnectionId()
        {
            // TODO: We need to sign and encyrpt this
            return Guid.NewGuid().ToString();
        }

        private static void Scan(object state)
        {
            ((ConnectionManager)state).Scan();
        }

        private void Scan()
        {
            // Scan the registered connections looking for ones that have timed out
            foreach (var c in _connections)
            {
                if (c.Value.Status == ConnectionState.State.Inactive && (DateTimeOffset.UtcNow - c.Value.LastSeenUtc).TotalSeconds > 5)
                {
                    try
                    {
                        c.Value.Lock.Wait();

                        ConnectionState s;
                        if (_connections.TryRemove(c.Key, out s))
                        {
                            // REVIEW: Should we keep firing and forgetting this?
                            var ignore = s.DisposeAsync();
                        }
                    }
                    finally
                    {
                        c.Value.Lock.Release();
                    }
                }
            }
        }

        public void CloseConnections()
        {
            // Stop firing the timer
            _timer.Dispose();

            var tasks = new List<Task>();

            foreach (var c in _connections)
            {
                ConnectionState s;
                if (_connections.TryRemove(c.Key, out s))
                {
                    tasks.Add(s.DisposeAsync());
                }
            }

            Task.WaitAll(tasks.ToArray(), TimeSpan.FromSeconds(5));
        }
    }
}
