using System.Globalization;
using System.Text;

namespace NomadNet;

public enum NomadNetLogDestination { Stdout, File }

public sealed record NomadNetLoggingOptions(int Level, NomadNetLogDestination Destination, string? File)
{
    public static NomadNetLoggingOptions Resolve(NomadNetSettings settings, string profileDirectory,
        int inheritedLevel, bool daemon, bool textUi, bool console)
    {
        settings.Validate();
        _ = textUi; // CLI evaluates --console against the original --daemon flag, before --textui.
        var destination = settings.LogDestination ?? NomadNetLogDestination.Stdout;
        if (daemon && console && settings.LogDestination is not null) destination = NomadNetLogDestination.Stdout;
        return new(settings.LogLevel ?? inheritedLevel, destination,
            destination == NomadNetLogDestination.File ? Path.GetFullPath(settings.LogFile ?? Path.Combine(profileDirectory, "logfile")) : null);
    }
}

/// <summary>Application-owned RNS-style logging; never installs a process-global handler.</summary>
public sealed class NomadNetLogging : IAsyncDisposable
{
    public const int MaximumMessageCharacters = 16 * 1024;
    public const int MaximumFileBytes = 5 * 1024 * 1024;
    private readonly object _gate = new();
    private readonly NomadNetLoggingOptions _options;
    private readonly TextWriter _stdout;
    private readonly TextWriter _errors;
    private readonly Func<DateTimeOffset>? _clock;
    private readonly bool _timestamps;
    private readonly CancellationTokenSource _failure = new();
    private Exception? _fault;
    private bool _disposed;

    private NomadNetLogging(NomadNetLoggingOptions options, TextWriter stdout, TextWriter errors,
        Func<DateTimeOffset>? clock, bool timestamps)
    {
        _options = options;
        _stdout = stdout;
        _errors = errors;
        _clock = clock;
        _timestamps = timestamps;
    }

    public CancellationToken FailureToken => _failure.Token;
    public Exception? Fault { get { lock (_gate) return _fault; } }

    public static NomadNetLogging Start(NomadNetLoggingOptions options, TextWriter? stdout = null,
        TextWriter? errors = null, Func<DateTimeOffset>? clock = null, bool timestamps = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        if (options.Level is < -1 or > 7 || !Enum.IsDefined(options.Destination))
            throw new ArgumentException("Invalid logging options.", nameof(options));
        if (options.Destination == NomadNetLogDestination.File)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(options.File);
            using var stream = OpenAppend(options.File);
        }
        return new(options, stdout ?? Console.Out, errors ?? Console.Error, clock, timestamps);
    }

    public static int ParseLevel(string value)
    {
        // Python int accepts Unicode decimal digits, inter-digit underscores and arbitrary magnitude.
        var text = value.AsSpan().Trim();
        var negative = text.StartsWith("-");
        if (text.StartsWith("+") || negative) text = text[1..];
        var level = 0;
        var digit = false;
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value == '_' && digit) { digit = false; continue; }
            if (Rune.GetUnicodeCategory(rune) != UnicodeCategory.DecimalDigitNumber)
                throw new InvalidDataException("Invalid logging.loglevel integer.");
            level = Math.Min(8, level * 10 + (int)Rune.GetNumericValue(rune));
            digit = true;
        }
        if (!digit) throw new InvalidDataException("Invalid logging.loglevel integer.");
        return negative ? 0 : Math.Min(7, level);
    }

    public void Write(int level, string message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_disposed || _fault is not null || _options.Level < level || _options.Level < 0) return;
            var bounded = message.Length > MaximumMessageCharacters ? message[..MaximumMessageCharacters] + " [truncated]" : message;
            var label = level switch
            {
                0 => "[Critical]", 1 => "[Error]   ", 2 => "[Warning] ", 3 => "[Notice]  ",
                4 => "[Info]    ", 5 => "[Verbose] ", 6 => "[Debug]   ", 7 => "[Pathing] ",
                8 => "[Extra]   ", _ => "Unknown",
            };
            var prefix = _timestamps ? "[" + (_clock?.Invoke() ?? DateTimeOffset.Now)
                .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "] " : "";
            var line = prefix + label + " " + NomadNetTerminalText.Safe(bounded);
            try
            {
                if (_options.Destination == NomadNetLogDestination.Stdout)
                {
                    _stdout.WriteLine(line);
                    _stdout.Flush();
                }
                else
                {
                    using (var stream = OpenAppend(_options.File!))
                    {
                        var bytes = Encoding.UTF8.GetBytes(line + Environment.NewLine);
                        stream.Write(bytes);
                    }
                    if (new FileInfo(_options.File!).Length > MaximumFileBytes)
                        System.IO.File.Move(_options.File!, _options.File + ".1", overwrite: true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                _fault = exception;
                try
                {
                    _errors.WriteLine($"nomadnet: logging failed ({_options.File ?? "stdout"}): {exception.Message}; stopping without fallback.");
                    _errors.Flush();
                }
                finally
                {
                    try { _failure.Cancel(); }
                    catch (AggregateException callbacks)
                    {
                        _errors.WriteLine("nomadnet: logging cancellation observer failed: " + callbacks.Flatten().Message);
                        _errors.Flush();
                    }
                }
            }
        }
    }

    public void ThrowIfFaulted()
    {
        if (Fault is { } fault) throw new IOException("NomadNet logging failed; no console fallback was installed.", fault);
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            _failure.Dispose();
        }
        return ValueTask.CompletedTask;
    }

    private static FileStream OpenAppend(string path) => new(path, FileMode.Append, FileAccess.Write, FileShare.Read,
        bufferSize: 4096, FileOptions.None);
}
