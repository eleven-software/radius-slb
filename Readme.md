# RADIUS SLB #

This fork adds session-like load balancing of RADIUS traffic to SLB using RADIUS attribute values to form the session key. 

![bar](udp-slb.jpg)

## Why? ##

RADIUS is a UDP protocol but implements session-like behavior in some variations (e.g. EAP). However, the session affinity isn't best keyed on IP addresses and ports, but the internal contents of the messages.  For EAP message exchanges to complete successfully, all packets from a suplicant must be handled with the knowledge of previous packets in the conversation (i.e. the "state") and in general that means they must be handled by the same backend server.  

This variation of SimplestLoadBalancer uses the RADIUS attributes `Calling-Station-Id`, `Called-Station-Id` and the server port as the key for sessions.  When new key values are seen a random backend is chosen and recorded so that future packets will be sent to the same backend (using the same port).  If there is a lack of messages with the key for the client timeout period, the recorded backend is discarded and a new backend will be chosen if more such packets subsequently arrive. 

## Building ##

This is a very simple .Net Core 6 project, so to build (assuming you have the SDK installed):

```
dotnet build
```

If you'd like to generate a single-file executable for ease of use and deployment, just target the platform you'll be running on. For Linux:

```
dotnet publish -o ./ -c Release -r linux-x64 /p:PublishSingleFile=true /p:PublishTrimmed=true --self-contained
```

Or for Windows:

```
dotnet publish -o ./ -c Release -r win10-x64 /p:PublishSingleFile=true /p:PublishTrimmed=true --self-contained
```

## Usage ##
In a development environment, run using `dotnet run` in the project directory:

```
$ dotnet run
```

If you've built a single-file executable, run with the name of that file:

```
$ ./SimplestLoadBalancer
```

By default the process will listen on port `1812` for any incomming UDP packets.  You can control the ports it listens on with the `--server-port-range` option.  Other options are described in the command help:

```
SimplestLoadBalancer:
  Sessionless UDP Load Balancer sends packets to targets without session affinity.

Usage:
  SimplestLoadBalancer [options]

Options:
  --server-port-range <start_port-end_port>          Set the port range to listen to and forward to backend targets (default 1812)
  --admin-port <admin-port>                          Set the port that targets will send watchdog events (default 1111)
  --client-timeout <client-timeout>                  Seconds to allow before cleaning-up idle clients (default 30)
  --target-timeout <target-timeout>                  Seconds to allow before removing target missing watchdog events (default 30)
  --default-target-weight <default-target-weight>    Weight to apply to targets when not specified (default 100)
  --unwise                                           Allows public IP addresses for targets (default is to only allow private IPs)
  --version                                          Show version information
  -?, -h, --help                                     Show help and usage information
```

In context of the image at the top of this document, the `--server-point-range` corresponds to "E1" (the client-facing external ports).

To add and maintain targets send periodic UDP packets to the admin port (default 1111) on the first private IPv4 address configured on your machine.  The packet format is very simple, consisting of a couple magic bytes to avoid spurrious adds, four bytes for an ipv4 address and two byes for the port number (NOTE: the port number is ignored; packets will be sent to the same port number on the backend as they arrived on) to target. 

```
0x11 0x11 [four bytes for ip] [two bytes (unused)] [one byte for weight (optional)]
```

A value for `weight` is optional in such packets, and if not in the packet the value specified at the command line will be used.  Weights are applied in the traditional manner (relative probability of a backend being chosen).  

Each time such a packet is received the backend's "last seen" time is updated. If 30 seconds passes without a backend being seen, it is removed. To immeadiately remove a target send a packet with 0x86 as the first byte instead of 0x11:

```
0x86 0x11 [four bytes for ip] [two bytes (unused)]
```

Using Linux bash, it's easy to send those packets to `/dev/udp/[admin ip]/[admin port]`. For example, if your load balancer is listening on the default admin port `1111` at `192.168.1.11`, and you want to add a target with the IP `192.168.1.22` and port number `1812`:

```bash
$ echo -e $(echo  "\x11\x11$(echo "192.168.1.22" | tr "." "\n" | xargs printf '\\x%02X')\x14\x07") > /dev/udp/192.168.1.11/1111
```
To use a different port on the target, just change the `\x14\07` to the little endian hex representation of port number of your choice.

It can be tedius to manually send those packets and keep a target registered. A simple way to maintain a set of IPs as targets for testing is to create a small shell script, say `lb.sh`:

```bash
#!/bin/bash
echo -ne $(echo  "\x11\x11$(echo "192.168.1.22" | tr "." "\n" | xargs printf '\\x%02X')\x14\x07") > /dev/udp/192.168.1.11/1111
echo -ne $(echo  "\x11\x11$(echo "192.168.1.23" | tr "." "\n" | xargs printf '\\x%02X')\x14\x07") > /dev/udp/192.168.1.11/1111
echo -ne $(echo  "\x11\x11$(echo "192.168.1.24" | tr "." "\n" | xargs printf '\\x%02X')\x14\x07") > /dev/udp/192.168.1.11/1111
echo -ne $(echo  "\x11\x11$(echo "192.168.1.25" | tr "." "\n" | xargs printf '\\x%02X')\x14\x07") > /dev/udp/192.168.1.11/1111
echo -ne $(echo  "\x11\x11$(echo "192.168.1.26" | tr "." "\n" | xargs printf '\\x%02X')\x14\x07") > /dev/udp/192.168.1.11/1111
echo -ne $(echo  "\x11\x11$(echo "192.168.1.27" | tr "." "\n" | xargs printf '\\x%02X')\x14\x07") > /dev/udp/192.168.1.11/1111
```

And then use the `watch` command to call that script every few seconds:

```bash
$ watch -n10 ./lb.sh
```

Enjoy!
