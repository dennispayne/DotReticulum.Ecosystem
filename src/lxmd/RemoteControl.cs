using System.Globalization;
using DotReticulum.Applications;
using DotReticulum.Core;

namespace Lxmd;

internal static class RemoteControl
{
    public static async Task<int> RunAsync(string directory, string? rnsDirectory, string? identityPath,
        string? remote, string? sync, string? unpeer, bool showStatus, bool showPeers, double? seconds,
        TextWriter output, TextWriter error, CancellationToken stop)
    {
        byte[]? peer;
        byte[]? target;
        try
        {
            peer = sync is not null ? ParseHash(sync) : unpeer is not null ? ParseHash(unpeer) : null;
            target = remote is not null ? ParseHash(remote) : null;
        }
        catch (FormatException ex) { await error.WriteLineAsync(ex.Message).ConfigureAwait(false); return 203; }

        if (identityPath is null && !Directory.Exists(directory))
        {
            await error.WriteLineAsync("Specified configuration directory does not exist, exiting now").ConfigureAwait(false);
            return 201;
        }
        var path = identityPath ?? Path.Combine(directory, "identity");
        if (!File.Exists(path))
        {
            await error.WriteLineAsync("Identity file not found in specified configuration directory, exiting now").ConfigureAwait(false);
            return 202;
        }
        Identity identity;
        try { identity = IdentityFile.Load(path); }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidDataException)
        {
            await error.WriteLineAsync($"Could not load the Primary Identity: {ex.Message}").ConfigureAwait(false);
            return 4;
        }
        using (identity)
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop))
        {
            var timeout = TimeSpan.FromSeconds(seconds ?? (showStatus || showPeers ? 5 : 15));
            deadline.CancelAfter(timeout);
            try
            {
                await using var node = ReticulumNode.Open(rnsDirectory);
                await node.StartAsync(deadline.Token).ConfigureAwait(false);
                using var remoteIdentity = target is null ? null :
                    await node.ResolveIdentityAsync(target, timeout, deadline.Token).ConfigureAwait(false)
                    ?? throw new TimeoutException("Resolving remote identity timed out.");
                var controlHash = Destination.ComputeHash((remoteIdentity ?? identity).Hash, "lxmf", ["propagation", "control"]);
                var connected = await ClientLinks.ConnectAsync(node, controlHash, timeout, deadline.Token).ConfigureAwait(false);
                var link = connected.Link ?? throw new TimeoutException(connected.Error);
                try
                {
                    link.Identify(identity);
                    var requestPath = showStatus || showPeers ? "/pn/get/stats" : sync is not null ? "/pn/peer/sync" : "/pn/peer/unpeer";
                    var receipt = link.Request(requestPath, peer, timeout: timeout.TotalSeconds, maxResponseSize: 4 * 1024 * 1024,
                        cancellationToken: deadline.Token, useApplicationValues: true) ?? throw new IOException("Control link closed.");
                    var response = await receipt.ResponseTask.WaitAsync(deadline.Token).ConfigureAwait(false);
                    var failure = response switch
                    {
                        null => (207, "Empty response received"),
                        long code => code switch
                        {
                            0xf0 => (203, "Remote received no identity"),
                            0xf1 => (204, "Access denied"),
                            0xf4 => (205, "Invalid data received by remote"),
                            0xfd => (206, "The requested peer was not found"),
                            _ => (0, ""),
                        },
                        _ => (0, ""),
                    };
                    if (failure.Item1 != 0)
                    {
                        await output.WriteLineAsync(failure.Item2).ConfigureAwait(false);
                        return failure.Item1;
                    }
                    if (showStatus || showPeers)
                    {
                        if (response is not Dictionary<object, object?> stats) throw new InvalidDataException("Invalid statistics response.");
                        await PrintStatsAsync(stats, showStatus, showPeers, output).ConfigureAwait(false);
                    }
                    else
                        await output.WriteLineAsync(sync is not null ? $"Sync requested for peer <{sync}>" : $"Broke peering with <{unpeer}>").ConfigureAwait(false);
                    return 0;
                }
                finally { link.Teardown(); }
            }
            catch (Exception ex) when (ex is TimeoutException || ex is OperationCanceledException && !stop.IsCancellationRequested)
            {
                await error.WriteLineAsync("Requesting lxmd control operation timed out, exiting now").ConfigureAwait(false);
                return 200;
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return 0; }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
            {
                await error.WriteLineAsync($"lxmd: {ex.Message}").ConfigureAwait(false);
                return 1;
            }
        }
    }

    private static byte[] ParseHash(string value)
    {
        if (value.Length != 32) throw new FormatException("Destination hash length must be 32 characters.");
        return Convert.FromHexString(value);
    }

    private static async Task PrintStatsAsync(Dictionary<object, object?> stats, bool showStatus, bool showPeers, TextWriter output)
    {
        string Text(Dictionary<object, object?> map, string key) => map.GetValueOrDefault(key) switch
        {
            byte[] hash => $"<{Convert.ToHexStringLower(hash)}>",
            IFormattable value => value.ToString(null, CultureInfo.InvariantCulture),
            { } value => value.ToString() ?? "unknown",
            _ => "unknown",
        };
        await output.WriteLineAsync($"LXMF Propagation Node running on {Text(stats, "destination_hash")}, uptime is {Text(stats, "uptime")}s").ConfigureAwait(false);
        if (showStatus)
        {
            if (stats.GetValueOrDefault("messagestore") is Dictionary<object, object?> store)
                await output.WriteLineAsync($"Messagestore contains {Text(store, "count")} messages, {Text(store, "bytes")} bytes (limit {Text(store, "limit")} bytes)").ConfigureAwait(false);
            await output.WriteLineAsync($"Required propagation stamp cost is {Text(stats, "target_stamp_cost")}, flexibility is {Text(stats, "stamp_cost_flexibility")}").ConfigureAwait(false);
            await output.WriteLineAsync($"Peers: {Text(stats, "total_peers")} total (peer limit is {Text(stats, "max_peers")})").ConfigureAwait(false);
        }
        if (showPeers && stats.GetValueOrDefault("peers") is Dictionary<object, object?> peers)
            foreach (var (key, value) in peers)
                if (key is byte[] hash && value is Dictionary<object, object?> peer)
                    await output.WriteLineAsync($"<{Convert.ToHexStringLower(hash)}> {Text(peer, "type")}: {(peer.GetValueOrDefault("alive") is true ? "Available" : "Unreachable")}, unhandled messages {(peer.GetValueOrDefault("messages") is Dictionary<object, object?> messages ? Text(messages, "unhandled") : "unknown")}").ConfigureAwait(false);
        foreach (var (key, value) in stats)
        {
            if (key is not string name || name == "peers" && !showPeers || name != "peers" && !showStatus) continue;
            await PrintFieldsAsync(name, value, output, 0).ConfigureAwait(false);
        }
    }

    private static async Task PrintFieldsAsync(string name, object? value, TextWriter output, int depth)
    {
        if (depth > 16) throw new InvalidDataException("Control statistics are nested too deeply.");
        if (value is Dictionary<object, object?> map)
        {
            foreach (var (key, item) in map)
            {
                var label = key is byte[] hash ? Convert.ToHexStringLower(hash) : Convert.ToString(key, CultureInfo.InvariantCulture);
                await PrintFieldsAsync($"{name}.{label}", item, output, depth + 1).ConfigureAwait(false);
            }
            if (map.Count == 0) await output.WriteLineAsync($"{name} = {{}}").ConfigureAwait(false);
        }
        else
        {
            var text = value switch
            {
                null => "unknown",
                byte[] bytes => Convert.ToHexStringLower(bytes),
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString(),
            };
            await output.WriteLineAsync($"{name} = {text}").ConfigureAwait(false);
        }
    }
}
