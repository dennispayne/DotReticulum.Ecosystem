using System.Runtime.InteropServices;

namespace LXST;

internal static class NativeAudioLibrary
{
    public static nint LoadWindows(string conventionalName, string distributionName)
    {
        if (NativeLibrary.TryLoad(conventionalName, out var library)) return library;
        if (NativeLibrary.TryLoad(distributionName, out library)) return library;
        throw new DllNotFoundException($"Unable to load {conventionalName} or {distributionName}, including their native dependencies.");
    }
}
