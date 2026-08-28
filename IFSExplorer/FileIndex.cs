using System.IO;

namespace IFSExplorer
{
    internal class FileIndex {
        private readonly Stream _stream;

        private readonly long _index;
        internal readonly int Size;
        internal readonly int EntryNumber;
        internal readonly string Name;
        internal readonly string FullPath;
        internal TextureInfo Texture;

        internal FileIndex(Stream stream, int index, int size, int entryNumber)
            : this(stream, index, size, entryNumber, "#" + entryNumber, "#" + entryNumber)
        {
        }

        internal FileIndex(Stream stream, long index, int size, int entryNumber, string name, string fullPath)
        {
            _stream = stream;
            EntryNumber = entryNumber;
            Size = size;
            _index = index;
            Name = name;
            FullPath = fullPath;
        }

        internal byte[] Read()
        {
            _stream.Seek(_index, SeekOrigin.Begin);
            var r = new byte[Size];
            var offset = 0;
            while (offset < Size) {
                var read = _stream.Read(r, offset, Size - offset);
                if (read == 0) {
                    throw new EndOfStreamException("IFS file data is truncated.");
                }
                offset += read;
            }
            return r;
        }
    }
}
