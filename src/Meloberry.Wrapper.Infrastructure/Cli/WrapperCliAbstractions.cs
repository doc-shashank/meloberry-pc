namespace Meloberry.Wrapper.Infrastructure.Cli;

/// <summary>Console I/O abstraction enabling deterministic testing of `--cli` execution.</summary>
public interface IWrapperConsole
{
    TextWriter Out { get; }
    TextWriter Error { get; }
    TextReader In { get; }
    bool IsInputRedirected { get; }
}

/// <summary>Process standard console adapter used by `mby-pc.exe --cli`.</summary>
public sealed class SystemWrapperConsole : IWrapperConsole
{
    public TextWriter Out => Console.Out;
    public TextWriter Error => Console.Error;
    public TextReader In => Console.In;
    public bool IsInputRedirected => Console.IsInputRedirected;
}
