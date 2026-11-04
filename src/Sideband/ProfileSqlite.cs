using System.Reflection;
using System.Runtime.InteropServices;

namespace Sideband;

// Only private, bounded snapshot copies are opened. No SQLite handle ever opens the source profile.
internal sealed class ProfileSqlite : IDisposable
{
    private const int MaximumCellBytes = 8 * 1024 * 1024;
    private IntPtr _database;
    private readonly CancellationToken _token;
    private readonly Native.Progress _progress;
    private int _remainingSteps = 500_000;

    internal ProfileSqlite(string path, CancellationToken token, bool verifyIntegrity = true, bool immutable = false)
    {
        _token = token;
        _progress = _ => token.IsCancellationRequested || --_remainingSteps <= 0 ? 1 : 0;
        var windows = OperatingSystem.IsWindows();
        var fullPath = Path.GetFullPath(path);
        if (windows && !fullPath.StartsWith(@"\\?\", StringComparison.Ordinal))
            fullPath = fullPath.StartsWith(@"\\", StringComparison.Ordinal)
                ? @"\\?\UNC\" + fullPath[2..] : @"\\?\" + fullPath;
        var filename = immutable
            ? (windows ? "file:" + Uri.EscapeDataString(fullPath) : new Uri(fullPath).AbsoluteUri) + "?immutable=1"
            : fullPath;
        var result = Native.sqlite3_open_v2(filename, out _database, immutable ? 1 | 0x40 : 1,
            windows ? "win32-longpath" : null);
        if (result != 0)
        {
            Dispose();
            throw new InvalidDataException($"Cannot open the profile SQLite snapshot (SQLite result {result}).");
        }
        try
        {
            Native.sqlite3_limit(_database, 0, MaximumCellBytes);
            Native.sqlite3_busy_timeout(_database, 1_000);
            Native.sqlite3_progress_handler(_database, 100, _progress, IntPtr.Zero);
            Execute("PRAGMA trusted_schema=OFF");
            Execute("PRAGMA query_only=ON");
            Execute("PRAGMA cache_size=-2048");
            Execute("BEGIN");
            if (verifyIntegrity)
            {
                var check = Rows("PRAGMA quick_check", 1).Single();
                if (check[0] is not string text || text != "ok")
                    throw new InvalidDataException("The profile SQLite snapshot failed its integrity check.");
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal List<object?[]> Rows(string sql, int maximumRows)
    {
        _token.ThrowIfCancellationRequested();
        if (Native.sqlite3_prepare_v2(_database, sql, -1, out var statement, IntPtr.Zero) != 0)
            throw new InvalidDataException("The profile database schema or query is unsupported.");
        try
        {
            var rows = new List<object?[]>();
            long totalBytes = 0;
            while (true)
            {
                _token.ThrowIfCancellationRequested();
                var result = Native.sqlite3_step(statement);
                if (result == 101) return rows;
                if (result != 100) throw new InvalidDataException("The profile SQLite snapshot could not be read within its safety limits.");
                if (rows.Count >= maximumRows) throw new InvalidDataException("The profile exceeds its row safety limit.");
                var row = new object?[Native.sqlite3_column_count(statement)];
                for (var index = 0; index < row.Length; index++)
                {
                    var type = Native.sqlite3_column_type(statement, index);
                    if (type is 3 or 4)
                    {
                        var length = Native.sqlite3_column_bytes(statement, index);
                        totalBytes += length;
                        if (length < 0 || length > MaximumCellBytes || totalBytes > 64L * 1024 * 1024)
                            throw new InvalidDataException("The profile exceeds its cell or allocation safety limit.");
                        var pointer = Native.sqlite3_column_blob(statement, index);
                        if (length > 0 && pointer == IntPtr.Zero)
                            throw new InvalidDataException("SQLite could not allocate the profile cell.");
                        var bytes = new byte[length];
                        if (length > 0) Marshal.Copy(pointer, bytes, 0, length);
                        row[index] = type == 3 ? new System.Text.UTF8Encoding(false, true).GetString(bytes) : bytes;
                    }
                    else row[index] = type switch
                    {
                        1 => Native.sqlite3_column_int64(statement, index),
                        2 => Native.sqlite3_column_double(statement, index),
                        5 => null,
                        _ => throw new InvalidDataException("Unknown SQLite value type."),
                    };
                }
                rows.Add(row);
            }
        }
        finally { Native.sqlite3_finalize(statement); }
    }

    private void Execute(string sql) => _ = Rows(sql, 0);

    public void Dispose()
    {
        if (_database != IntPtr.Zero)
        {
            Native.sqlite3_close_v2(_database);
            _database = IntPtr.Zero;
        }
        GC.KeepAlive(_progress);
    }

    private static class Native
    {
        private const string Library = "sideband_profile_sqlite";
        static Native() => NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, Resolve);
        private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? path) =>
            name != Library ? IntPtr.Zero : NativeLibrary.Load(
                OperatingSystem.IsWindows() ? "winsqlite3.dll" :
                OperatingSystem.IsLinux() ? "libsqlite3.so.0" :
                OperatingSystem.IsMacOS() ? "libsqlite3.dylib" :
                throw new PlatformNotSupportedException("Profile import requires Windows SQLite or system libsqlite3."), assembly, path);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int Progress(IntPtr argument);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr database, int flags,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string? vfs);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_close_v2(IntPtr database);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_limit(IntPtr database, int category, int value);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_busy_timeout(IntPtr database, int milliseconds);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void sqlite3_progress_handler(IntPtr database, int instructions, Progress progress, IntPtr argument);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_prepare_v2(IntPtr database, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int bytes, out IntPtr statement, IntPtr tail);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_step(IntPtr statement);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_finalize(IntPtr statement);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_column_count(IntPtr statement);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_column_type(IntPtr statement, int column);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_column_bytes(IntPtr statement, int column);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr sqlite3_column_blob(IntPtr statement, int column);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern long sqlite3_column_int64(IntPtr statement, int column);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern double sqlite3_column_double(IntPtr statement, int column);
    }
}
