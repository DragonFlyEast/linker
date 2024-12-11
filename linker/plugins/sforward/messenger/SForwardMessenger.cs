﻿using linker.client.config;
using linker.plugins.sforward.config;
using linker.plugins.sforward.validator;
using linker.plugins.signin.messenger;
using MemoryPack;
using linker.plugins.sforward.proxy;
using linker.config;
using LiteDB;
using System.Net;
using linker.plugins.messenger;
using linker.libs;

namespace linker.plugins.sforward.messenger
{
    /// <summary>
    /// 穿透服务端
    /// </summary>
    public sealed class SForwardServerMessenger : IMessenger
    {

        private readonly SForwardProxy proxy;
        private readonly ISForwardServerCahing sForwardServerCahing;
        private readonly IMessengerSender sender;
        private readonly SignCaching signCaching;
        private readonly FileConfig configWrap;
        private readonly ISForwardValidator validator;
        private readonly SForwardServerConfigTransfer sForwardServerConfigTransfer;

        public SForwardServerMessenger(SForwardProxy proxy, ISForwardServerCahing sForwardServerCahing, IMessengerSender sender, SignCaching signCaching, FileConfig configWrap, ISForwardValidator validator, SForwardServerConfigTransfer sForwardServerConfigTransfer)
        {
            this.proxy = proxy;
            proxy.WebConnect = WebConnect;
            proxy.TunnelConnect = TunnelConnect;
            proxy.UdpConnect = UdpConnect;
            this.sForwardServerCahing = sForwardServerCahing;
            this.sender = sender;
            this.signCaching = signCaching;
            this.configWrap = configWrap;
            this.validator = validator;
            this.sForwardServerConfigTransfer = sForwardServerConfigTransfer;
        }

        /// <summary>
        /// 添加穿透
        /// </summary>
        /// <param name="connection"></param>
        [MessengerId((ushort)SForwardMessengerIds.Add)]
        public async Task Add(IConnection connection)
        {
            SForwardAddInfo sForwardAddInfo = MemoryPackSerializer.Deserialize<SForwardAddInfo>(connection.ReceiveRequestWrap.Payload.Span);
            SForwardAddResultInfo result = new SForwardAddResultInfo { Success = true, BufferSize = sForwardServerConfigTransfer.BufferSize };

            if (signCaching.TryGet(connection.Id, out SignCacheInfo cache) == false)
            {
                result.Success = false;
                result.Message = "need sign in";
                return;
            }

            try
            {
                string error = await validator.Validate(cache, sForwardAddInfo);
                if (string.IsNullOrWhiteSpace(error) == false)
                {
                    result.Success = false;
                    result.Message = error;
                    return;
                }

                //有域名，
                if (string.IsNullOrWhiteSpace(sForwardAddInfo.Domain) == false)
                {
                    //有可能是 端口范围，不是真的域名
                    if (PortRange(sForwardAddInfo.Domain, out int min, out int max))
                    {
                        for (int port = min; port <= max; port++)
                        {
                            if (sForwardServerCahing.TryAdd(port, connection.Id))
                            {
                                proxy.Stop(port);
                                result.Message = proxy.Start(port, false, sForwardServerConfigTransfer.BufferSize, cache.GroupId);
                                if (string.IsNullOrWhiteSpace(result.Message) == false)
                                {
                                    LoggerHelper.Instance.Error(result.Message);
                                    sForwardServerCahing.TryRemove(port, connection.Id, out _);
                                }
                            }
                        }
                    }
                    else
                    {
                        if (sForwardServerCahing.TryAdd(sForwardAddInfo.Domain, connection.Id) == false)
                        {
                            result.Success = false;
                            result.Message = $"domain 【{sForwardAddInfo.Domain}】 already exists";
                            LoggerHelper.Instance.Error(result.Message);
                        }
                        else
                        {
                            result.Message = $"domain 【{sForwardAddInfo.Domain}】 add success";
                        }
                    }
                    return;
                }
                //如果是端口
                if (sForwardAddInfo.RemotePort > 0)
                {
                    if (sForwardServerCahing.TryAdd(sForwardAddInfo.RemotePort, connection.Id) == false)
                    {

                        result.Success = false;
                        result.Message = $"port 【{sForwardAddInfo.RemotePort}】 already exists";
                        LoggerHelper.Instance.Error(result.Message);
                    }
                    else
                    {
                        proxy.Stop(sForwardAddInfo.RemotePort);
                        string msg = proxy.Start(sForwardAddInfo.RemotePort, false, sForwardServerConfigTransfer.BufferSize, cache.GroupId);
                        if (string.IsNullOrWhiteSpace(msg) == false)
                        {
                            result.Success = false;
                            result.Message = $"port 【{sForwardAddInfo.RemotePort}】 add fail : {msg}";
                            sForwardServerCahing.TryRemove(sForwardAddInfo.RemotePort, connection.Id, out _);
                            LoggerHelper.Instance.Error(result.Message);
                        }
                        else
                        {
                            result.Message = $"port 【{sForwardAddInfo.RemotePort}】 add success";
                        }
                    }
                    return;
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = $"sforward fail : {ex.Message}";
                LoggerHelper.Instance.Error(result.Message);
            }
            finally
            {
                connection.Write(MemoryPackSerializer.Serialize(result));
            }

        }

        /// <summary>
        /// 删除穿透
        /// </summary>
        /// <param name="connection"></param>
        [MessengerId((ushort)SForwardMessengerIds.Remove)]
        public async Task Remove(IConnection connection)
        {
            SForwardAddInfo sForwardAddInfo = MemoryPackSerializer.Deserialize<SForwardAddInfo>(connection.ReceiveRequestWrap.Payload.Span);
            SForwardAddResultInfo result = new SForwardAddResultInfo { Success = true };

            if (signCaching.TryGet(connection.Id, out SignCacheInfo cache) == false)
            {
                result.Success = false;
                result.Message = "need sign in";
                return;
            }
            try
            {
                string error = await validator.Validate(cache, sForwardAddInfo);
                if (string.IsNullOrWhiteSpace(error) == false)
                {
                    result.Success = false;
                    result.Message = error;
                    return;
                }

                if (string.IsNullOrWhiteSpace(sForwardAddInfo.Domain) == false)
                {
                    if (PortRange(sForwardAddInfo.Domain, out int min, out int max))
                    {
                        for (int port = min; port <= max; port++)
                        {
                            if (sForwardServerCahing.TryRemove(port, connection.Id, out _))
                            {
                                proxy.Stop(port);
                            }
                        }
                    }
                    else
                    {
                        sForwardServerCahing.TryRemove(sForwardAddInfo.Domain, connection.Id, out _);
                        result.Message = $"domain 【{sForwardAddInfo.Domain}】 remove success";
                    }

                    return;
                }

                if (sForwardAddInfo.RemotePort > 0)
                {
                    sForwardServerCahing.TryRemove(sForwardAddInfo.RemotePort, connection.Id, out _);
                    proxy.Stop(sForwardAddInfo.RemotePort);
                    result.Message = $"port 【{sForwardAddInfo.RemotePort}】 remove success";
                    return;
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = $"sforward fail : {ex.Message}";
            }
            finally
            {
                connection.Write(MemoryPackSerializer.Serialize(result));
            }
        }


        /// <summary>
        /// 获取对端的穿透记录
        /// </summary>
        /// <param name="connection"></param>
        [MessengerId((ushort)SForwardMessengerIds.GetForward)]
        public void GetForward(IConnection connection)
        {
            string machineId = MemoryPackSerializer.Deserialize<string>(connection.ReceiveRequestWrap.Payload.Span);
            if (signCaching.TryGet(machineId, out SignCacheInfo cacheTo) && signCaching.TryGet(connection.Id, out SignCacheInfo cacheFrom) && cacheFrom.GroupId == cacheTo.GroupId)
            {
                uint requestid = connection.ReceiveRequestWrap.RequestId;
                sender.SendReply(new MessageRequestWrap
                {
                    Connection = cacheTo.Connection,
                    MessengerId = (ushort)SForwardMessengerIds.Get,
                    Payload = connection.ReceiveRequestWrap.Payload
                }).ContinueWith(async (result) =>
                {
                    if (result.Result.Code == MessageResponeCodes.OK)
                    {
                        await sender.ReplyOnly(new MessageResponseWrap
                        {
                            Connection = connection,
                            Code = MessageResponeCodes.OK,
                            Payload = result.Result.Data,
                            RequestId = requestid
                        }, (ushort)SForwardMessengerIds.GetForward).ConfigureAwait(false);
                    }
                });
            }
        }
        /// <summary>
        /// 添加对端的穿透记录
        /// </summary>
        /// <param name="connection"></param>
        [MessengerId((ushort)SForwardMessengerIds.AddClientForward)]
        public async Task AddClientForward(IConnection connection)
        {
            SForwardAddForwardInfo info = MemoryPackSerializer.Deserialize<SForwardAddForwardInfo>(connection.ReceiveRequestWrap.Payload.Span);
            if (signCaching.TryGet(info.MachineId, out SignCacheInfo cacheTo) && signCaching.TryGet(connection.Id, out SignCacheInfo cacheFrom) && cacheFrom.GroupId == cacheTo.GroupId)
            {
                uint requestid = connection.ReceiveRequestWrap.RequestId;
                await sender.SendOnly(new MessageRequestWrap
                {
                    Connection = cacheTo.Connection,
                    MessengerId = (ushort)SForwardMessengerIds.AddClient,
                    Payload = MemoryPackSerializer.Serialize(info.Data)
                });
            }
        }
        /// <summary>
        /// 删除对端的穿透记录
        /// </summary>
        /// <param name="connection"></param>
        [MessengerId((ushort)SForwardMessengerIds.RemoveClientForward)]
        public async Task RemoveClientForward(IConnection connection)
        {
            SForwardRemoveForwardInfo info = MemoryPackSerializer.Deserialize<SForwardRemoveForwardInfo>(connection.ReceiveRequestWrap.Payload.Span);
            if (signCaching.TryGet(info.MachineId, out SignCacheInfo cacheTo) && signCaching.TryGet(connection.Id, out SignCacheInfo cacheFrom) && cacheFrom.GroupId == cacheTo.GroupId)
            {
                uint requestid = connection.ReceiveRequestWrap.RequestId;
                await sender.SendOnly(new MessageRequestWrap
                {
                    Connection = cacheTo.Connection,
                    MessengerId = (ushort)SForwardMessengerIds.RemoveClient,
                    Payload = MemoryPackSerializer.Serialize(info.Id)
                });
            }
        }

        /// <summary>
        /// 测试对端的穿透记录
        /// </summary>
        /// <param name="connection"></param>
        [MessengerId((ushort)SForwardMessengerIds.TestClientForward)]
        public async Task TestClientForward(IConnection connection)
        {
            string machineid = MemoryPackSerializer.Deserialize<string>(connection.ReceiveRequestWrap.Payload.Span);
            if (signCaching.TryGet(machineid, out SignCacheInfo cacheTo) && signCaching.TryGet(connection.Id, out SignCacheInfo cacheFrom) && cacheFrom.GroupId == cacheTo.GroupId)
            {
                uint requestid = connection.ReceiveRequestWrap.RequestId;
                await sender.SendOnly(new MessageRequestWrap
                {
                    Connection = cacheTo.Connection,
                    MessengerId = (ushort)SForwardMessengerIds.TestClientForward
                });
            }
        }

        /// <summary>
        /// 服务器收到http连接
        /// </summary>
        /// <param name="host"></param>
        /// <param name="port"></param>
        /// <param name="id"></param>
        /// <returns></returns>
        private async Task<bool> WebConnect(string host, int port, ulong id)
        {
            //发给对应的客户端
            if (sForwardServerCahing.TryGet(host, out string machineId) && signCaching.TryGet(machineId, out SignCacheInfo sign) && sign.Connected)
            {
                return await sender.SendOnly(new MessageRequestWrap
                {
                    Connection = sign.Connection,
                    MessengerId = (ushort)SForwardMessengerIds.Proxy,
                    Payload = MemoryPackSerializer.Serialize(new SForwardProxyInfo { Domain = host, RemotePort = port, Id = id, BufferSize = sForwardServerConfigTransfer.BufferSize })
                }).ConfigureAwait(false);
            }
            return false;
        }
        /// <summary>
        /// 服务器收到tcp连接
        /// </summary>
        /// <param name="port"></param>
        /// <param name="id"></param>
        /// <returns></returns>
        private async Task<bool> TunnelConnect(int port, ulong id)
        {
            //发给对应的客户端
            if (sForwardServerCahing.TryGet(port, out string machineId) && signCaching.TryGet(machineId, out SignCacheInfo sign) && sign.Connected)
            {
                return await sender.SendOnly(new MessageRequestWrap
                {
                    Connection = sign.Connection,
                    MessengerId = (ushort)SForwardMessengerIds.Proxy,
                    Payload = MemoryPackSerializer.Serialize(new SForwardProxyInfo { RemotePort = port, Id = id, BufferSize = sForwardServerConfigTransfer.BufferSize })
                }).ConfigureAwait(false);
            }
            return false;
        }
        /// <summary>
        /// 服务器收到udp数据
        /// </summary>
        /// <param name="port"></param>
        /// <param name="id"></param>
        /// <returns></returns>
        private async Task<bool> UdpConnect(int port, ulong id)
        {
            //发给对应的客户端
            if (sForwardServerCahing.TryGet(port, out string machineId) && signCaching.TryGet(machineId, out SignCacheInfo sign) && sign.Connected)
            {
                return await sender.SendOnly(new MessageRequestWrap
                {
                    Connection = sign.Connection,
                    MessengerId = (ushort)SForwardMessengerIds.ProxyUdp,
                    Payload = MemoryPackSerializer.Serialize(new SForwardProxyInfo { RemotePort = port, Id = id, BufferSize = sForwardServerConfigTransfer.BufferSize })
                }).ConfigureAwait(false);
            }
            return false;
        }

        private bool PortRange(string str, out int min, out int max)
        {
            min = 0; max = 0;
            string[] arr = str.Split('/');
            return arr.Length == 2 && int.TryParse(arr[0], out min) && int.TryParse(arr[1], out max);
        }
    }

    /// <summary>
    /// 服务器穿透客户端
    /// </summary>
    public sealed class SForwardClientMessenger : IMessenger
    {
        private readonly SForwardProxy proxy;
        private readonly RunningConfig runningConfig;
        private readonly SForwardTransfer sForwardTransfer;

        public SForwardClientMessenger(SForwardProxy proxy, RunningConfig runningConfig, SForwardTransfer sForwardTransfer)
        {
            this.proxy = proxy;
            this.runningConfig = runningConfig;
            this.sForwardTransfer = sForwardTransfer;
        }

        /// <summary>
        /// 别人来获取穿透记录
        /// </summary>
        /// <param name="connection"></param>
        [MessengerId((ushort)SForwardMessengerIds.Get)]
        public void Get(IConnection connection)
        {
            List<SForwardInfo> result = sForwardTransfer.Get();
            connection.Write(MemoryPackSerializer.Serialize(result));
        }
        /// <summary>
        /// 添加
        /// </summary>
        /// <param name="connection"></param>
        [MessengerId((ushort)SForwardMessengerIds.AddClient)]
        public void AddClient(IConnection connection)
        {
            SForwardInfo sForwardInfo = MemoryPackSerializer.Deserialize<SForwardInfo>(connection.ReceiveRequestWrap.Payload.Span);
            sForwardTransfer.Add(sForwardInfo);
        }
        // <summary>
        /// 删除
        /// </summary>
        /// <param name="connection"></param>
        [MessengerId((ushort)SForwardMessengerIds.RemoveClient)]
        public void RemoveClient(IConnection connection)
        {
            uint id = MemoryPackSerializer.Deserialize<uint>(connection.ReceiveRequestWrap.Payload.Span);
            sForwardTransfer.Remove(id);
        }
        // <summary>
        /// 删除
        /// </summary>
        /// <param name="connection"></param>
        [MessengerId((ushort)SForwardMessengerIds.TestClient)]
        public void TestClient(IConnection connection)
        {
            sForwardTransfer.TestLocal();
        }


        /// <summary>
        /// 收到服务器发来的连接
        /// </summary>
        /// <param name="connection"></param>
        [MessengerId((ushort)SForwardMessengerIds.Proxy)]
        public void Proxy(IConnection connection)
        {
            SForwardProxyInfo sForwardProxyInfo = MemoryPackSerializer.Deserialize<SForwardProxyInfo>(connection.ReceiveRequestWrap.Payload.Span);

            //是http
            if (string.IsNullOrWhiteSpace(sForwardProxyInfo.Domain) == false)
            {
                SForwardInfo sForwardInfo = runningConfig.Data.SForwards.FirstOrDefault(c => c.Domain == sForwardProxyInfo.Domain);
                if (sForwardInfo != null)
                {
                    _ = proxy.OnConnectTcp(sForwardProxyInfo.BufferSize, sForwardProxyInfo.Id, new System.Net.IPEndPoint(connection.Address.Address, sForwardProxyInfo.RemotePort), sForwardInfo.LocalEP);
                }
            }
            //是tcp
            else if (sForwardProxyInfo.RemotePort > 0)
            {
                IPEndPoint localEP = GetLocalEP(sForwardProxyInfo);
                if (localEP != null)
                {
                    IPEndPoint server = new IPEndPoint(connection.Address.Address, sForwardProxyInfo.RemotePort);
                    _ = proxy.OnConnectTcp(sForwardProxyInfo.BufferSize, sForwardProxyInfo.Id, server, localEP);
                }
            }
        }

        /// <summary>
        /// 收到服务器发来的udp请求
        /// </summary>
        /// <param name="connection"></param>
        [MessengerId((ushort)SForwardMessengerIds.ProxyUdp)]
        public void ProxyUdp(IConnection connection)
        {
            SForwardProxyInfo sForwardProxyInfo = MemoryPackSerializer.Deserialize<SForwardProxyInfo>(connection.ReceiveRequestWrap.Payload.Span);
            if (sForwardProxyInfo.RemotePort > 0)
            {
                IPEndPoint localEP = GetLocalEP(sForwardProxyInfo);
                if (localEP != null)
                {
                    IPEndPoint server = new IPEndPoint(connection.Address.Address, sForwardProxyInfo.RemotePort);
                    _ = proxy.OnConnectUdp(sForwardProxyInfo.BufferSize, sForwardProxyInfo.Id, server, localEP);
                }
            }
        }

        /// <summary>
        /// 获取这个连接请求对应的本机服务
        /// </summary>
        /// <param name="sForwardProxyInfo"></param>
        /// <returns></returns>
        private IPEndPoint GetLocalEP(SForwardProxyInfo sForwardProxyInfo)
        {
            SForwardInfo sForwardInfo = runningConfig.Data.SForwards.FirstOrDefault(c => c.RemotePort == sForwardProxyInfo.RemotePort || (c.RemotePortMin <= sForwardProxyInfo.RemotePort && c.RemotePortMax >= sForwardProxyInfo.RemotePort));
            if (sForwardInfo != null)
            {
                IPEndPoint localEP = IPEndPoint.Parse(sForwardInfo.LocalEP.ToString());
                if (sForwardInfo.RemotePortMin != 0 && sForwardInfo.RemotePortMax != 0)
                {
                    uint plus = (uint)(sForwardProxyInfo.RemotePort - sForwardInfo.RemotePortMin);
                    uint newIP = NetworkHelper.IP2Value(localEP.Address) + plus;
                    localEP.Address = NetworkHelper.Value2IP(newIP);
                }
                return localEP;
            }
            return null;
        }

    }


    [MemoryPackable]
    public sealed partial class SForwardAddForwardInfo
    {
        public string MachineId { get; set; }
        public SForwardInfo Data { get; set; }
    }
    [MemoryPackable]
    public sealed partial class SForwardRemoveForwardInfo
    {
        public string MachineId { get; set; }
        public uint Id { get; set; }
    }
}
