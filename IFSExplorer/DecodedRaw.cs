using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace IFSExplorer
{
    internal class DecodedRaw
    {
        internal readonly int RawLength;
        private readonly int _offset;
        private readonly int[] _argbArr;
        private readonly int[] _widths;
        private readonly int[] _heights;

        internal int IndexSize { get { return _widths.Length; } }

        internal DecodedRaw(int rawLength, int offset, int[] argbArr, int[] widths, int[] heights)
        {
            RawLength = rawLength;
            _heights = heights;
            _widths = widths;
            _argbArr = argbArr;
            _offset = offset;
        }

        internal DecodedRaw(int rawLength, int[] argbArr, int width, int height)
            : this(rawLength, 0, argbArr, new[] {width}, new[] {height})
        {
        }

        internal Point GetSize(int index)
        {
            return new Point(_widths[index], _heights[index]);
        }

        internal int GetARGB(int index, int x, int y)
        {
            return _argbArr[(y*_widths[index]) + x + _offset];
        }

        internal Bitmap ToBitmap(int index)
        {
            var width = _widths[index];
            var height = _heights[index];
            var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            var data = bitmap.LockBits(new Rectangle(0, 0, width, height),
                                       ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try {
                if (_offset == 0 && data.Stride == width * 4) {
                    Marshal.Copy(_argbArr, 0, data.Scan0, width * height);
                } else {
                    for (var y = 0; y < height; ++y) {
                        var destination = new IntPtr(data.Scan0.ToInt64() + y * data.Stride);
                        Marshal.Copy(_argbArr, _offset + y * width, destination, width);
                    }
                }
            } finally {
                bitmap.UnlockBits(data);
            }
            return bitmap;
        }
    }
}
