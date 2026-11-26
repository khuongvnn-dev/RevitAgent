namespace RevitAgent.Contracts.Framing
{
    using System;
    using System.IO;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    public static class LengthPrefixedPipeFramer
    {
        /// <summary>
        /// Maximum allowed payload size (32MB) to prevent Out-Of-Memory attacks or buffer exhaustion DoS.
        /// </summary>
        public const uint MaxAllowedPayloadBytes = 32 * 1024 * 1024;

        public static async Task WriteMessageAsync(Stream stream, string jsonPayload, CancellationToken ct = default)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            if (jsonPayload == null) throw new ArgumentNullException(nameof(jsonPayload));

            byte[] payloadBytes = Encoding.UTF8.GetBytes(jsonPayload);
            byte[] lengthHeader = BitConverter.GetBytes((uint)payloadBytes.Length);

            // Write 4-byte length prefix (Little-Endian) followed by payload body
            await stream.WriteAsync(lengthHeader, 0, 4, ct).ConfigureAwait(false);
            if (payloadBytes.Length > 0)
            {
                await stream.WriteAsync(payloadBytes, 0, payloadBytes.Length, ct).ConfigureAwait(false);
            }
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }

        public static async Task<string> ReadMessageAsync(Stream stream, CancellationToken ct = default)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));

            byte[] lengthHeader = new byte[4];
            int bytesRead = 0;
            while (bytesRead < 4)
            {
                int n = await stream.ReadAsync(lengthHeader, bytesRead, 4 - bytesRead, ct).ConfigureAwait(false);
                if (n == 0) throw new EndOfStreamException("Pipe connection closed prematurely.");
                bytesRead += n;
            }

            uint payloadLength = BitConverter.ToUInt32(lengthHeader, 0);
            if (payloadLength > MaxAllowedPayloadBytes)
            {
                throw new InvalidDataException($"Payload length {payloadLength} bytes exceeds the maximum allowed limit of {MaxAllowedPayloadBytes} bytes.");
            }
            if (payloadLength == 0)
            {
                return string.Empty;
            }

            byte[] buffer = new byte[payloadLength];
            bytesRead = 0;
            while (bytesRead < payloadLength)
            {
                int n = await stream.ReadAsync(buffer, bytesRead, (int)payloadLength - bytesRead, ct).ConfigureAwait(false);
                if (n == 0) throw new EndOfStreamException("Incomplete frame received.");
                bytesRead += n;
            }

            return Encoding.UTF8.GetString(buffer);
        }
    }
}
