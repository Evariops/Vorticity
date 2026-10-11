// Signature Version 4, held to the signatures AWS publishes: the get-vanilla cases of its test suite,
// and the four worked examples of the S3 reference (a ranged GET, a PUT, a bucket's lifecycle and a
// listing). Each was recomputed by an implementation written apart, in Python's hmac and hashlib, which
// agreed with the published value before it was written here.
using System;
using System.Globalization;
using Vorticity.ObjectStorage.Credentials;
using Vorticity.ObjectStorage.Http;
using Xunit;

namespace Vorticity.Tests.ObjectStorage;

public sealed class SigV4Tests
{
    private const string SuiteSecret = "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY";
    private const string S3Secret = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";
    private const string Empty = SigV4Signer.EmptyPayloadHash;

    [Fact]
    public void TheTestSuitesVanillaGetSigns()
    {
        using AwsKeys keys = new AwsKeys("AKIDEXAMPLE", SuiteSecret, null);
        string authorization = new SigV4Signer("us-east-1", "service").Authorization(
            keys, "20150830T123600Z", "GET", "/", string.Empty,
            [("host", "example.amazonaws.com"), ("x-amz-date", "20150830T123600Z")], Empty);
        Assert.Equal(
            "AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20150830/us-east-1/service/aws4_request, SignedHeaders=host;x-amz-date, " +
            "Signature=5fa00fa31553b73ebf1942676e86291e8372ff2a2260956d9b8aae1d763fbf31",
            authorization);
    }

    [Fact]
    public void TheTestSuitesQueryIsSignedInTheOrderItsNamesSort()
    {
        using AwsKeys keys = new AwsKeys("AKIDEXAMPLE", SuiteSecret, null);
        string authorization = new SigV4Signer("us-east-1", "service").Authorization(
            keys, "20150830T123600Z", "GET", "/", "Param1=value1&Param2=value2",
            [("x-amz-date", "20150830T123600Z"), ("host", "example.amazonaws.com")], Empty);
        Assert.EndsWith("Signature=b97d918cfa904a5beff61c982a1b6f458b799221646efd99d3219ec94cdf2500", authorization, StringComparison.Ordinal);
    }

    [Fact]
    public void TheS3ReferenceRangedGetSigns()
    {
        using AwsKeys keys = new AwsKeys("AKIAIOSFODNN7EXAMPLE", S3Secret, null);
        string authorization = new SigV4Signer("us-east-1").Authorization(
            keys, "20130524T000000Z", "GET", "/test.txt", string.Empty,
            [("host", "examplebucket.s3.amazonaws.com"), ("range", "bytes=0-9"), ("x-amz-content-sha256", Empty), ("x-amz-date", "20130524T000000Z")],
            Empty);
        Assert.Equal(
            "AWS4-HMAC-SHA256 Credential=AKIAIOSFODNN7EXAMPLE/20130524/us-east-1/s3/aws4_request, " +
            "SignedHeaders=host;range;x-amz-content-sha256;x-amz-date, Signature=f0e8bdb87c964420e857bd35b5d6ed310bd44f0170aba48dd91039c6036bdb41",
            authorization);
    }

    [Fact]
    public void TheS3ReferencePutSigns()
    {
        const string payload = "44ce7dd67c959e0d3524ffac1771dfbba87d2b6b4b4e99e42034a8b803f8b072";
        using AwsKeys keys = new AwsKeys("AKIAIOSFODNN7EXAMPLE", S3Secret, null);
        string authorization = new SigV4Signer("us-east-1").Authorization(
            keys, "20130524T000000Z", "PUT", UriEncoding.Path("/test$file.text"), string.Empty,
            [
                ("date", "Fri, 24 May 2013 00:00:00 GMT"), ("host", "examplebucket.s3.amazonaws.com"),
                ("x-amz-content-sha256", payload), ("x-amz-date", "20130524T000000Z"), ("x-amz-storage-class", "REDUCED_REDUNDANCY"),
            ],
            payload);
        Assert.EndsWith("Signature=98ad721746da40c64f1a55b78f14c238d841ea1380cd77a1b5971af0ece108bd", authorization, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("lifecycle=", "fea454ca298b7da1c68078a5d1bdbfbbe0d65c699e0f91ac7a200a0136783543")]
    [InlineData("max-keys=2&prefix=J", "34b48302e7b5fa45bde8084f4b7868a86f0a534bc59db6670ed5711ef69dc6f7")]
    public void TheS3ReferenceBucketRequestsSign(string query, string signature)
    {
        using AwsKeys keys = new AwsKeys("AKIAIOSFODNN7EXAMPLE", S3Secret, null);
        string authorization = new SigV4Signer("us-east-1").Authorization(
            keys, "20130524T000000Z", "GET", "/", query,
            [("host", "examplebucket.s3.amazonaws.com"), ("x-amz-content-sha256", Empty), ("x-amz-date", "20130524T000000Z")],
            Empty);
        Assert.EndsWith("Signature=" + signature, authorization, StringComparison.Ordinal);
    }

    [Fact]
    public void AHeaderValueIsTrimmedAndItsSpacesCollapsed()
    {
        using AwsKeys keys = new AwsKeys("AKIDEXAMPLE", SuiteSecret, null);
        SigV4Signer signer = new SigV4Signer("us-east-1", "service");
        string loose = signer.Authorization(keys, "20150830T123600Z", "GET", "/", string.Empty,
            [("host", "example.amazonaws.com"), ("my-header1", "  value1   with   spaces "), ("x-amz-date", "20150830T123600Z")], Empty);
        string tight = signer.Authorization(keys, "20150830T123600Z", "GET", "/", string.Empty,
            [("host", "example.amazonaws.com"), ("my-header1", "value1 with spaces"), ("x-amz-date", "20150830T123600Z")], Empty);
        Assert.Equal(tight, loose);
    }

    [Fact]
    public void ADateIsWrittenAsTheHeaderWantsIt() =>
        Assert.Equal("20260102T030405Z", SigV4Signer.AmzDate(new DateTimeOffset(2026, 1, 2, 4, 4, 5, TimeSpan.FromHours(1))));

    [Theory]
    [InlineData("data/a.vortex", "data/a.vortex")]
    [InlineData("a b+c", "a%20b%2Bc")]
    [InlineData("é/~x", "%C3%A9/~x")]
    [InlineData("100%", "100%25")]
    public void APathKeepsItsSlashesAndEncodesTheRest(string key, string encoded)
    {
        Assert.Equal(encoded, UriEncoding.Path(key));
        Assert.Equal(encoded.Replace("/", "%2F", StringComparison.Ordinal), UriEncoding.Query(key));
    }

    [Theory]
    [InlineData("a+b", "a b")]
    [InlineData("100%25+done", "100% done")]
    [InlineData("%C3%A9t%C3%A9", "été")]
    [InlineData("plain/key", "plain/key")]
    public void AListedKeyDecodesFromItsUrlEncoding(string listed, string key) =>
        Assert.Equal(key, UriEncoding.DecodeForm(listed));

    [Fact]
    public void KeysNameTheirIdentityAndNeverTheirSecret()
    {
        using AwsKeys keys = new AwsKeys("AKIDVISIBLE", "very-secret", "token");
        Assert.DoesNotContain("very-secret", keys.ToString(), StringComparison.Ordinal);
        Assert.Contains("AKIDVISIBLE", keys.ToString(), StringComparison.Ordinal);
        Assert.Equal("AWS4very-secret", System.Text.Encoding.ASCII.GetString(keys.SigningSecret.Span));
        keys.Dispose();
        Assert.Throws<ObjectDisposedException>(() => keys.SigningSecret.Span.Length.ToString(CultureInfo.InvariantCulture));
    }
}
