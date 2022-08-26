using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SimplestLoadBalancer
{
    static class Extensions
    {
        public static IEnumerable<int> Enumerate(this (int from, int to) range) => Enumerable.Range(range.from, range.to - range.from + 1);

        static readonly Random rand = new Random();
        public static K Random<K, V>(this IDictionary<K, (byte weight, V)> items)
        {
            var n = rand.Next(0, items.Values.Sum(v => v.weight));
            return items.FirstOrDefault(kv => (n -= kv.Value.weight) < 0).Key;
        }

        public static void SendVia(this IPEndPoint backend, UdpClient client, byte[] packet, AsyncCallback cb) =>
            client.BeginSend(packet, packet.Length, backend, cb, null);

        public static IEnumerable<IPAddress> Private(this NetworkInterface[] interfaces) =>
            interfaces.Where(i => i.OperationalStatus == OperationalStatus.Up)
                .Where(i => i.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(i => i.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Where(a => IPNetwork.IsIANAReserved(a.Address))
                .Select(a => a.Address);

        public const int SIO_UDP_CONNRESET = -1744830452;
        public static UdpClient Configure(this UdpClient client)
        {
            client.DontFragment = true;
//            client.Client.IOControl((IOControlCode)SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null); // don't throw on disconnect
            return client;
        }
    }
    static class Program
    {
        static long received = 0L;
        static long relayed = 0L;
        static long responded = 0L;

        /// <summary>
        /// Sessionless UDP Load Balancer sends packets to targets without session affinity.
        /// </summary>
        /// <param name="serverPortRange">Set the ports to listen to and forward to backend targets (default "1812-1813")</param>
        /// <param name="adminPort">Set the port that targets will send watchdog events (default 1111)</param>
        /// <param name="clientTimeout">Seconds to allow before cleaning-up idle clients (default 30)</param>
        /// <param name="targetTimeout">Seconds to allow before removing target missing watchdog events (default 30)</param>
        /// <param name="defaultTargetWeight">Weight to apply to targets when not specified (default 100)</param>
        /// <param name="unwise">Allows public IP addresses for targets (default is to only allow private IPs)</param>
        /// <param name="statsPeriodMs">Sets the number of milliseconds between statistics messages printed to the console (default 500, disable 0, max 65535)</param>
        /// <param name="defaultGroupId">Sets the group ID to assign to backends that when a registration packet doesn't include one, and when port isn't assigned a group (default 0)</param>
        static async Task Main(string serverPortRange = "1812-1813", int adminPort = 1111, uint clientTimeout = 30, uint targetTimeout = 30, byte defaultTargetWeight = 100, bool unwise = false, ushort statsPeriodMs = 1000, byte defaultGroupId = 0)
        {
            var ports = serverPortRange.Split("-", StringSplitOptions.RemoveEmptyEntries) switch {
                string[] a when a.Length == 1 => new[] { int.Parse(a[0]) },
                string[] a when a.Length == 2 => (from: int.Parse(a[0]), to: int.Parse(a[1])).Enumerate().ToArray(),
                _ => throw new Exception($"Invalid server port range: {serverPortRange}.")
            };

            await Console.Out.WriteLineAsync($"{DateTime.UtcNow:s}: Welcome to the simplest UDP Load Balancer.  Hit Ctrl-C to Stop.");

            var admin_ip = NetworkInterface.GetAllNetworkInterfaces().Private().First();
            await Console.Out.WriteLineAsync($"{DateTime.UtcNow:s}: The server port range is {serverPortRange} ({ports.Length} port{(ports.Length > 1 ? "s" : "")}).");
            await Console.Out.WriteLineAsync($"{DateTime.UtcNow:s}: The watchdog endpoint is {admin_ip}:{adminPort}.");
            await Console.Out.WriteLineAsync($"{DateTime.UtcNow:s}: Timeouts are: {clientTimeout}s for clients, and {targetTimeout}s  for targets.");
            await Console.Out.WriteLineAsync($"{DateTime.UtcNow:s}: {(unwise ? "*WARNING* " : string.Empty)}"
                + $"Targets with public IPs {(unwise ? "WILL BE" : "will NOT be")} allowed.");

            using var cts = new CancellationTokenSource();

            Console.CancelKeyPress += (s, a) =>
            {
                Console.Out.WriteLine($"{DateTime.UtcNow:s}: Beginning shutdown procedure.");
                cts.Cancel();
                a.Cancel = true;
            };

            // helper to run tasks with cancellation
            Task run(Func<Task> func, string name)
            {
                return Task.Run(async () =>
                {
                    var ct = cts.Token;
                    while (!ct.IsCancellationRequested)
                    {
                        try
                        {
                            await func();
                        }
                        catch (Exception e)
                        {
                            await Console.Out.WriteLineAsync($"{DateTime.UtcNow:s}: *ERROR* Task {name} encountered a problem: {e.Message}");
                            await Task.Delay(100); // slow fail
                        }
                    }
                    await Console.Out.WriteLineAsync($"{DateTime.UtcNow:s}: {name} is done.");
                });
            }

            var backend_groups = new ConcurrentDictionary<byte, ConcurrentDictionary<IPAddress, (byte weight, DateTime seen)>>();
            var port_group_map = new ConcurrentDictionary<int, byte>(ports.ToDictionary(p => p, p => defaultGroupId));

            var clients = new ConcurrentDictionary<(IPEndPoint remote, int external_port), (UdpClient internal_client, DateTime seen)>();
            var servers = ports.ToDictionary(p => p, p => new UdpClient(p).Configure());
            var stations = new ConcurrentDictionary<string, (IPEndPoint backend, DateTime seen)>();

            // helper to extract the Calling-Station-Id from a RADIUS packet
            (string called, string calling) get_station(Memory<byte> buffer) {
                string called = "unknown", calling = "unknown";
                if (buffer.Length > 22) { 
                    buffer = buffer.Slice(20);
                    while (buffer.Length > 0 && buffer.Length >= buffer.Span[1]) {
                        switch (buffer.Span[0]) { 
                            case 30: called = Encoding.UTF8.GetString(buffer.Slice(2, buffer.Span[1] - 2).Span); break;
                            case 31: calling = Encoding.UTF8.GetString(buffer.Slice(2, buffer.Span[1] - 2).Span); break;
                        }
                        buffer = buffer.Slice(buffer.Span[1]);
                    }
                }
                return (called, calling);
            }

            // helper to get requests (inbound packets from external sources) asyncronously
            async IAsyncEnumerable<(UdpReceiveResult result, int port)> requests()
            {
                foreach (var s in servers)
                    if (s.Value.Available > 0)
                        yield return (await s.Value.ReceiveAsync(), s.Key);
            }

            // task to listen on the server port and relay packets to random backends via a client-specific internal port
            async Task relay()
            {
                var any = false;
                await foreach(var (request, port) in requests()) {
                    Interlocked.Increment(ref received);

                    var client = clients.AddOrUpdate((request.RemoteEndPoint, port), ep => (new UdpClient().Configure(), DateTime.UtcNow), (ep, c) => (c.internal_client, DateTime.UtcNow));
                    var station = get_station(request.Buffer);
                    if (backend_groups.TryGetValue(port_group_map[port], out var group)) {
                        var session = group.Any() ? stations.AddOrUpdate($"{station.called}-{station.calling}-{port}", csid => (new IPEndPoint(group.Random(), port), DateTime.UtcNow), (csid, s) => (s.backend, DateTime.UtcNow)) : (null, DateTime.UtcNow);
                        session.backend?.SendVia(client.internal_client, request.Buffer, s => Interlocked.Increment(ref relayed));
                    }
                    any = true;
                }
                if (any) await Task.Delay(10); // slack the loop
            }

            // helper to get replies asyncronously
            async IAsyncEnumerable<(UdpReceiveResult result, IPEndPoint ep, int port)> replies()
            {
                foreach (var c in clients)
                    if (c.Value.internal_client.Available > 0)
                        yield return (await c.Value.internal_client.ReceiveAsync(), c.Key.remote, c.Key.external_port);
            }

            // task to listen for responses from backends and re-send them to the correct external client
            async Task reply()
            {
                var any = false;
                await foreach (var (result, ep, port) in replies())
                {
                    servers[port].BeginSend(result.Buffer, result.Buffer.Length, ep, s => Interlocked.Increment(ref responded), null);
                    any = true;
                }
                if (!any) await Task.Delay(10); // slack the loop
            }

            // task to listen for instances asking to add/remove themselves as a target (watch-dog pattern)
            using var control = new UdpClient(new IPEndPoint(admin_ip, adminPort)).Configure();
            async Task admin()
            {
                if (control.Available > 0)
                {
                    var packet = await control.ReceiveAsync();
                    var payload = new ArraySegment<byte>(packet.Buffer);

                    var header = BitConverter.ToInt16(payload.Slice(0, 2));

                    (IPAddress ip, byte weight, byte group_id) get_ip_weight_and_group()
                    {
                        var ip = new IPAddress(payload.Slice(2, 4));
                        if (ip.Equals(IPAddress.Any)) ip = packet.RemoteEndPoint.Address;
                        
                        var weight = payload.Count > 8 ? payload[8] : defaultTargetWeight;
                        
                        var group_id = payload.Count > 9 ? payload[9] : defaultGroupId;
                        return (ip, weight, group_id);
                    }

                    switch (header)
                    {
                        case 0x1166: {
                            if (packet.Buffer.Length == 5) {
                                var port = BitConverter.ToInt16(payload.Slice(2, 2)); // bytes [2] and [3]
                                var group = payload[4];
                                port_group_map.AddOrUpdate(port, port => group, (port, group) => group);
                            } else await Console.Out.WriteLineAsync($"{DateTime.UtcNow:s}: Invalid port group registration message (length {packet.Buffer.Length} != 5).");
                        } break;
                        
                        case 0x1111: {
                            (var ip, var weight, var group_id) = get_ip_weight_and_group();
                            if (unwise || IPNetwork.IsIANAReserved(ip))
                            {
                                var group = backend_groups.AddOrUpdate(group_id, id => new(), (id, group) => group);
                                if (group != null) {
                                    if (weight > 0) {
                                        group.AddOrUpdate(ip, ip => (weight, DateTime.UtcNow), (ep, d) => (weight, DateTime.UtcNow));
                                        await Console.Out.WriteLineAsync($"{DateTime.UtcNow:s}: Refresh {ip} (weight {weight}, group {group_id}).");
                                    } else await Console.Out.WriteLineAsync($"{DateTime.UtcNow}: Rejected zero-weighted {ip} for group {group_id}.");
                                } else await Console.Out.WriteLineAsync($"${DateTime.UtcNow:s}: Rejected invalid backend group {group_id} for ip {ip}.");
                            } else await Console.Out.WriteLineAsync($"{DateTime.UtcNow:s}: Rejected {ip}.");
                        } break;
                        
                        case 0x1186: {// see AIEE No. 26
                            (var ip, var weight, var group_id)  = get_ip_weight_and_group();
                            if (backend_groups.TryGetValue(group_id, out var group))
                                group.Remove(ip, out var seen);
                            await Console.Out.WriteLineAsync($"{DateTime.UtcNow:s}: Remove {ip} from group {group_id}.");
                        } break;
                    }
                } else await Task.Delay(10);
            }

            // task to remove backends and clients we haven't heard from in a while
            async Task prune()
            {
                await Task.Delay(100);
                foreach(var backends in backend_groups.Values) {
                    var remove_backends = backends.Where(kv => kv.Value.seen < DateTime.UtcNow.AddSeconds(-targetTimeout)).Select(kv => kv.Key).ToArray();
                    foreach (var b in remove_backends)
                    {
                        backends.TryRemove(b, out var seen);
                        await Console.Out.WriteLineAsync($"{DateTime.UtcNow:s}: Expired target {b} (last seen {seen:s}).");
                    }
                }
                var remove_clients = clients.Where(kv => kv.Value.seen < DateTime.UtcNow.AddSeconds(-clientTimeout)).Select(kv => kv.Key).ToArray();
                foreach (var c in remove_clients)
                {
                    clients.TryRemove(c, out var info);
                    info.internal_client.Dispose();
                    await Console.Out.WriteLineAsync($"{DateTime.UtcNow:s}: Expired client {c} (last seen {info.seen:s}).");
                }
                var remove_expired_stations = stations.Where(kv => kv.Value.seen < DateTime.UtcNow.AddSeconds(-clientTimeout)).Select(kv => kv.Key).ToArray();
                foreach (var s in remove_expired_stations)
                {
                    stations.TryRemove(s, out var info);
                    await Console.Out.WriteLineAsync($"{DateTime.UtcNow:s}: Expired station {s} (last seen {info.seen:s}).");
                }
                var remove_orphaned_stations = stations.Where(kv => ! backend_groups[port_group_map[kv.Value.backend.Port]].ContainsKey(kv.Value.backend.Address)).Select(kv => kv.Key).ToArray();
                foreach (var s in remove_orphaned_stations)
                {
                    stations.TryRemove(s, out var info);
                    await Console.Out.WriteLineAsync($"{DateTime.UtcNow:s}: Orphaned station {s} (last seen {info.seen:s}).");
                }
            }

            // task to occassionally write statistics to the console
            async Task stats()
            {
                await Console.Out.WriteLineAsync($"{DateTime.UtcNow:s}: {received}/{relayed}/{responded}, {clients.Count} => {backend_groups.Count}/{backend_groups.Sum(g => g.Value.Count)}({backend_groups.Values.SelectMany(g => g).Distinct().Count()})");
                try { await Task.Delay(statsPeriodMs, cts.Token); } catch { } // supress cancel
            }

            var tasks = new[] {
                run(relay, "Relay"),
                run(reply, "Reply"),
                run(admin, "Admin"),
                run(prune, "Prune")
            }.ToList();

            if (statsPeriodMs > 0) 
                tasks.Add(run(stats, "State"));

            await Task.WhenAll(tasks);
            var e = string.Join(", ", tasks.Where(t => t.Exception != null).Select(t => t.Exception.Message));
            await Console.Out.WriteLineAsync($"{DateTime.UtcNow:s}: Bye-now ({(e.Any() ? e : "OK")}).");
        }
    }
}
