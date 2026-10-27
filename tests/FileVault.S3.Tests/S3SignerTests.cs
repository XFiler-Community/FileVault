using System.Globalization;
using System.Text;

namespace FileVault.S3.Tests;

/// <summary>
/// SigV4 against AWS's published examples: the S3 "Signature Calculations for the Authorization Header" examples
/// (examplebucket, 2013-05-24), the generic aws-sig-v4-test-suite (service "service", 2015-08-30) and the
/// signing-key derivation example of the IAM docs.
/// </summary>
[TestFixture]
public class S3SignerTests
{
    private const string S3AccessKey = "AKIAIOSFODNN7EXAMPLE";
    private const string S3Secret = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";
    private static readonly DateTime S3Date = new(2013, 5, 24, 0, 0, 0, DateTimeKind.Utc);

    private const string SuiteAccessKey = "AKIDEXAMPLE";
    private const string SuiteSecret = "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY";
    private static readonly DateTime SuiteDate = new(2015, 8, 30, 12, 36, 0, DateTimeKind.Utc);

    [Test]
    public void SigningKey_IamExample()
    {
        var key = S3Signer.SigningKey(SuiteSecret, "20120215", "us-east-1", "iam");

        Assert.That(S3Signer.Hex(key), Is.EqualTo("f4780e2d9f65fa895f9c67b32ce1baf0b0d8a43505a000a1a9e090d414db404d"));
    }

    [Test]
    public void S3Example_GetObject()
    {
        var headers = Headers(
            ("Host", "examplebucket.s3.amazonaws.com"),
            ("Range", "bytes=0-9"),
            ("x-amz-content-sha256", S3Signer.EmptyPayloadHash),
            ("x-amz-date", "20130524T000000Z"));

        var canonical = S3Signer.CanonicalRequest("GET", "/test.txt", "", headers, S3Signer.EmptyPayloadHash, out var signed);

        Assert.That(canonical, Is.EqualTo(
            "GET\n/test.txt\n\nhost:examplebucket.s3.amazonaws.com\nrange:bytes=0-9\n" +
            $"x-amz-content-sha256:{S3Signer.EmptyPayloadHash}\nx-amz-date:20130524T000000Z\n\n" +
            $"host;range;x-amz-content-sha256;x-amz-date\n{S3Signer.EmptyPayloadHash}"));
        Assert.That(signed, Is.EqualTo("host;range;x-amz-content-sha256;x-amz-date"));

        Assert.That(S3Authorization("GET", "/test.txt", "", headers, S3Signer.EmptyPayloadHash), Is.EqualTo(
            "AWS4-HMAC-SHA256 Credential=AKIAIOSFODNN7EXAMPLE/20130524/us-east-1/s3/aws4_request, " +
            "SignedHeaders=host;range;x-amz-content-sha256;x-amz-date, " +
            "Signature=f0e8bdb87c964420e857bd35b5d6ed310bd44f0170aba48dd91039c6036bdb41"));
    }

    [Test]
    public void S3Example_PutObject_EncodesTheKey()
    {
        var payloadHash = S3Signer.PayloadHash(Encoding.UTF8.GetBytes("Welcome to Amazon S3."));
        Assert.That(payloadHash, Is.EqualTo("44ce7dd67c959e0d3524ffac1771dfbba87d2b6b4b4e99e42034a8b803f8b072"));

        var headers = Headers(
            ("Date", "Fri, 24 May 2013 00:00:00 GMT"),
            ("Host", "examplebucket.s3.amazonaws.com"),
            ("x-amz-content-sha256", payloadHash),
            ("x-amz-date", "20130524T000000Z"),
            ("x-amz-storage-class", "REDUCED_REDUNDANCY"));
        var uri = "/" + S3Signer.UriEncode("test$file.text", encodeSlash: false);

        Assert.That(uri, Is.EqualTo("/test%24file.text"));
        Assert.That(SignatureOf(S3Authorization("PUT", uri, "", headers, payloadHash)),
            Is.EqualTo("98ad721746da40c64f1a55b78f14c238d841ea1380cd77a1b5971af0ece108bd"));
    }

    [Test]
    public void S3Example_GetBucketLifecycle_EmptyQueryValue()
    {
        var headers = Headers(
            ("Host", "examplebucket.s3.amazonaws.com"),
            ("x-amz-content-sha256", S3Signer.EmptyPayloadHash),
            ("x-amz-date", "20130524T000000Z"));
        var query = S3Signer.CanonicalQuery([new("lifecycle", "")]);

        Assert.That(query, Is.EqualTo("lifecycle="));
        Assert.That(SignatureOf(S3Authorization("GET", "/", query, headers, S3Signer.EmptyPayloadHash)),
            Is.EqualTo("fea454ca298b7da1c68078a5d1bdbfbbe0d65c699e0f91ac7a200a0136783543"));
    }

    [Test]
    public void S3Example_ListObjects_SortsTheQuery()
    {
        var headers = Headers(
            ("Host", "examplebucket.s3.amazonaws.com"),
            ("x-amz-content-sha256", S3Signer.EmptyPayloadHash),
            ("x-amz-date", "20130524T000000Z"));
        var query = S3Signer.CanonicalQuery([new("prefix", "J"), new("max-keys", "2")]);

        Assert.That(query, Is.EqualTo("max-keys=2&prefix=J"));
        Assert.That(SignatureOf(S3Authorization("GET", "/", query, headers, S3Signer.EmptyPayloadHash)),
            Is.EqualTo("34b48302e7b5fa45bde8084f4b7868a86f0a534bc59db6670ed5711ef69dc6f7"));
    }

    [Test]
    public void TestSuite_GetVanilla()
    {
        var headers = Headers(("Host", "example.amazonaws.com"), ("X-Amz-Date", "20150830T123600Z"));

        Assert.That(S3Signer.Authorization(SuiteAccessKey, SuiteSecret, "us-east-1", "service", SuiteDate, "GET", "/",
                "", headers, S3Signer.EmptyPayloadHash),
            Is.EqualTo("AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20150830/us-east-1/service/aws4_request, " +
                       "SignedHeaders=host;x-amz-date, " +
                       "Signature=5fa00fa31553b73ebf1942676e86291e8372ff2a2260956d9b8aae1d763fbf31"));
    }

    [Test]
    public void TestSuite_GetVanillaQueryOrderKeyCase()
    {
        var headers = Headers(("Host", "example.amazonaws.com"), ("X-Amz-Date", "20150830T123600Z"));
        var query = S3Signer.CanonicalQuery([new("Param2", "value2"), new("Param1", "value1")]);

        Assert.That(query, Is.EqualTo("Param1=value1&Param2=value2"));
        Assert.That(SignatureOf(S3Signer.Authorization(SuiteAccessKey, SuiteSecret, "us-east-1", "service", SuiteDate,
                "GET", "/", query, headers, S3Signer.EmptyPayloadHash)),
            Is.EqualTo("b97d918cfa904a5beff61c982a1b6f458b799221646efd99d3219ec94cdf2500"));
    }

    [Test]
    public void TestSuite_GetHeaderValueTrim()
    {
        var headers = Headers(
            ("Host", "example.amazonaws.com"),
            ("My-Header1", " value1"),
            ("My-Header2", " \"a   b   c\""),
            ("X-Amz-Date", "20150830T123600Z"));

        var canonical = S3Signer.CanonicalRequest("GET", "/", "", headers, S3Signer.EmptyPayloadHash, out _);

        Assert.That(canonical, Does.Contain("my-header1:value1\nmy-header2:\"a b c\"\n"));
        Assert.That(SignatureOf(S3Signer.Authorization(SuiteAccessKey, SuiteSecret, "us-east-1", "service", SuiteDate,
                "GET", "/", "", headers, S3Signer.EmptyPayloadHash)),
            Is.EqualTo("acc3ed3afb60bb290fc8d2dd0098b9911fcaa05412b367055dee359757a9c736"));
    }

    [TestCase("test.txt", "test.txt")]
    [TestCase("dir a/f+1%.txt", "dir%20a/f%2B1%25.txt")]
    [TestCase("Привет мир.txt", "%D0%9F%D1%80%D0%B8%D0%B2%D0%B5%D1%82%20%D0%BC%D0%B8%D1%80.txt")]
    [TestCase("a~b_c-d.e", "a~b_c-d.e")]
    [TestCase("q?&=#*'()!", "q%3F%26%3D%23%2A%27%28%29%21")]
    public void UriEncode_KeyKeepsSlashes(string key, string expected) =>
        Assert.That(S3Signer.UriEncode(key, encodeSlash: false), Is.EqualTo(expected));

    [Test]
    public void UriEncode_QueryEncodesSlashes() =>
        Assert.That(S3Signer.UriEncode("a/b c", encodeSlash: true), Is.EqualTo("a%2Fb%20c"));

    [Test]
    public void AmzDate_IsInvariant()
    {
        var previous = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("ar-SA");
            Assert.That(S3Signer.AmzDate(S3Date), Is.EqualTo("20130524T000000Z"));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    private static string S3Authorization(string method, string uri, string query,
        IEnumerable<KeyValuePair<string, string>> headers, string payloadHash) =>
        S3Signer.Authorization(S3AccessKey, S3Secret, "us-east-1", "s3", S3Date, method, uri, query, headers,
            payloadHash);

    private static string SignatureOf(string authorization) =>
        authorization[(authorization.IndexOf("Signature=", StringComparison.Ordinal) + "Signature=".Length)..];

    private static List<KeyValuePair<string, string>> Headers(params (string Name, string Value)[] headers) =>
        headers.Select(h => new KeyValuePair<string, string>(h.Name, h.Value)).ToList();
}
