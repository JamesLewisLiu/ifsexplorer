using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace IFSExplorer
{
    internal sealed class KBinNode
    {
        internal readonly string Name;
        internal readonly KBinNode Parent;
        internal readonly List<KBinNode> Children = new List<KBinNode>();
        internal readonly Dictionary<string, string> Attributes = new Dictionary<string, string>();
        internal long[] IntegerValues;
        internal string StringValue;

        internal KBinNode(string name, KBinNode parent)
        {
            Name = name;
            Parent = parent;
        }

        internal string GetAttribute(string name)
        {
            string value;
            return Attributes.TryGetValue(name, out value) ? value : null;
        }

        internal KBinNode FindChild(string name)
        {
            foreach (var child in Children) {
                if (child.Name == name) {
                    return child;
                }
            }
            return null;
        }
    }

    /// <summary>
    /// Minimal reader for Konami's binary XML format. It intentionally only
    /// models the values IFS manifests and texture lists need.
    /// </summary>
    internal sealed class KBinXmlReader
    {
        private const byte Signature = 0xA0;
        private const byte CompressedSignature = 0x42;
        private const byte UncompressedSignature = 0x45;
        private const int AttributeType = 46;
        private const int NodeEndType = 190;
        private const int EndSectionType = 191;
        private const string SixBitCharacters = "0123456789:ABCDEFGHIJKLMNOPQRSTUVWXYZ_abcdefghijklmnopqrstuvwxyz";

        private readonly byte[] _bytes;
        private readonly Encoding _encoding;
        private readonly bool _compressedNames;
        private readonly int _nodeEnd;
        private int _nodeOffset;
        private int _dataOffset;
        private int _byteDataOffset;
        private int _wordDataOffset;

        private KBinXmlReader(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 12 || bytes[0] != Signature) {
                throw new InvalidDataException("Invalid KBinXML header.");
            }

            if (bytes[1] != CompressedSignature && bytes[1] != UncompressedSignature) {
                throw new InvalidDataException("Unsupported KBinXML name encoding.");
            }

            if ((byte) (bytes[2] ^ bytes[3]) != 0xff) {
                throw new InvalidDataException("Invalid KBinXML encoding marker.");
            }

            _bytes = bytes;
            _compressedNames = bytes[1] == CompressedSignature;
            _encoding = GetEncoding(bytes[2]);
            _nodeEnd = checked((int) ReadUInt32(bytes, 4) + 8);
            if (_nodeEnd < 8 || _nodeEnd + 4 > bytes.Length) {
                throw new InvalidDataException("KBinXML node section exceeds the input.");
            }

            _nodeOffset = 8;
            _dataOffset = _nodeEnd;
            var dataSize = ReadDataUInt32();
            if (dataSize > bytes.Length - _dataOffset) {
                throw new InvalidDataException("KBinXML data section exceeds the input.");
            }
            _byteDataOffset = _nodeEnd;
            _wordDataOffset = _nodeEnd;
        }

        internal static KBinNode Parse(byte[] bytes)
        {
            return new KBinXmlReader(bytes).ReadTree();
        }

        internal static bool IsBinaryXml(byte[] bytes)
        {
            return bytes != null && bytes.Length >= 2 && bytes[0] == Signature &&
                   (bytes[1] == CompressedSignature || bytes[1] == UncompressedSignature);
        }

        private KBinNode ReadTree()
        {
            KBinNode root = null;
            KBinNode current = null;

            while (_nodeOffset < _nodeEnd) {
                while (_nodeOffset < _nodeEnd && _bytes[_nodeOffset] == 0) {
                    ++_nodeOffset;
                }
                if (_nodeOffset >= _nodeEnd) {
                    break;
                }

                var rawType = ReadNodeByte();
                var isArray = (rawType & 0x40) != 0;
                var nodeType = rawType & ~0x40;

                if (nodeType == NodeEndType) {
                    if (current != null) {
                        current = current.Parent;
                    }
                    continue;
                }
                if (nodeType == EndSectionType) {
                    break;
                }

                var name = ReadNodeName();
                if (nodeType == AttributeType) {
                    if (current == null) {
                        throw new InvalidDataException("KBinXML attribute has no owning node.");
                    }
                    current.Attributes[name] = ReadDataString();
                    continue;
                }

                var child = new KBinNode(name, current);
                if (current != null) {
                    current.Children.Add(child);
                } else if (root == null) {
                    root = child;
                } else {
                    throw new InvalidDataException("KBinXML contains multiple root nodes.");
                }
                current = child;

                if (nodeType == 1) {
                    continue;
                }

                ReadNodeValue(child, nodeType, isArray);
            }

            if (root == null) {
                throw new InvalidDataException("KBinXML contains no root node.");
            }
            return root;
        }

        private void ReadNodeValue(KBinNode node, int nodeType, bool isArray)
        {
            int scalarSize;
            int scalarCount;
            GetTypeLayout(nodeType, out scalarSize, out scalarCount);

            if (scalarCount == -1) {
                var length = checked((int) ReadDataUInt32());
                var data = ReadDataBytes(length);
                AlignData(4);
                if (nodeType == 11) {
                    var stringLength = data.Length;
                    while (stringLength > 0 && data[stringLength - 1] == 0) {
                        --stringLength;
                    }
                    node.StringValue = _encoding.GetString(data, 0, stringLength);
                }
                return;
            }

            var valueCount = scalarCount;
            byte[] raw;
            if (isArray) {
                var byteLength = checked((int) ReadDataUInt32());
                if (byteLength % (scalarSize * scalarCount) != 0) {
                    throw new InvalidDataException("KBinXML array has an invalid byte length.");
                }
                valueCount = byteLength / scalarSize;
                raw = ReadDataBytes(byteLength);
                AlignData(4);
            } else {
                raw = ReadAlignedValueBytes(scalarSize * scalarCount);
            }

            if (IsIntegerType(nodeType)) {
                var values = new long[valueCount];
                var signed = IsSignedType(nodeType);
                for (var i = 0; i < valueCount; ++i) {
                    values[i] = ReadInteger(raw, i * scalarSize, scalarSize, signed);
                }
                node.IntegerValues = values;
            }
        }

        private byte[] ReadAlignedValueBytes(int byteCount)
        {
            int offset;
            if (byteCount == 1) {
                if ((_byteDataOffset & 3) == 0) {
                    _byteDataOffset = _dataOffset;
                }
                offset = _byteDataOffset;
                _byteDataOffset += byteCount;
            } else if (byteCount == 2) {
                if ((_wordDataOffset & 3) == 0) {
                    _wordDataOffset = _dataOffset;
                }
                offset = _wordDataOffset;
                _wordDataOffset += byteCount;
            } else {
                offset = _dataOffset;
                _dataOffset += byteCount;
                AlignData(4);
            }

            var trailing = Math.Max(_byteDataOffset, _wordDataOffset);
            if (_dataOffset < trailing) {
                _dataOffset = trailing;
                AlignData(4);
            }
            return CopyBytes(offset, byteCount);
        }

        private string ReadDataString()
        {
            var length = checked((int) ReadDataUInt32());
            var data = ReadDataBytes(length);
            AlignData(4);
            var stringLength = data.Length;
            while (stringLength > 0 && data[stringLength - 1] == 0) {
                --stringLength;
            }
            return _encoding.GetString(data, 0, stringLength);
        }

        private string ReadNodeName()
        {
            if (!_compressedNames) {
                var length = (ReadNodeByte() & ~0x40) + 1;
                var bytes = ReadNodeBytes(length);
                return _encoding.GetString(bytes);
            }

            var characterCount = ReadNodeByte();
            var byteCount = (characterCount * 6 + 7) / 8;
            var packed = ReadNodeBytes(byteCount);
            var chars = new char[characterCount];
            for (var i = 0; i < characterCount; ++i) {
                var value = 0;
                for (var bit = 0; bit < 6; ++bit) {
                    var bitIndex = i * 6 + bit;
                    value = (value << 1) | ((packed[bitIndex / 8] >> (7 - bitIndex % 8)) & 1);
                }
                if (value >= SixBitCharacters.Length) {
                    throw new InvalidDataException("KBinXML node name contains an invalid character.");
                }
                chars[i] = SixBitCharacters[value];
            }
            return new string(chars);
        }

        private byte ReadNodeByte()
        {
            if (_nodeOffset >= _nodeEnd) {
                throw new EndOfStreamException("Unexpected end of KBinXML node data.");
            }
            return _bytes[_nodeOffset++];
        }

        private byte[] ReadNodeBytes(int count)
        {
            if (count < 0 || _nodeOffset > _nodeEnd - count) {
                throw new EndOfStreamException("Unexpected end of KBinXML node data.");
            }
            var result = CopyBytes(_nodeOffset, count);
            _nodeOffset += count;
            return result;
        }

        private uint ReadDataUInt32()
        {
            var value = ReadUInt32(_bytes, _dataOffset);
            _dataOffset += 4;
            return value;
        }

        private byte[] ReadDataBytes(int count)
        {
            if (count < 0 || _dataOffset > _bytes.Length - count) {
                throw new EndOfStreamException("Unexpected end of KBinXML value data.");
            }
            var result = CopyBytes(_dataOffset, count);
            _dataOffset += count;
            return result;
        }

        private byte[] CopyBytes(int offset, int count)
        {
            if (offset < 0 || count < 0 || offset > _bytes.Length - count) {
                throw new EndOfStreamException("Unexpected end of KBinXML input.");
            }
            var result = new byte[count];
            Buffer.BlockCopy(_bytes, offset, result, 0, count);
            return result;
        }

        private void AlignData(int alignment)
        {
            _dataOffset = (_dataOffset + alignment - 1) & ~(alignment - 1);
        }

        private static Encoding GetEncoding(byte marker)
        {
            switch (marker) {
                case 0x00:
                case 0x80:
                    return Encoding.GetEncoding(932);
                case 0x20:
                    return Encoding.ASCII;
                case 0x40:
                    return Encoding.GetEncoding("iso-8859-1");
                case 0x60:
                    return Encoding.GetEncoding("euc-jp");
                case 0xA0:
                    return Encoding.UTF8;
                default:
                    throw new InvalidDataException("Unsupported KBinXML text encoding.");
            }
        }

        private static void GetTypeLayout(int nodeType, out int scalarSize, out int scalarCount)
        {
            if (nodeType == 10 || nodeType == 11) {
                scalarSize = 1;
                scalarCount = -1;
                return;
            }

            if (nodeType >= 2 && nodeType <= 15) {
                scalarCount = 1;
                if (nodeType == 2 || nodeType == 3) scalarSize = 1;
                else if (nodeType == 4 || nodeType == 5) scalarSize = 2;
                else if (nodeType == 8 || nodeType == 9 || nodeType == 15) scalarSize = 8;
                else scalarSize = 4;
                return;
            }

            if (nodeType >= 16 && nodeType <= 45) {
                scalarCount = 2 + (nodeType - 16) / 10;
                var member = (nodeType - 16) % 10;
                if (member <= 1) scalarSize = 1;
                else if (member <= 3) scalarSize = 2;
                else if (member <= 5 || member == 8) scalarSize = 4;
                else scalarSize = 8;
                return;
            }

            switch (nodeType) {
                case 48: scalarSize = 1; scalarCount = 16; return;
                case 49: scalarSize = 1; scalarCount = 16; return;
                case 50: scalarSize = 2; scalarCount = 8; return;
                case 51: scalarSize = 2; scalarCount = 8; return;
                case 52: scalarSize = 1; scalarCount = 1; return;
                case 53: scalarSize = 1; scalarCount = 2; return;
                case 54: scalarSize = 1; scalarCount = 3; return;
                case 55: scalarSize = 1; scalarCount = 4; return;
                case 56: scalarSize = 1; scalarCount = 16; return;
                default:
                    throw new NotSupportedException("Unsupported KBinXML value type " + nodeType + ".");
            }
        }

        private static bool IsIntegerType(int nodeType)
        {
            return nodeType != 14 && nodeType != 15 &&
                   nodeType != 24 && nodeType != 25 && nodeType != 34 &&
                   nodeType != 35 && nodeType != 44 && nodeType != 45;
        }

        private static bool IsSignedType(int nodeType)
        {
            if (nodeType == 52 || (nodeType >= 53 && nodeType <= 56)) return true;
            if (nodeType >= 2 && nodeType <= 9) return (nodeType & 1) == 0;
            if (nodeType >= 16 && nodeType <= 43) return ((nodeType - 16) % 2) == 0;
            return nodeType == 48 || nodeType == 50;
        }

        private static long ReadInteger(byte[] bytes, int offset, int size, bool signed)
        {
            ulong value = 0;
            for (var i = 0; i < size; ++i) {
                value = (value << 8) | bytes[offset + i];
            }
            if (signed && size < 8 && (value & (1UL << (size * 8 - 1))) != 0) {
                value |= ulong.MaxValue << (size * 8);
            }
            return unchecked((long) value);
        }

        private static uint ReadUInt32(byte[] bytes, int offset)
        {
            if (offset < 0 || offset > bytes.Length - 4) {
                throw new EndOfStreamException("Unexpected end of KBinXML input.");
            }
            return ((uint) bytes[offset] << 24) | ((uint) bytes[offset + 1] << 16) |
                   ((uint) bytes[offset + 2] << 8) | bytes[offset + 3];
        }
    }
}
