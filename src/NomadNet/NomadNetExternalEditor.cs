using System.Security.Cryptography;
using System.Text;
using DotReticulum.Applications.Rnsh;

namespace NomadNet;

/// <summary>Edits a private working copy; the existing editor owns validation and conflict-safe commit.</summary>
public static class NomadNetExternalEditor
{
    public static string[] Arguments(string executable, string path, bool darwin = false)
    {
        if (string.IsNullOrWhiteSpace(executable) || executable.Length > 2048 || executable.Any(char.IsControl))
            throw new ArgumentException("Configure one editor executable, without shell arguments.");
        return [darwin && executable == "editor" ? "nano" : executable, path];
    }

    public static async Task<bool> EditAsync(NomadNetEditor editor, string profile,
        Func<string, CancellationToken, Task> run, Func<CancellationToken, Task> save,
        CancellationToken cancellationToken = default)
    {
        if (!editor.IsConfiguration || !editor.Buffer.Editable || editor.Buffer.Dirty)
            throw new InvalidOperationException("Open a clean, editable native or selected RNS configuration first.");
        editor.ValidateConfiguration();
        NomadNetEditor.Guard(profile);
        var directory = Path.Combine(Path.GetFullPath(profile), ".editor-session-" + Guid.NewGuid().ToString("N"));
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var path = Path.Combine(directory, editor.Scope == "config" ? "settings.json" : "config");
        var original = editor.Buffer.Text;
        byte[]? bytes = null;
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(path, options))
            {
                bytes = new UTF8Encoding(false, true).GetBytes(original);
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
                CryptographicOperations.ZeroMemory(bytes);
                bytes = null;
            }
            await run(path, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            NomadNetEditor.Guard(directory);
            NomadNetEditor.Guard(path);
            bytes = await NomadNetLocalFiles.ReadAsync(path,
                editor.Scope == "config" ? NomadNetSettings.MaximumSettingsBytes : NomadNetEditor.MaximumBytes,
                cancellationToken).ConfigureAwait(false);
            var text = new UTF8Encoding(false, true).GetString(bytes);
            if (text == original) return false;
            editor.Buffer.Set(text);
            editor.ValidateConfiguration();
            await save(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
            if (editor.Buffer.Dirty) editor.Buffer.Load(original);
            // Never recurse into an editor-created directory or follow its links.
            NomadNetEditor.Guard(directory);
            var count = 0;
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++count > 4096) throw new IOException("Editor cleanup exceeded 4096 entries; manual cleanup is required.");
                if ((File.GetAttributes(entry) & FileAttributes.Directory) != 0)
                    throw new IOException("Editor left a directory in its working area; manual cleanup is required.");
                File.Delete(entry);
            }
            Directory.Delete(directory);
        }
    }

    public static async Task RunTerminalAsync(string executable, string path, CancellationToken cancellationToken)
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
            throw new InvalidOperationException("External editing requires an interactive terminal; redirected input/output is unsupported.");
        var argv = Arguments(executable, path, OperatingSystem.IsMacOS());
        var window = new RnshWindow { Columns = Console.WindowWidth, Rows = Console.WindowHeight };
        await using var process = RnshProcess.Start(argv, new RnshExecute { Command = argv, Window = window }, null);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var output = PumpAsync(process.Stdout, Console.OpenStandardOutput(), lifetime);
        var error = PumpAsync(process.Stderr, Console.OpenStandardError(), lifetime);
        var wait = process.WaitAsync(lifetime.Token);
        var controlC = Console.TreatControlCAsInput;
        Console.TreatControlCAsInput = true;
        var input = NomadNetConsoleInput.ForwardAsync(process, wait, lifetime.Token);
        try
        {
            // Input failures must tear down the child too, not wait forever for its exit.
            await await Task.WhenAny(wait, input).ConfigureAwait(false);
            if (input.IsFaulted) await input.ConfigureAwait(false);
            var code = await wait.ConfigureAwait(false);
            if (code != 0) throw new IOException($"External editor exited with code {code}; configuration was not saved.");
        }
        finally
        {
            lifetime.Cancel();
            Console.TreatControlCAsInput = controlC;
            await process.DisposeAsync().ConfigureAwait(false);
            try { await Task.WhenAll(input, output, error).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            Console.WriteLine();
        }

    }

    private static async Task PumpAsync(Stream source, Stream destination, CancellationTokenSource lifetime)
    {
        try { await source.CopyToAsync(destination, 4096, lifetime.Token).ConfigureAwait(false); }
        catch
        {
            lifetime.Cancel();
            throw;
        }
    }
}

/// <summary>One input owner for interactive commands and child terminals; never mixes Console.In buffering with raw stdin.</summary>
internal static class NomadNetConsoleInput
{
    public static bool Interactive => !Console.IsInputRedirected && !Console.IsOutputRedirected;

    public static async Task<string?> ReadLineAsync(CancellationToken token, NomadNetSettings? settings = null)
    {
        if (!Interactive) return await Console.In.ReadLineAsync(token).ConfigureAwait(false);
        var mouse = settings?.MouseEnabled == true;
        try
        {
            if (settings is not null)
            {
                Console.Write("\u001b7\u001b[1;1H\u001b[2K" +
                    NomadNetTerminalControls.Heading(NomadNetTerminalControls.MenuText(settings), settings, true) + "\u001b8");
                if (mouse) Console.Write("\u001b[?1000h\u001b[?1006h");
                Console.Out.Flush();
            }
            return await ReadInteractiveAsync(token, settings).ConfigureAwait(false);
        }
        finally
        {
            if (mouse) { Console.Write("\u001b[?1000l\u001b[?1006l"); Console.Out.Flush(); }
        }
    }

    private static async Task<string?> ReadInteractiveAsync(CancellationToken token, NomadNetSettings? settings)
    {
        using var windows = OperatingSystem.IsWindows() ? new NomadNetWindowsTerminalInput(settings?.MouseEnabled == true) : null;
        var line = new StringBuilder();
        var overflow = false;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            ConsoleKeyInfo key;
            if (OperatingSystem.IsWindows())
            {
                if (!windows!.TryRead(out var pressed, out var click))
                { await Task.Delay(20, token).ConfigureAwait(false); continue; }
                if (click is { } position && settings is not null && line.Length == 0 &&
                    NomadNetTerminalControls.Mouse(settings, $"\u001b[<0;{position.Column};{position.Row}M") is { } clicked)
                { Console.WriteLine("Mouse menu selected: " + clicked); return clicked; }
                if (pressed is null) continue;
                key = pressed.Value;
            }
            else
            {
                if (!Console.KeyAvailable) { await Task.Delay(20, token).ConfigureAwait(false); continue; }
                key = Console.ReadKey(intercept: true);
            }
            if (key.KeyChar == '\u001b')
            {
                var sequence = new StringBuilder("\u001b");
                var until = DateTime.UtcNow.AddMilliseconds(200);
                while (sequence.Length < 32 && DateTime.UtcNow < until)
                {
                    char next;
                    if (OperatingSystem.IsWindows())
                    {
                        if (!windows!.TryRead(out var nextKey, out _) || nextKey is null)
                        { await Task.Delay(5, token).ConfigureAwait(false); continue; }
                        next = nextKey.Value.KeyChar;
                    }
                    else
                    {
                        if (!Console.KeyAvailable) { await Task.Delay(5, token).ConfigureAwait(false); continue; }
                        next = Console.ReadKey(intercept: true).KeyChar;
                    }
                    sequence.Append(next);
                    if (char.IsAsciiLetter(next) || next == '~') break;
                }
                if (settings is not null && line.Length == 0 &&
                    NomadNetTerminalControls.Mouse(settings, sequence.ToString()) is { } command)
                { Console.WriteLine(); return command; }
                continue;
            }
            if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return overflow ? new string('x', 16 * 1024 + 1) : line.ToString(); }
            if (key.Key == ConsoleKey.D && key.Modifiers.HasFlag(ConsoleModifiers.Control) && line.Length == 0) return null;
            if (key.Key == ConsoleKey.Backspace && line.Length > 0) { line.Length--; Console.Write("\b \b"); }
            else if (!char.IsControl(key.KeyChar))
            {
                if (line.Length == 16 * 1024) overflow = true;
                else { line.Append(key.KeyChar); Console.Write(key.KeyChar); }
            }
        }
    }

    public static async Task ForwardAsync(RnshProcess process, Task exit, CancellationToken token)
    {
        var columns = Console.WindowWidth;
        var rows = Console.WindowHeight;
        char? high = null;
        while (!exit.IsCompleted)
        {
            token.ThrowIfCancellationRequested();
            if (Console.WindowWidth != columns || Console.WindowHeight != rows)
            {
                columns = Console.WindowWidth; rows = Console.WindowHeight;
                process.Resize(new RnshWindow { Columns = columns, Rows = rows });
            }
            if (!Console.KeyAvailable) { await Task.Delay(20, token).ConfigureAwait(false); continue; }
            var key = Console.ReadKey(intercept: true);
            if (key.KeyChar == '\u001d') throw new OperationCanceledException("External editor cancelled with Ctrl+]; working copy discarded.");
            var text = Encode(key);
            if (text.Length == 1 && char.IsHighSurrogate(text[0])) { high = text[0]; continue; }
            if (high is { } pending)
            {
                text = pending + text;
                high = null;
            }
            if (text.Length != 0)
            {
                await process.Stdin.WriteAsync(Encoding.UTF8.GetBytes(text), token).ConfigureAwait(false);
                await process.Stdin.FlushAsync(token).ConfigureAwait(false);
            }
        }
    }

    internal static string Encode(ConsoleKeyInfo key)
    {
        var text = key.Key switch
        {
            ConsoleKey.Enter => "\r",
            ConsoleKey.Backspace => "\u007f",
            ConsoleKey.UpArrow => "\u001b[A",
            ConsoleKey.DownArrow => "\u001b[B",
            ConsoleKey.RightArrow => "\u001b[C",
            ConsoleKey.LeftArrow => "\u001b[D",
            ConsoleKey.Home => "\u001b[H",
            ConsoleKey.End => "\u001b[F",
            ConsoleKey.Delete => "\u001b[3~",
            ConsoleKey.Insert => "\u001b[2~",
            ConsoleKey.PageUp => "\u001b[5~",
            ConsoleKey.PageDown => "\u001b[6~",
            ConsoleKey.F1 => "\u001bOP",
            ConsoleKey.F2 => "\u001bOQ",
            ConsoleKey.F3 => "\u001bOR",
            ConsoleKey.F4 => "\u001bOS",
            _ => key.KeyChar == '\0' ? "" : key.KeyChar.ToString(),
        };
        return key.Modifiers.HasFlag(ConsoleModifiers.Alt) ? "\u001b" + text : text;
    }
}
