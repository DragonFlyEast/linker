﻿using cmonitor.client.capi;
using cmonitor.plugins.relay;
using cmonitor.tunnel;
using cmonitor.tunnel.connection;
using common.libs;
using common.libs.api;
using common.libs.extends;
using System.Collections.Concurrent;

namespace cmonitor.plugins.connections
{
    public sealed class ConnectionsApiController : IApiClientController
    {

        private uint connectionVersion = 0;
        public uint ConnectionVersion => connectionVersion;
        private ConcurrentDictionary<string, ConcurrentDictionary<string, ITunnelConnection>> connections { get; } = new ConcurrentDictionary<string, ConcurrentDictionary<string, ITunnelConnection>>();

        public ConnectionsApiController(TunnelTransfer tunnelTransfer, RelayTransfer relayTransfer)
        {
            tunnelTransfer.SetConnectedCallback(Helper.GlobalString, AddConnections);
            relayTransfer.SetConnectedCallback(Helper.GlobalString, AddConnections);
        }

        public ConnectionListInfo Get(ApiControllerParamsInfo param)
        {
            uint hashCode = uint.Parse(param.Content);
            //if (hashCode != connectionVersion)
            {
                foreach (var item in connections)
                {
                    foreach (var connection in item.Value)
                    {
                        try
                        {
                            connection.Value.SendPing();
                        }
                        catch (Exception)
                        {
                        }
                    }
                }
                return new ConnectionListInfo { HashCode = connectionVersion, List = connections };
            }
            //return new ConnectionListInfo { HashCode = connectionVersion };
        }
        public bool Remove(ApiControllerParamsInfo param)
        {
            RemoveConnectionInfo removeConnectionInfo = param.Content.DeJson<RemoveConnectionInfo>();
            RemoveConnection(removeConnectionInfo.MachineId, removeConnectionInfo.TransactionId);
            return true;
        }

        private void RemoveConnection(string machineId, string transactionId)
        {
            if (connections.TryGetValue(machineId, out ConcurrentDictionary<string, ITunnelConnection> cons))
            {
                if (cons.TryRemove(transactionId, out ITunnelConnection _connection))
                {
                    try
                    {
                        _connection.Dispose();
                    }
                    catch (Exception)
                    {
                    }
                }
            }
            Interlocked.Increment(ref connectionVersion);
        }
        private void AddConnections(ITunnelConnection connection)
        {
            lock (this)
            {
                if (connections.TryGetValue(connection.RemoteMachineId, out ConcurrentDictionary<string, ITunnelConnection> cons) == false)
                {
                    cons = new ConcurrentDictionary<string, ITunnelConnection>();
                    connections.TryAdd(connection.RemoteMachineId, cons);
                }
                if (cons.TryRemove(connection.TransactionId, out ITunnelConnection _connection))
                {
                    try
                    {
                        if (Logger.Instance.LoggerLevel <= LoggerTypes.DEBUG)
                            Logger.Instance.Warning($"TryRemove {_connection.GetHashCode()} {_connection.TransactionId} {_connection.ToJson()}");
                        _connection.Dispose();
                    }
                    catch (Exception)
                    {
                    }
                }
                if (Logger.Instance.LoggerLevel <= LoggerTypes.DEBUG)
                    Logger.Instance.Warning($"TryAdd {connection.GetHashCode()} {connection.TransactionId} {connection.ToJson()}");
                cons.TryAdd(connection.TransactionId, connection);
            }
            Interlocked.Increment(ref connectionVersion);
        }


        public sealed class ConnectionListInfo
        {
            public ConcurrentDictionary<string, ConcurrentDictionary<string, ITunnelConnection>> List { get; set; }
            public uint HashCode { get; set; }

        }
        sealed class RemoveConnectionInfo
        {
            public string MachineId { get; set; }
            public string TransactionId { get; set; }

        }
    }
}
