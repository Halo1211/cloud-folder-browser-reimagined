using System.Buffers.Binary;
using CloudFolderBrowser.Networking;

namespace CloudFolderBrowser.Tests;

public sealed class NinthPassAuditTests
{
    [Fact]
    public void Dns_RejectsResponseWithWrongQuestionType()
    {
        byte[] response = Answer();
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(QuestionTail(response)), 28);

        Assert.Throws<InvalidDataException>(() => CustomDnsResolver.ParseResponse(response, 0x1234, 1));
    }

    [Fact]
    public void Dns_RejectsResponseWithWrongQuestionClass()
    {
        byte[] response = Answer();
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(QuestionTail(response) + 2), 3);

        Assert.Throws<InvalidDataException>(() => CustomDnsResolver.ParseResponse(response, 0x1234, 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Dns_RejectsResponseWithUnexpectedQuestionCount(ushort count)
    {
        byte[] response = Answer();
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(4), count);

        Assert.Throws<InvalidDataException>(() => CustomDnsResolver.ParseResponse(response, 0x1234, 1));
    }

    [Fact]
    public void Dns_RejectsNonstandardResponseOpcode()
    {
        byte[] response = Answer();
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(2), 0x8980);

        Assert.Throws<InvalidDataException>(() => CustomDnsResolver.ParseResponse(response, 0x1234, 1));
    }

    [Fact]
    public void Dns_RejectsInvalidAnswerCompressionPointer()
    {
        byte[] response = Answer();
        int answerOffset = response.Length - 16;
        response[answerOffset] = 0xC0;
        response[answerOffset + 1] = 0xFF;

        Assert.Throws<InvalidDataException>(() => CustomDnsResolver.ParseResponse(response, 0x1234, 1));
    }

    [Fact]
    public void Dns_PreservesMatchingStandardResponse()
    {
        var answer = CustomDnsResolver.ParseResponse(Answer(), 0x1234, 1);

        Assert.Equal(new byte[] { 192, 0, 2, 1 }, Assert.Single(answer.Addresses).GetAddressBytes());
        Assert.Equal((uint)120, answer.MinimumTtl);
    }

    [Fact]
    public void Dns_RejectsResponseForAnotherHost()
    {
        byte[] response = Answer();
        response[13] = (byte)'x';

        Assert.Throws<InvalidDataException>(() =>
            CustomDnsResolver.ParseResponse(response, 0x1234, 1, "example.com"));
    }

    [Fact]
    public void Dns_IgnoresAddressForUnrelatedAnswerOwner()
    {
        byte[] response = ResponseWithRecords(stream =>
            WriteRecord(stream, "other.example.com", 1, 120, new byte[] { 192, 0, 2, 9 }));

        Assert.Empty(CustomDnsResolver.ParseResponse(response, 0x1234, 1, "example.com").Addresses);
    }

    [Fact]
    public void Dns_AcceptsCnameChainAndUsesShortestTtl()
    {
        byte[] response = ResponseWithRecords(stream =>
        {
            using var name = new MemoryStream();
            WriteName(name, "alias.example.com");
            WriteRecord(stream, "example.com", 5, 30, name.ToArray());
            WriteRecord(stream, "alias.example.com", 1, 120, new byte[] { 192, 0, 2, 7 });
        }, answerCount: 2);

        var result = CustomDnsResolver.ParseResponse(response, 0x1234, 1, "example.com");
        Assert.Equal(new byte[] { 192, 0, 2, 7 }, Assert.Single(result.Addresses).GetAddressBytes());
        Assert.Equal((uint)30, result.MinimumTtl);
    }

    [Fact]
    public async Task FlareSolverr_RejectsNonHttpTargetBeforeRequest()
    {
        using var client = new FlareSolverrClient("http://127.0.0.1:8191", TimeSpan.FromSeconds(1));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.GetSessionAsync(new Uri("file:///C:/secret.txt"), cancellation.Token));
    }

    [Fact]
    public void FlareSolverr_DoesNotEmitCookiesForOtherSchemes()
    {
        var session = new CloudflareSession("fixture", new[]
        {
            new FlareSolverrCookie { Name = "session", Value = "secret", Domain = "example.com" }
        });

        Assert.Equal("session=secret", session.GetCookieHeader(new Uri("https://example.com/file")));
        Assert.Equal(string.Empty, session.GetCookieHeader(new Uri("ftp://example.com/file")));
    }

    [Fact]
    public void FlareSolverr_DoesNotEmitCookiesForRelativeUri()
    {
        var session = new CloudflareSession("fixture", new[]
        {
            new FlareSolverrCookie { Name = "session", Value = "secret", Domain = "example.com" }
        });

        Assert.Equal(string.Empty, session.GetCookieHeader(new Uri("/file", UriKind.Relative)));
    }

    private static int QuestionTail(byte[] response) => response.Length - 16 - 4;

    private static byte[] Answer()
    {
        byte[] query = CustomDnsResolver.BuildQuery(0x1234, "example.com", 1);
        using var stream = new MemoryStream();
        stream.Write(query);
        byte[] buffer = stream.GetBuffer();
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(2), 0x8180);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(6), 1);
        stream.Write(new byte[] { 0xC0, 0x0C, 0, 1, 0, 1, 0, 0, 0, 120, 0, 4, 192, 0, 2, 1 });
        return stream.ToArray();
    }

    private static byte[] ResponseWithRecords(Action<MemoryStream> write, ushort answerCount = 1)
    {
        byte[] query = CustomDnsResolver.BuildQuery(0x1234, "example.com", 1);
        using var stream = new MemoryStream();
        stream.Write(query);
        byte[] buffer = stream.GetBuffer();
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(2), 0x8180);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(6), answerCount);
        write(stream);
        return stream.ToArray();
    }

    private static void WriteRecord(MemoryStream stream, string owner, ushort type, uint ttl, byte[] data)
    {
        WriteName(stream, owner);
        Span<byte> header = stackalloc byte[10];
        BinaryPrimitives.WriteUInt16BigEndian(header, type);
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], 1);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], ttl);
        BinaryPrimitives.WriteUInt16BigEndian(header[8..], checked((ushort)data.Length));
        stream.Write(header);
        stream.Write(data);
    }

    private static void WriteName(MemoryStream stream, string host)
    {
        foreach (string label in host.Split('.'))
        {
            stream.WriteByte(checked((byte)label.Length));
            stream.Write(System.Text.Encoding.ASCII.GetBytes(label));
        }
        stream.WriteByte(0);
    }
}
