using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace IFSExplorer
{
    /// <summary>
    /// IFS reader ported from ifstools' header, manifest and texture handling.
    /// </summary>
    internal static class IFSArchiveReader
    {
        private const uint Signature = 0x6CAD8F89;

        internal static IList<FileIndex> Read(Stream stream)
        {
            if (stream == null || !stream.CanRead || !stream.CanSeek) {
                throw new ArgumentException("IFS input must be a readable, seekable stream.", "stream");
            }
            if (stream.Length < 20) {
                throw new InvalidDataException("IFS header is truncated.");
            }

            stream.Seek(0, SeekOrigin.Begin);
            var signature = ReadUInt32(stream);
            if (signature != Signature) {
                throw new InvalidDataException("The selected file is not an IFS archive.");
            }

            var version = ReadUInt16(stream);
            var invertedVersion = ReadUInt16(stream);
            if ((ushort) (version ^ invertedVersion) != 0xffff) {
                throw new InvalidDataException("IFS version marker is invalid.");
            }

            ReadUInt32(stream); // timestamp
            ReadUInt32(stream); // in-memory manifest size
            var manifestEnd = ReadUInt32(stream);
            var headerSize = version > 1 ? 36 : 20;
            if (manifestEnd < headerSize || manifestEnd > stream.Length) {
                throw new InvalidDataException("IFS manifest offset is outside the archive.");
            }

            stream.Seek(headerSize, SeekOrigin.Begin);
            var manifest = ReadExactly(stream, checked((int) (manifestEnd - headerSize)));
            var root = KBinXmlReader.Parse(manifest);

            var entries = new List<FileIndex>();
            ReadEntries(root, string.Empty, stream, manifestEnd, stream.Length - manifestEnd, entries);
            if (entries.Count == 0) {
                throw new InvalidDataException("IFS manifest contains no local files.");
            }

            ApplyTextureMetadata(entries);
            var images = new List<FileIndex>();
            foreach (var entry in entries) {
                if (entry.Texture != null) {
                    images.Add(entry);
                }
            }
            return images.Count == 0 ? entries : images;
        }

        private static void ReadEntries(KBinNode node, string path, Stream stream, long dataOffset,
                                        long dataLength, List<FileIndex> entries)
        {
            var name = FixName(node.Name);
            var currentPath = path;
            if (node.Parent != null && name != "_info_") {
                currentPath = path.Length == 0 ? name : path + "/" + name;
            }

            var values = node.IntegerValues;
            if (values != null && (values.Length == 2 || values.Length == 3) &&
                values[0] >= 0 && values[1] > 0 && values[0] <= dataLength - values[1]) {
                var filePath = currentPath;
                var slash = filePath.LastIndexOf('/');
                var fileName = slash < 0 ? filePath : filePath.Substring(slash + 1);
                entries.Add(new FileIndex(stream, dataOffset + values[0], checked((int) values[1]),
                                          entries.Count, fileName, filePath));
                return;
            }

            foreach (var child in node.Children) {
                ReadEntries(child, currentPath, stream, dataOffset, dataLength, entries);
            }
        }

        private static void ApplyTextureMetadata(List<FileIndex> entries)
        {
            foreach (var candidate in entries) {
                if (!IsTextureManifestCandidate(candidate)) {
                    continue;
                }

                try {
                    var bytes = candidate.Read();
                    if (!KBinXmlReader.IsBinaryXml(bytes)) {
                        continue;
                    }
                    var root = KBinXmlReader.Parse(bytes);
                    var compression = root.GetAttribute("compress");
                    ApplyTextureNodes(root, compression, entries);
                    if (HasTextureMetadata(entries)) {
                        return;
                    }
                } catch (Exception) {
                    // Some IFS archives contain unrelated binary XML files in tex.
                    // Try the next candidate before giving up on explicit metadata.
                }
            }
        }

        private static bool IsTextureManifestCandidate(FileIndex entry)
        {
            var path = entry.FullPath.Replace('\\', '/');
            return (path.StartsWith("tex/", StringComparison.OrdinalIgnoreCase) ||
                    path.IndexOf("/tex/", StringComparison.OrdinalIgnoreCase) >= 0) &&
                   path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);
        }

        private static void ApplyTextureNodes(KBinNode root, string compression, List<FileIndex> entries)
        {
            foreach (var textureSet in root.Children) {
                var format = textureSet.GetAttribute("format");
                if (format == null) {
                    continue;
                }

                foreach (var image in textureSet.Children) {
                    if (image.Name != "image") {
                        continue;
                    }
                    var imageName = image.GetAttribute("name");
                    var imageRect = image.FindChild("imgrect");
                    if (imageName == null || imageRect == null || imageRect.IntegerValues == null ||
                        imageRect.IntegerValues.Length < 4) {
                        continue;
                    }

                    var values = imageRect.IntegerValues;
                    var width = checked((int) ((values[1] - values[0]) / 2));
                    var height = checked((int) ((values[3] - values[2]) / 2));
                    if (width <= 0 || height <= 0) {
                        continue;
                    }

                    var hashedName = Md5Name(imageName);
                    foreach (var entry in entries) {
                        if (entry.Name.Equals(hashedName, StringComparison.OrdinalIgnoreCase) ||
                            entry.Name.Equals(imageName, StringComparison.OrdinalIgnoreCase) ||
                            entry.Name.Equals(imageName + ".png", StringComparison.OrdinalIgnoreCase)) {
                            entry.Texture = new TextureInfo(imageName, format, compression, width, height);
                            break;
                        }
                    }
                }
            }
        }

        private static bool HasTextureMetadata(List<FileIndex> entries)
        {
            foreach (var entry in entries) {
                if (entry.Texture != null) {
                    return true;
                }
            }
            return false;
        }

        private static string Md5Name(string value)
        {
            using (var md5 = MD5.Create()) {
                var hash = md5.ComputeHash(Encoding.GetEncoding(932).GetBytes(value));
                var result = new StringBuilder(hash.Length * 2);
                foreach (var b in hash) {
                    result.Append(b.ToString("x2"));
                }
                return result.ToString();
            }
        }

        private static string FixName(string name)
        {
            var result = name.Replace("_E", ".").Replace("__", "_");
            if (result.Length > 1 && result[0] == '_' && char.IsDigit(result[1])) {
                result = result.Substring(1);
            }
            return result;
        }

        private static ushort ReadUInt16(Stream stream)
        {
            var high = stream.ReadByte();
            var low = stream.ReadByte();
            if (low < 0) {
                throw new EndOfStreamException("IFS header is truncated.");
            }
            return (ushort) ((high << 8) | low);
        }

        private static uint ReadUInt32(Stream stream)
        {
            var bytes = ReadExactly(stream, 4);
            return ((uint) bytes[0] << 24) | ((uint) bytes[1] << 16) |
                   ((uint) bytes[2] << 8) | bytes[3];
        }

        private static byte[] ReadExactly(Stream stream, int count)
        {
            var result = new byte[count];
            var offset = 0;
            while (offset < count) {
                var read = stream.Read(result, offset, count - offset);
                if (read == 0) {
                    throw new EndOfStreamException("IFS data is truncated.");
                }
                offset += read;
            }
            return result;
        }
    }
}
