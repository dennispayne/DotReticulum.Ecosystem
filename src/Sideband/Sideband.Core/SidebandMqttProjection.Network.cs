using System.Globalization;
using System.Numerics;
using V = Sideband.SidebandTelemetryValue;
using K = Sideband.SidebandTelemetryValueKind;

namespace Sideband;

public static partial class SidebandMqtt
{
    private static void Transport(ProjectionWriter output, string topic, V data)
    {
        var interfaces = new Dictionary<string, V>(StringComparer.Ordinal);
        if (data.Get("ifstats") is { IsNil: false } statistics)
            foreach (var item in Required(statistics, "interfaces").Items)
                interfaces[Required(item, "name").Text ?? throw new FormatException("Invalid transport interface name.")] = item;
        var paths = data.Get("path_table") is { IsNil: false } table ? table.Items : [];
        var knownPaths = paths.Where(path => interfaces.ContainsKey(Required(path, "interface").Text ?? "")).ToArray();
        output.Fields(topic, data, "transport_enabled");
        output.Add(topic + "/transport_identity", Hash(Required(data, "transport_identity")));
        output.Fields(topic, data,
            "transport_uptime traffic_rxb traffic_txb speed_rx speed_tx speed_rx_inst speed_tx_inst memory_used");
        output.Add(topic + "/path_count", data.Get("path_count") ?? V.From(0L));
        output.Fields(topic, data, "link_count interface_count");
        output.Add(topic + "/remote_transport_node_count", V.From((long)knownPaths
            .Select(path => Convert.ToHexString(Required(path, "via").Encode())).Distinct(StringComparer.Ordinal).Count()));
        foreach (var (name, value) in interfaces)
        {
            var prefix = topic + "/interfaces/" + HashPath(Required(value, "hash"));
            output.Add(prefix + "/name", V.From(name));
            output.Add(prefix + "/short_name", Required(value, "short_name"));
            output.Add(prefix + "/up", Required(value, "status"));
            output.Add(prefix + "/mode", V.From(Required(value, "mode").Number switch
            {
                1 => "full", 2 => "point_to_point", 3 => "access_point", 4 => "roaming",
                5 => "boundary", 6 => "gateway", _ => "unknown"
            }));
            output.Fields(prefix, value, "type bitrate rxs txs rxb txb");
            output.Add(prefix + "/ifac_signature", Hash(Required(value, "ifac_signature")));
            output.Fields(prefix, value,
                "ifac_size ifac_netname incoming_announce_frequency outgoing_announce_frequency held_announces");
            var interfacePaths = knownPaths.Where(path => Required(path, "interface").Text == name).ToArray();
            output.Add(prefix + "/path_count", V.From((long)interfacePaths.Length));
            foreach (var path in interfacePaths)
            {
                var pathPrefix = prefix + "/paths/" + HashPath(Required(path, "via")) + "/" + HashPath(Required(path, "hash"));
                output.Fields(pathPrefix, path, "hops timestamp expires interface");
            }
        }
    }

    private static void Propagation(ProjectionWriter output, string topic, V data)
    {
        output.Add(topic + "/identity_hash", Hash(Required(data, "identity_hash")));
        output.Fields(topic, data, "uptime");
        foreach (var limit in new[] { "delivery_limit", "propagation_limit" })
        {
            var value = Required(data, limit);
            output.Add(topic + "/" + limit, Integral(value, out var integer)
                ? V.From((integer * 1000).ToString(CultureInfo.InvariantCulture)) : V.From(Number(value) * 1000));
        }
        output.Fields(topic, data, "autopeer_maxdepth from_static_only");
        var store = Required(data, "messagestore");
        foreach (var key in new[] { "count", "bytes" })
            output.Add(topic + "/messagestore_" + key, Required(store, key));
        output.Add(topic + "/messagestore_free", Difference(Required(store, "limit"), Required(store, "bytes")));
        output.Add(topic + "/messagestore_limit", Required(store, "limit"));
        var storeLimit = Number(Required(store, "limit"));
        if (storeLimit == 0) throw new FormatException("Primary propagation percentage divides by zero limit.");
        output.Add(topic + "/messagestore_pct", V.From(SidebandTelemetryCodec.RoundBinaryDecimal(
            Math.Min(Number(Required(store, "bytes")) / storeLimit * 100, 100), 2)));
        output.Fields(topic, Required(data, "clients"), "client_propagation_messages_received client_propagation_messages_served");
        output.Fields(topic, data, "unpeered_propagation_incoming unpeered_propagation_rx_bytes static_peers total_peers");
        var peers = Required(data, "peers").Members;
        var active = peers.Count(peer => Required(peer.Value, "alive") is var alive && (alive.Boolean == true || alive.Number == 1));
        output.Add(topic + "/active_peers", V.From((long)active));
        output.Add(topic + "/unreachable_peers", Difference(Required(data, "total_peers"), V.From((long)active)));
        output.Fields(topic, data, "max_peers");
        var totals = new Dictionary<string, List<V>>(StringComparer.Ordinal)
        {
            ["rx_bytes"] = [], ["tx_bytes"] = [], ["offered"] = [],
            ["outgoing"] = [], ["incoming"] = [], ["unhandled"] = []
        };
        var maxUnhandled = V.From(0L);
        foreach (var peer in peers)
        {
            var prefix = topic + "/peers/" + HashPath(peer.Key);
            var value = peer.Value;
            output.Fields(prefix, value,
                "type state alive last_heard next_sync_attempt last_sync_attempt sync_backoff peering_timebase ler str transfer_limit network_distance");
            foreach (var key in new[] { "rx_bytes", "tx_bytes" })
            {
                var metric = Required(value, key);
                output.Add(prefix + "/" + key, metric);
                totals[key].Add(metric);
            }
            foreach (var key in new[] { "offered", "outgoing", "incoming", "unhandled" })
            {
                var metric = Required(Required(value, "messages"), key);
                output.Add(prefix + "/messages_" + key, metric);
                totals[key].Add(metric);
                if (key == "unhandled" && Number(metric) > Number(maxUnhandled)) maxUnhandled = metric;
            }
        }
        foreach (var (key, metrics) in totals)
        {
            var integerTotal = BigInteger.Zero;
            var integral = true;
            foreach (var metric in metrics)
            {
                if (Integral(metric, out var integer)) integerTotal += integer;
                else { integral = false; break; }
            }
            output.Add(topic + "/peered_propagation_" + key, integral
                ? V.From(integerTotal.ToString(CultureInfo.InvariantCulture)) : V.From(metrics.Sum(Number)));
        }
        output.Add(topic + "/peered_propagation_max_unhandled", maxUnhandled);
    }
}
