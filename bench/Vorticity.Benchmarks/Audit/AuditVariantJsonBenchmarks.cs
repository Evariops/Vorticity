// Audit VA-01: what a variant made from a log line's JSON costs, a call a row, as an ingestion of JSON attributes makes it.
//
// A JSON object of fifteen members, two of them objects and one an array, about 400 bytes.
using System.Text;

using BenchmarkDotNet.Attributes;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A variant from a JSON object.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditVariantJsonBenchmarks
{
    private static readonly byte[] Json = Encoding.UTF8.GetBytes(
        "{\"service\":\"checkout\",\"pod\":\"checkout-7f9c6d-x2k4p\",\"status\":503,\"latency_ms\":182.5,\"retry\":true," +
        "\"user\":{\"id\":\"u-182736\",\"tier\":\"gold\",\"country\":\"FR\"},\"route\":\"/api/v2/cart/items\",\"method\":\"POST\"," +
        "\"bytes\":48213,\"trace\":\"4bf92f3577b34da6a3ce929d0e0e4736\",\"tags\":[\"edge\",\"eu-west-3\",\"canary\"]," +
        "\"region\":\"eu-west-3\",\"http\":{\"version\":\"2\",\"tls\":true},\"note\":null}");

    /// <summary>The variant of the object.</summary>
    [Benchmark]
    public int FromJson() => Variant.FromJson(Json).Value.Length;
}
