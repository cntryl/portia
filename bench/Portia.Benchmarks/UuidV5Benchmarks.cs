using System.Text;
using BenchmarkDotNet.Attributes;

namespace Cntryl.Portia;

/// <summary>
///     Measures UUID version 5 generation hot paths.
/// </summary>
[MemoryDiagnoser]
public class UuidV5Benchmarks
{
    string _name = null!;
    byte[] _nameBytes = null!;

    /// <summary>
    ///     Gets or sets the name length used by each benchmark invocation.
    /// </summary>
    [Params(16, 64, 256)]
    public int NameLength { get; set; }

    /// <summary>
    ///     Creates the string and UTF-8 inputs shared by benchmark invocations.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        _name = new string('a', NameLength);
        _nameBytes = Encoding.UTF8.GetBytes(_name);
    }

    /// <summary>
    ///     Creates a UUID version 5 from a UTF-16 string.
    /// </summary>
    /// <returns>The generated UUID.</returns>
    [Benchmark]
    public Uuid CreateFromString() => Uuid.CreateVersion5(Uuid.UrlNamespace, _name);

    /// <summary>
    ///     Creates a UUID version 5 from pre-encoded UTF-8 bytes.
    /// </summary>
    /// <returns>The generated UUID.</returns>
    [Benchmark(Baseline = true)]
    public Uuid CreateFromUtf8Bytes() => Uuid.CreateVersion5(Uuid.UrlNamespace, _nameBytes);
}
