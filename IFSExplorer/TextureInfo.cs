namespace IFSExplorer
{
    internal sealed class TextureInfo
    {
        internal readonly string Name;
        internal readonly string Format;
        internal readonly string Compression;
        internal readonly int Width;
        internal readonly int Height;

        internal TextureInfo(string name, string format, string compression, int width, int height)
        {
            Name = name;
            Format = format;
            Compression = compression;
            Width = width;
            Height = height;
        }
    }
}
