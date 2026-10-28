using System.Text.RegularExpressions;

namespace LXST;

public static class AudioDeviceSelection
{
    public static AudioDeviceInfo Resolve(string selector, IReadOnlyList<AudioDeviceInfo> devices, bool includeLoopback = false)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(devices);
        if (selector.Length > 256) throw new ArgumentException("Device selection is limited to 256 characters.", nameof(selector));
        var candidates = devices.Where(device => includeLoopback || !device.Id.Contains("monitor", StringComparison.Ordinal)).ToArray();
        var exact = candidates.FirstOrDefault(device => device.Id == selector);
        if (exact is not null) return exact;
        var names = new Dictionary<string, AudioDeviceInfo>(StringComparer.Ordinal);
        foreach (var device in candidates) names[device.Name] = device;
        var substring = names.Values.FirstOrDefault(device => device.Name.Contains(selector, StringComparison.Ordinal));
        if (substring is not null) return substring;
        var pattern = new Regex("\\A" + string.Join(".*", selector.Select(character => character.ToString())),
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        return names.Values.FirstOrDefault(device => pattern.IsMatch(device.Name)) ??
            throw new KeyNotFoundException($"No audio device matches {selector}.");
    }
}
