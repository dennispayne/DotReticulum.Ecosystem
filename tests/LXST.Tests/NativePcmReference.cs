using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LXST.Tests;

internal static partial class NativePcmReference
{
    internal static float[] Expected(JsonElement linux, string group, int index = 0)
    {
        if (!OperatingSystem.IsWindows())
            return linux.EnumerateArray().Select(value => value.GetSingle()).ToArray();
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "lxst-windows-native.json")));
        Assert.Equal("eba5abc77fef7fef3bcb6c9c091c80b7f7492c9f",
            fixture.RootElement.GetProperty("revision").GetString());
        var baseline = group == "file" ? "lxst-file-vectors.json" : "lxst-vectors.json";
        Assert.Equal(fixture.RootElement.GetProperty(group == "file" ? "file_baseline_sha256" : "baseline_sha256").GetString(),
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", baseline)).ReplaceLineEndings("\n")))));
        foreach (var library in fixture.RootElement.GetProperty("libraries").EnumerateObject())
        {
            if (group == "opus" && library.Name != "libopus-0.dll" ||
                group == "codec2" && library.Name != "libcodec2.dll" ||
                group == "file" && library.Name == "libcodec2.dll") continue;
            var handle = NativeLibrary.Load(library.Name);
            try
            {
                var name = new StringBuilder(32768);
                Assert.NotEqual(0u, GetModuleFileName(handle, name, name.Capacity));
                Assert.Equal(library.Value.GetString(),
                    Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(name.ToString()))));
            }
            finally { NativeLibrary.Free(handle); }
        }
        var row = fixture.RootElement.GetProperty(group);
        if (group != "file") row = row[index];
        var pcm = Convert.FromBase64String(row.GetProperty("pcm_s16le").GetString()!);
        var result = new float[pcm.Length / 2];
        for (var i = 0; i < result.Length; i++)
            result[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 2)) / 32767f;
        Assert.Equal(linux.GetArrayLength(), result.Length);
        return result;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetModuleFileNameW", CharSet = CharSet.Unicode)]
    private static extern uint GetModuleFileName(nint module, StringBuilder path, int size);
}
