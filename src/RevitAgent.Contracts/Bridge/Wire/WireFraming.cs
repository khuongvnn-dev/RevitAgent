namespace RevitAgent.Contracts.Bridge.Wire
{
    using System;
    using System.IO;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Bridge frame = 4-byte little-endian payload length + UTF-8 JSON payload (no BOM).
    /// Same layout as <c>LengthPrefixedPipeFramer</c>, without dependencies (linked into the Bridge).
    /// </summary>
    internal static class WireFraming
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        public static async Task WriteFrameAsync(Stream stream, string json, CancellationToken cancellationToken)
        {
            var payload = Utf8.GetBytes(json ?? string.Empty);
            if (payload.Length > BridgeWire.MaxPayloadBytes)
            {
                throw new InvalidDataException("Frame payload of " + payload.Length + " bytes exceeds the " + BridgeWire.MaxPayloadBytes + " byte limit.");
            }

            var buffer = new byte[4 + payload.Length];
            buffer[0] = (byte)payload.Length;
            buffer[1] = (byte)(payload.Length >> 8);
            buffer[2] = (byte)(payload.Length >> 16);
            buffer[3] = (byte)(payload.Length >> 24);
            Buffer.BlockCopy(payload, 0, buffer, 4, payload.Length);
            await stream.WriteAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Reads one frame. Returns null on a clean end of stream before a new frame starts.</summary>
        public static async Task<string?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
        {
            var header = new byte[4];
            var got = await ReadExactlyAsync(stream, header, 4, cancellationToken).ConfigureAwait(false);
            if (got == 0)
            {
                return null;
            }

            if (got < 4)
            {
                throw new EndOfStreamException("Connection closed inside a frame header.");
            }

            var length = (uint)(header[0] | (header[1] << 8) | (header[2] << 16) | (header[3] << 24));
            if (length > BridgeWire.MaxPayloadBytes)
            {
                throw new InvalidDataException("Frame payload length " + length + " exceeds the " + BridgeWire.MaxPayloadBytes + " byte limit.");
            }

            if (length == 0)
            {
                return string.Empty;
            }

            var payload = new byte[length];
            got = await ReadExactlyAsync(stream, payload, (int)length, cancellationToken).ConfigureAwait(false);
            if (got < length)
            {
                throw new EndOfStreamException("Connection closed inside a frame payload.");
            }

            return Utf8.GetString(payload);
        }

        private static async Task<int> ReadExactlyAsync(Stream stream, byte[] buffer, int count, CancellationToken cancellationToken)
        {
            var total = 0;
            while (total < count)
            {
                var n = await stream.ReadAsync(buffer, total, count - total, cancellationToken).ConfigureAwait(false);
                if (n == 0)
                {
                    break;
                }

                total += n;
            }

            return total;
        }
    }
}
