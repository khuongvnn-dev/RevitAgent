namespace RevitAgent.Tests;

using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using RevitAgent.Contracts.Framing;
using Xunit;

[Trait("Category", "SpikeS0")]
public class FramingTests
{
    [Fact]
    public async Task SmallMessage_RoundTrip_PreservesContent()
    {
        // Arrange
        const string payload = "{\"action\":\"ping\",\"id\":\"12345\"}";
        using var stream = new MemoryStream();

        // Act
        await LengthPrefixedPipeFramer.WriteMessageAsync(stream, payload);
        stream.Position = 0;
        string result = await LengthPrefixedPipeFramer.ReadMessageAsync(stream);

        // Assert
        Assert.Equal(payload, result);
    }

    [Fact]
    public async Task MultilineJson_WithNewlinesUnicodeAndSpecialChars_RoundTrip_PreservesContent()
    {
        // Arrange
        string payload = "{\r\n" +
                         "  \"projectName\": \"BIM 3D Model - \u26A0\uFE0F \ud83d\ude80 \ud83c\udfe2\",\r\n" +
                         "  \"description\": \"Multiline notes:\\r\\nLine 1\\r\\nLine 2\\tTabbed\\nLine 4\",\r\n" +
                         "  \"multilingual\": {\r\n" +
                         "    \"japanese\": \"ファミリとパラメータ\",\r\n" +
                         "    \"chinese\": \"建筑信息模型\",\r\n" +
                         "    \"russian\": \"Моделирование\",\r\n" +
                         "    \"vietnamese\": \"Mô hình thông tin công trình\"\r\n" +
                         "  },\r\n" +
                         "  \"symbols\": \"~!@#$%^&*()_+`-={}|[]\\\\:\";'<>?,./\\\"\\/\"\r\n" +
                         "}";
        using var stream = new MemoryStream();

        // Act
        await LengthPrefixedPipeFramer.WriteMessageAsync(stream, payload);
        stream.Position = 0;
        string result = await LengthPrefixedPipeFramer.ReadMessageAsync(stream);

        // Assert
        Assert.Equal(payload, result);
    }

    [Fact]
    public async Task LargePayload_5MBString_RoundTrip_PreservesIntegrity()
    {
        // Arrange: 5MB string (5 * 1024 * 1024 chars = 5,242,880 bytes in UTF-8 for ASCII chars)
        int size = 5 * 1024 * 1024;
        var sb = new StringBuilder(size);
        for (int i = 0; i < size; i++)
        {
            sb.Append((char)('a' + (i % 26)));
        }
        string payload = sb.ToString();
        using var stream = new MemoryStream();

        // Act
        await LengthPrefixedPipeFramer.WriteMessageAsync(stream, payload);
        stream.Position = 0;
        string result = await LengthPrefixedPipeFramer.ReadMessageAsync(stream);

        // Assert
        Assert.Equal(size, result.Length);
        Assert.Equal(payload, result);
    }

    [Fact]
    public async Task EmptyStream_ReadMessageAsync_ThrowsEndOfStreamException()
    {
        // Arrange
        using var stream = new MemoryStream(Array.Empty<byte>());

        // Act & Assert
        await Assert.ThrowsAsync<EndOfStreamException>(async () =>
        {
            await LengthPrefixedPipeFramer.ReadMessageAsync(stream);
        });
    }

    [Fact]
    public async Task IncompleteHeader_ThrowsEndOfStreamException()
    {
        // Arrange: Only 2 bytes available instead of 4-byte length prefix
        byte[] truncatedHeader = { 0x0A, 0x00 };
        using var stream = new MemoryStream(truncatedHeader);

        // Act & Assert
        await Assert.ThrowsAsync<EndOfStreamException>(async () =>
        {
            await LengthPrefixedPipeFramer.ReadMessageAsync(stream);
        });
    }

    [Fact]
    public async Task IncompletePayload_ThrowsEndOfStreamException()
    {
        // Arrange: Header says payload is 100 bytes, but stream ends after 20 bytes
        using var stream = new MemoryStream();
        byte[] lengthHeader = BitConverter.GetBytes((uint)100);
        byte[] partialBody = new byte[20];
        stream.Write(lengthHeader, 0, 4);
        stream.Write(partialBody, 0, 20);
        stream.Position = 0;

        // Act & Assert
        await Assert.ThrowsAsync<EndOfStreamException>(async () =>
        {
            await LengthPrefixedPipeFramer.ReadMessageAsync(stream);
        });
    }

    [Fact]
    public async Task EmptyPayload_WriteAndRead_ReturnsEmptyString()
    {
        // Arrange
        string emptyPayload = string.Empty;
        using var stream = new MemoryStream();

        // Act
        await LengthPrefixedPipeFramer.WriteMessageAsync(stream, emptyPayload);
        stream.Position = 0;
        string result = await LengthPrefixedPipeFramer.ReadMessageAsync(stream);

        // Assert
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public async Task NullArguments_ThrowArgumentNullException()
    {
        using var stream = new MemoryStream();

        await Assert.ThrowsAsync<ArgumentNullException>(() => LengthPrefixedPipeFramer.WriteMessageAsync(null!, "test"));
        await Assert.ThrowsAsync<ArgumentNullException>(() => LengthPrefixedPipeFramer.WriteMessageAsync(stream, null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => LengthPrefixedPipeFramer.ReadMessageAsync(null!));
    }

    [Fact]
    public async Task ExcessivelyLargeHeader_ThrowsInvalidDataException()
    {
        // Arrange: A 4-byte header indicating 64MB (> 32MB max limit)
        byte[] oversizedHeader = BitConverter.GetBytes(64 * 1024 * 1024U);
        using var stream = new MemoryStream(oversizedHeader);

        // Act & Assert
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => LengthPrefixedPipeFramer.ReadMessageAsync(stream));
        Assert.Contains("exceeds the maximum allowed limit", ex.Message);
    }
}
