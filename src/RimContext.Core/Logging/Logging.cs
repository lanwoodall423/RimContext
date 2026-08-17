namespace RimContext.Core.Logging;

public interface ILogger
{
    void Debug(string message);

    void Info(string message);

    void Warning(string message);

    void Error(string message);
}

public sealed class NullLogger : ILogger
{
    public void Debug(string message)
    {
    }

    public void Info(string message)
    {
    }

    public void Warning(string message)
    {
    }

    public void Error(string message)
    {
    }
}

public sealed class TextWriterLogger : ILogger
{
    private readonly TextWriter writer;

    public TextWriterLogger(TextWriter writer)
    {
        this.writer = writer ?? throw new ArgumentNullException(nameof(writer));
    }

    public void Debug(string message) => Write("debug", message);

    public void Info(string message) => Write("info", message);

    public void Warning(string message) => Write("warning", message);

    public void Error(string message) => Write("error", message);

    private void Write(string level, string message)
    {
        writer.Write("[");
        writer.Write(level);
        writer.Write("] ");
        writer.WriteLine(message);
        writer.Flush();
    }
}
