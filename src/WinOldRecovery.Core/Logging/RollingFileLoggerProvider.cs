using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using WinOldRecovery.Core.IO;

namespace WinOldRecovery.Core.Logging;

public sealed class RollingFileLoggerProvider : ILoggerProvider
{
    private readonly object sync = new();
    private readonly SafeFs safeFs;
    private readonly ILogRedactor redactor;
    private readonly long maximumBytes;
    private readonly int retainedFileCount;
    private readonly LogLevel minimumLevel;
    private readonly Func<DateTimeOffset> clock;
    private FileStream stream;
    private StreamWriter writer;
    private bool disposed;

    public RollingFileLoggerProvider(
        string logPath,
        SafeFs safeFs,
        ILogRedactor redactor,
        long maximumBytes = 5 * 1024 * 1024,
        int retainedFileCount = 3,
        LogLevel minimumLevel = LogLevel.Information,
        Func<DateTimeOffset>? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logPath);
        ArgumentNullException.ThrowIfNull(safeFs);
        ArgumentNullException.ThrowIfNull(redactor);
        if (maximumBytes < 128)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumBytes),
                "The rolling log size must be at least 128 bytes.");
        }

        if (retainedFileCount is < 1 or > 10)
        {
            throw new ArgumentOutOfRangeException(
                nameof(retainedFileCount),
                "Retained file count must be between 1 and 10.");
        }

        LogPath = safeFs.GetValidatedWritePath(logPath);
        string? parent = Path.GetDirectoryName(LogPath);
        if (string.IsNullOrEmpty(parent))
        {
            throw new ArgumentException("The log path must have a parent.", nameof(logPath));
        }

        safeFs.CreateDirectory(parent);
        this.safeFs = safeFs;
        this.redactor = redactor;
        this.maximumBytes = maximumBytes;
        this.retainedFileCount = retainedFileCount;
        this.minimumLevel = minimumLevel;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        (stream, writer) = OpenWriter();
    }

    public string LogPath { get; }

    public ILogger CreateLogger(string categoryName)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return new RollingFileLogger(this, categoryName);
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            writer.Dispose();
            stream.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private void Write(
        string category,
        LogLevel level,
        EventId eventId,
        string message,
        Exception? exception)
    {
        if (level < minimumLevel)
        {
            return;
        }

        string eventText = eventId.Id == 0
            ? string.Empty
            : $" {eventId.Id.ToString(CultureInfo.InvariantCulture)}";
        string line =
            $"{clock().ToUniversalTime():O} [{level}] {category}{eventText}: {message}";
        if (exception is not null)
        {
            line += Environment.NewLine + exception;
        }

        line = redactor.Redact(line);
        int lineBytes = Encoding.UTF8.GetByteCount(line + Environment.NewLine);

        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (stream.Length > 0 && stream.Length + lineBytes > maximumBytes)
            {
                Rotate();
            }

            writer.WriteLine(line);
        }
    }

    private void Rotate()
    {
        writer.Dispose();
        stream.Dispose();

        for (int index = retainedFileCount - 1; index >= 1; index--)
        {
            string source = $"{LogPath}.{index}";
            string destination = $"{LogPath}.{index + 1}";
            if (!File.Exists(source))
            {
                continue;
            }

            if (File.Exists(destination))
            {
                safeFs.DeleteFile(destination);
            }

            safeFs.MoveFile(source, destination);
        }

        string firstArchive = $"{LogPath}.1";
        if (File.Exists(firstArchive))
        {
            safeFs.DeleteFile(firstArchive);
        }

        if (File.Exists(LogPath))
        {
            safeFs.MoveFile(LogPath, firstArchive);
        }

        (stream, writer) = OpenWriter();
    }

    private (FileStream Stream, StreamWriter Writer) OpenWriter()
    {
        FileStream newStream = safeFs.OpenWrite(
            LogPath,
            FileMode.Append,
            share: FileShare.Read);
        StreamWriter newWriter = new(
            newStream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true,
        };
        return (newStream, newWriter);
    }

    private sealed class RollingFileLogger(
        RollingFileLoggerProvider provider,
        string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return logLevel >= provider.minimumLevel;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            if (!IsEnabled(logLevel))
            {
                return;
            }

            provider.Write(
                category,
                logLevel,
                eventId,
                formatter(state, exception),
                exception);
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
