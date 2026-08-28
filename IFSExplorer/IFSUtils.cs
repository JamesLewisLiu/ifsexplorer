using System;
using System.Collections.Generic;
using System.IO;

namespace IFSExplorer
{
    static class IFSUtils
    {
        internal static IEnumerable<FileIndex> ParseIFS(Stream stream)
        {
            try {
                return IFSArchiveReader.Read(stream);
            } catch (InvalidDataException) {
                return ParseIFSHeuristic(stream);
            } catch (NotSupportedException) {
                return ParseIFSHeuristic(stream);
            }
        }

        private static IEnumerable<FileIndex> ParseIFSHeuristic(Stream stream)
        {
            stream.Seek(16, SeekOrigin.Begin);
            var fIndex = ReadInt(stream);
            stream.Seek(40, SeekOrigin.Begin);
            var fHeader = ReadInt(stream);

            if (fHeader%4 != 0) {
                throw new ArgumentException("fHeader%4 != 0");
            }

            stream.Seek(fHeader + 72, SeekOrigin.Begin);

            var packet = new byte[4];
            var zeroPadArray = new byte[] {0, 0, 0, 0};
            var separator = new byte[] {0, 0, 0, 0};
            var sepInit = false;
            var zeroPad = false;
            var entryNumber = 0;

            var fileMappings = new List<FileIndex>();

            while (stream.Position < fIndex) {
                stream.Read(packet, 0, 4);

                if (stream.Position >= fIndex) {
                    break;
                }

                if (!sepInit || ByteArrayEqual(separator, zeroPadArray)) {
                    if (!ByteArrayEqual(packet, zeroPadArray)) {
                        packet.CopyTo(separator, 0);
                        sepInit = true;
                        continue;
                    }
                } else {
                    if (separator[0] == packet[0]) {
                        continue;
                    }

                    if (ByteArrayEqual(packet, zeroPadArray)) {
                        if (zeroPad) {
                            continue;
                        }
                        zeroPad = true;
                    }
                }

                var index = ReadInt(packet);

                if (stream.Position >= fIndex) {
                    break;
                }

                var size = ReadInt(stream);
                if (size > 0) {
                    fileMappings.Add(new FileIndex(stream, fIndex + index, size, entryNumber++));
                }
            }

            return fileMappings;
        }

        internal static byte[] DecompressLSZZ(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 8) {
                throw new InvalidDataException("AVSLZ header is truncated.");
            }

            var uncompressedSize = ReadInt(bytes, 0);
            var compressedSize = ReadInt(bytes, 4);
            if (compressedSize >= 0 && bytes.Length == compressedSize + 8) {
                return DecompressLz77(bytes, 8, compressedSize, uncompressedSize);
            }

            // Some files have no compressed payload: their two leading u32s
            // belong at the end of the raw texture instead.
            var raw = new byte[bytes.Length];
            Buffer.BlockCopy(bytes, 8, raw, 0, bytes.Length - 8);
            Buffer.BlockCopy(bytes, 0, raw, bytes.Length - 8, 8);
            return raw;
        }

        internal static DecodedRaw DecodeFile(FileIndex fileIndex)
        {
            var bytes = fileIndex.Read();
            var texture = fileIndex.Texture;
            if (texture == null) {
                return DecodeRaw(DecompressLSZZ(bytes));
            }

            if (string.Equals(texture.Compression, "avslz", StringComparison.OrdinalIgnoreCase)) {
                bytes = DecompressLSZZ(bytes);
            }

            switch (texture.Format.ToLowerInvariant()) {
                case "argb8888rev":
                    return DecodeArgb8888Rev(bytes, texture.Width, texture.Height);
                case "argb4444":
                    return DecodeArgb4444(bytes, texture.Width, texture.Height);
                case "dxt1":
                    return DecodeDxt(bytes, texture.Width, texture.Height, false);
                case "dxt5":
                    return DecodeDxt(bytes, texture.Width, texture.Height, true);
                default:
                    throw new NotSupportedException("Unsupported texture format " + texture.Format + ".");
            }
        }

        internal static DecodedRaw DecodeRaw(byte[] raw)
        {
            var fileSize = raw.Length;

            if (fileSize == 0 || (fileSize%4) != 0) {
                throw new ArgumentException("raw");
            }

            var argbSize = fileSize >> 2;
            var argbArr = new int[argbSize];

            using (var stream = new MemoryStream(raw)) {
                var data = new byte[4];
                var index = 0;
                while (stream.Read(data, 0, 4) == 4) {
                    argbArr[index++] = (data[3] << 24) | (data[2] << 16) | (data[1] << 8) | data[0];
                }

                var offset = 0;
                if (argbArr[0] == 0x54584454) {
                    // XXX: or 0x54445854?
                    offset = 16;
                }

                // Poor woman's Set.
                var set = new Dictionary<int, byte>();
                for (var i = 1; i <= (int) Math.Sqrt(argbSize); ++i) {
                    if (argbSize%i != 0) {
                        continue;
                    }
                    set[i] = 0;
                    set[argbSize/i] = 0;
                }

                var indexSize = set.Count;
                var widths = new int[indexSize];
                var heights = new int[indexSize];
                var k = 0;

                var keys = new List<int>(set.Keys);
                keys.Sort();

                foreach (var i in keys) {
                    widths[k] = i;
                    heights[indexSize - k - 1] = i;
                    ++k;
                }

                return new DecodedRaw(raw.Length, offset, argbArr, widths, heights);
            }
        }

        private static DecodedRaw DecodeArgb8888Rev(byte[] bytes, int width, int height)
        {
            var pixels = new int[checked(width * height)];
            for (var i = 0; i < pixels.Length; ++i) {
                var offset = i * 4;
                if (offset + 3 >= bytes.Length) {
                    break;
                }
                pixels[i] = (bytes[offset + 3] << 24) | (bytes[offset + 2] << 16) |
                            (bytes[offset + 1] << 8) | bytes[offset];
            }
            return new DecodedRaw(bytes.Length, pixels, width, height);
        }

        private static DecodedRaw DecodeArgb4444(byte[] bytes, int width, int height)
        {
            var pixels = new int[checked(width * height)];
            for (var i = 0; i < pixels.Length; ++i) {
                var offset = i * 2;
                if (offset + 1 >= bytes.Length) {
                    break;
                }
                var first = bytes[offset];
                var second = bytes[offset + 1];
                var red = ExpandNibble(second & 0x0f);
                var green = ExpandNibble(first >> 4);
                var blue = ExpandNibble(first & 0x0f);
                var alpha = ExpandNibble(second >> 4);
                pixels[i] = (alpha << 24) | (red << 16) | (green << 8) | blue;
            }
            return new DecodedRaw(bytes.Length, pixels, width, height);
        }

        private static int ExpandNibble(int value)
        {
            return (value << 4) | value;
        }

        private static DecodedRaw DecodeDxt(byte[] bytes, int width, int height, bool dxt5)
        {
            var blockSize = dxt5 ? 16 : 8;
            var blockWidth = (width + 3) / 4;
            var blockHeight = (height + 3) / 4;
            var expected = checked(blockWidth * blockHeight * blockSize);
            var canonical = new byte[expected];
            var copyLength = Math.Min(bytes.Length, expected);
            Buffer.BlockCopy(bytes, 0, canonical, 0, copyLength);
            for (var i = 0; i + 1 < copyLength; i += 2) {
                var value = canonical[i];
                canonical[i] = canonical[i + 1];
                canonical[i + 1] = value;
            }

            var pixels = new int[checked(width * height)];
            var offset = 0;
            for (var blockY = 0; blockY < blockHeight; ++blockY) {
                for (var blockX = 0; blockX < blockWidth; ++blockX) {
                    if (dxt5) {
                        DecodeDxt5Block(canonical, offset, pixels, width, height, blockX * 4, blockY * 4);
                    } else {
                        DecodeColorBlock(canonical, offset, pixels, width, height,
                                         blockX * 4, blockY * 4, false, null);
                    }
                    offset += blockSize;
                }
            }
            return new DecodedRaw(bytes.Length, pixels, width, height);
        }

        private static void DecodeDxt5Block(byte[] bytes, int offset, int[] pixels, int width, int height,
                                            int startX, int startY)
        {
            var alpha = new int[8];
            alpha[0] = bytes[offset];
            alpha[1] = bytes[offset + 1];
            if (alpha[0] > alpha[1]) {
                for (var i = 1; i <= 6; ++i) {
                    alpha[i + 1] = ((7 - i) * alpha[0] + i * alpha[1]) / 7;
                }
            } else {
                for (var i = 1; i <= 4; ++i) {
                    alpha[i + 1] = ((5 - i) * alpha[0] + i * alpha[1]) / 5;
                }
                alpha[6] = 0;
                alpha[7] = 255;
            }

            ulong alphaBits = 0;
            for (var i = 0; i < 6; ++i) {
                alphaBits |= (ulong) bytes[offset + 2 + i] << (8 * i);
            }
            var alphas = new int[16];
            for (var i = 0; i < 16; ++i) {
                alphas[i] = alpha[(int) ((alphaBits >> (3 * i)) & 7)];
            }
            DecodeColorBlock(bytes, offset + 8, pixels, width, height, startX, startY, true, alphas);
        }

        private static void DecodeColorBlock(byte[] bytes, int offset, int[] pixels, int width, int height,
                                             int startX, int startY, bool forceFourColors, int[] alphas)
        {
            var color0 = bytes[offset] | (bytes[offset + 1] << 8);
            var color1 = bytes[offset + 2] | (bytes[offset + 3] << 8);
            var colors = new int[4];
            colors[0] = DecodeRgb565(color0);
            colors[1] = DecodeRgb565(color1);

            if (color0 > color1 || forceFourColors) {
                colors[2] = InterpolateColor(colors[0], colors[1], 2, 1, 3);
                colors[3] = InterpolateColor(colors[0], colors[1], 1, 2, 3);
            } else {
                colors[2] = InterpolateColor(colors[0], colors[1], 1, 1, 2);
                colors[3] = 0;
            }

            var indices = (uint) (bytes[offset + 4] | (bytes[offset + 5] << 8) |
                                  (bytes[offset + 6] << 16) | (bytes[offset + 7] << 24));
            for (var y = 0; y < 4; ++y) {
                for (var x = 0; x < 4; ++x) {
                    var pixelIndex = y * 4 + x;
                    var color = colors[(int) ((indices >> (pixelIndex * 2)) & 3)];
                    var targetX = startX + x;
                    var targetY = startY + y;
                    if (targetX >= width || targetY >= height) {
                        continue;
                    }
                    if (alphas != null) {
                        color = (color & 0x00ffffff) | (alphas[pixelIndex] << 24);
                    }
                    pixels[targetY * width + targetX] = color;
                }
            }
        }

        private static int DecodeRgb565(int value)
        {
            var red = (value >> 11) & 0x1f;
            var green = (value >> 5) & 0x3f;
            var blue = value & 0x1f;
            red = (red << 3) | (red >> 2);
            green = (green << 2) | (green >> 4);
            blue = (blue << 3) | (blue >> 2);
            return unchecked((int) 0xff000000) | (red << 16) | (green << 8) | blue;
        }

        private static int InterpolateColor(int first, int second, int firstWeight, int secondWeight, int divisor)
        {
            var red = (((first >> 16) & 0xff) * firstWeight + ((second >> 16) & 0xff) * secondWeight) / divisor;
            var green = (((first >> 8) & 0xff) * firstWeight + ((second >> 8) & 0xff) * secondWeight) / divisor;
            var blue = ((first & 0xff) * firstWeight + (second & 0xff) * secondWeight) / divisor;
            return unchecked((int) 0xff000000) | (red << 16) | (green << 8) | blue;
        }

        private static byte[] DecompressLz77(byte[] input, int inputOffset, int inputLength, int expectedSize)
        {
            var initialSize = expectedSize > 0 ? expectedSize : Math.Max(inputLength * 2, 256);
            var output = new byte[initialSize];
            var outputLength = 0;
            var end = inputOffset + inputLength;
            var position = inputOffset;

            while (position < end) {
                var flags = input[position++];
                for (var bit = 0; bit < 8; ++bit) {
                    if ((flags & (1 << bit)) != 0) {
                        if (position >= end) {
                            throw new InvalidDataException("LZ77 literal is truncated.");
                        }
                        AppendByte(ref output, ref outputLength, input[position++]);
                        continue;
                    }

                    if (position + 1 >= end) {
                        throw new InvalidDataException("LZ77 match is truncated.");
                    }
                    var word = (input[position] << 8) | input[position + 1];
                    position += 2;
                    var distance = word >> 4;
                    var length = (word & 0x0f) + 3;
                    if (distance == 0) {
                        if (expectedSize > 0 && outputLength != expectedSize) {
                            throw new InvalidDataException("LZ77 output size does not match its header.");
                        }
                        return TrimBuffer(output, outputLength);
                    }

                    if (distance > outputLength) {
                        var zeroCount = Math.Min(distance - outputLength, length);
                        EnsureCapacity(ref output, outputLength + zeroCount);
                        outputLength += zeroCount;
                        length -= zeroCount;
                    }
                    for (var i = 0; i < length; ++i) {
                        AppendByte(ref output, ref outputLength, output[outputLength - distance]);
                    }
                }
            }
            throw new InvalidDataException("LZ77 stream has no end marker.");
        }

        private static void AppendByte(ref byte[] output, ref int length, byte value)
        {
            EnsureCapacity(ref output, length + 1);
            output[length++] = value;
        }

        private static void EnsureCapacity(ref byte[] output, int needed)
        {
            if (needed <= output.Length) {
                return;
            }
            var capacity = Math.Max(needed, output.Length == 0 ? 256 : output.Length * 2);
            Array.Resize(ref output, capacity);
        }

        private static byte[] TrimBuffer(byte[] output, int length)
        {
            if (length == output.Length) {
                return output;
            }
            var result = new byte[length];
            Buffer.BlockCopy(output, 0, result, 0, length);
            return result;
        }

        private static int ReadInt(Stream stream)
        {
            var bytes = new byte[4];
            if (stream.Read(bytes, 0, 4) != 4) {
                throw new EndOfStreamException();
            }
            return ReadInt(bytes);
        }

        private static int ReadInt(byte[] bytes)
        {
            return ReadInt(bytes, 0);
        }

        private static int ReadInt(byte[] bytes, int offset)
        {
            var r = 0;
            for (var i = 0; i < 4; ++i) {
                r = (r << 8) + bytes[offset + i];
            }

            return r;
        }

        private static bool ByteArrayEqual(byte[] a, byte[] b)
        {
            var aLen = a.Length;
            if (aLen != b.Length) {
                return false;
            }
            for (var i = 0; i < aLen; ++i) {
                if (a[i] != b[i]) {
                    return false;
                }
            }
            return true;
        }
    }
}
