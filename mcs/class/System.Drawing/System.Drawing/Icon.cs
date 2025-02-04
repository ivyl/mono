//
// System.Drawing.Icon.cs
//
// Authors:
//   Gary Barnett (gary.barnett.mono@gmail.com)
//   Dennis Hayes (dennish@Raytek.com)
//   Andreas Nahr (ClassDevelopment@A-SoftTech.com)
//   Sanjay Gupta (gsanjay@novell.com)
//   Peter Dennis Bartok (pbartok@novell.com)
//   Sebastien Pouliot  <sebastien@ximian.com>
//
// Copyright (C) 2002 Ximian, Inc. http://www.ximian.com
// Copyright (C) 2004-2008 Novell, Inc (http://www.novell.com)
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//

using System.Collections;
using System.ComponentModel;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.InteropServices;

namespace System.Drawing
{
	[Serializable]	
#if !MONOTOUCH
	[Editor ("System.Drawing.Design.IconEditor, " + Consts.AssemblySystem_Drawing_Design, typeof (System.Drawing.Design.UITypeEditor))]
#endif
	[TypeConverter(typeof(IconConverter))]

	public sealed class Icon : MarshalByRefObject, ISerializable, ICloneable, IDisposable
	{
		[StructLayout(LayoutKind.Sequential)]
		internal struct IconDirEntry {		
			internal byte	width;		// Width of icon
			internal byte	height;		// Height of icon
			internal byte	colorCount;	// colors in icon 
			internal byte	reserved;	// Reserved
			internal ushort planes;         // Color Planes
			internal ushort	bitCount;       // Bits per pixel
			internal uint	bytesInRes;     // bytes in resource
			internal uint	imageOffset;	// position in file 
			internal bool	ignore;		// for unsupported images (vista 256 png)
		}; 

		[StructLayout(LayoutKind.Sequential)]
		internal struct IconDir {
			internal ushort			idReserved;   // Reserved
			internal ushort			idType;       // resource type (1 for icons)
			internal ushort			idCount;      // how many images?
			internal IconDirEntry []	idEntries;    // the entries for each image
		};
		
		[StructLayout(LayoutKind.Sequential)]
		internal struct BitmapInfoHeader {
            		internal uint	biSize; 
			internal int	biWidth; 
			internal int	biHeight; 
			internal ushort	biPlanes; 
			internal ushort	biBitCount; 
			internal uint	biCompression; 
			internal uint	biSizeImage; 
			internal int	biXPelsPerMeter; 
			internal int	biYPelsPerMeter; 
			internal uint	biClrUsed; 
			internal uint	biClrImportant; 
		};

		[StructLayout(LayoutKind.Sequential)]	// added baseclass for non bmp image format support
		internal abstract class ImageData {
		};

		[StructLayout(LayoutKind.Sequential)]
		internal class IconImage : ImageData {
			internal BitmapInfoHeader	iconHeader;	//image header
			internal uint []		iconColors;	//colors table
			internal byte []		iconXOR;	// bits for XOR mask
			internal byte []		iconAND;	//bits for AND mask
		};

		[StructLayout(LayoutKind.Sequential)]
		internal class AlphaImage : ImageData {
			internal BitmapInfoHeader	iconHeader;	//image header
			internal byte []		iconARGB;	// ARGB bits for icon
		};

		[StructLayout(LayoutKind.Sequential)]
		internal class IconDump : ImageData {
			internal byte []		data;
		};

		private Size iconSize;
		private IntPtr handle = IntPtr.Zero;
		private IconDir	iconDir;
		private ushort id;
		private ImageData [] imageData;
		private bool undisposable;
		private bool disposed;
		private Bitmap bitmap;

		private Icon ()
		{
		}

#if !MONOTOUCH
		private Icon (IntPtr handle)
		{
			this.handle = handle;
			bitmap = Bitmap.FromHicon (handle);
			iconSize = new Size (bitmap.Width, bitmap.Height);
			if (GDIPlus.RunningOnUnix ()) {
				bitmap = Bitmap.FromHicon (handle);
				iconSize = new Size (bitmap.Width, bitmap.Height);
				// FIXME: we need to convert the bitmap into an icon
			} else {
				IconInfo ii;
				GDIPlus.GetIconInfo (handle, out ii);

				// If this structure defines an icon, the hot spot is always in the center of the icon
				iconSize = new Size (ii.xHotspot * 2, ii.yHotspot * 2);
				bitmap = (Bitmap) Image.FromHbitmap (ii.hbmColor);
			}
			undisposable = true;
		}
#endif

		public Icon (Icon original, int width, int height)
			: this (original, new Size (width, height))
		{
		}

		public Icon (Icon original, Size size)
		{
			if (original == null)
				throw new ArgumentException ("original");

			iconSize = size;
			iconDir = original.iconDir;
			
			int count = iconDir.idCount;
			if (count > 0) {
				imageData = original.imageData;
				id = UInt16.MaxValue;

				for (ushort i=0; i < count; i++) {
					IconDirEntry ide = iconDir.idEntries [i];
					if (((ide.height == size.Height) || (ide.width == size.Width)) && !ide.ignore) {
						id = i;
						break;
					}
				}

				// if a perfect match isn't found we look for the biggest icon *smaller* than specified
				if (id == UInt16.MaxValue) { 
					int requested = Math.Min (size.Height, size.Width);
					// previously best set to 1st image, as this might not be smallest changed loop to check all
					IconDirEntry? best = null; 
					for (ushort i=0; i < count; i++) {
						IconDirEntry ide = iconDir.idEntries [i];
						if (((ide.height < requested) || (ide.width < requested)) && !ide.ignore) {
							if (best == null) {
								best = ide;
								id = i;
							} else if ((ide.height > best.Value.height) || (ide.width > best.Value.width)) {
								best = ide;
								id = i;
							}
						}
					}
				}

				// last one, if nothing better can be found
				if (id == UInt16.MaxValue) {
					int i = count;
					while (id == UInt16.MaxValue && i > 0) {
						i--;
						if (!iconDir.idEntries [i].ignore)
							id = (ushort) i;
					}
				}

				if (id == UInt16.MaxValue)
					throw new ArgumentException ("Icon", "No valid icon image found");

				iconSize.Height = iconDir.idEntries [id].height;
				iconSize.Width = iconDir.idEntries [id].width;
			} else {
				iconSize.Height = size.Height;
				iconSize.Width = size.Width;
			}

			if (original.bitmap != null)
				bitmap = (Bitmap) original.bitmap.Clone ();
		}

		public Icon (Stream stream) : this (stream, 32, 32) 
		{
		}

		public Icon (Stream stream, int width, int height)
		{
			InitFromStreamWithSize (stream, width, height);
		}

		public Icon (string fileName)
		{
			using (FileStream fs = File.OpenRead (fileName)) {
				InitFromStreamWithSize (fs, 32, 32);
			}
		}

		public Icon (Type type, string resource)
		{
			if (resource == null)
				throw new ArgumentException ("resource");

			// For compatibility with the .NET Framework
			if (type == null)
				throw new NullReferenceException();

			using (Stream s = type.GetTypeInfo ().Assembly.GetManifestResourceStream (type, resource)) {
				if (s == null) {
					string msg = Locale.GetText ("Resource '{0}' was not found.", resource);
					throw new FileNotFoundException (msg);
				}
				InitFromStreamWithSize (s, 32, 32);		// 32x32 is default
			}
		}

		private Icon (SerializationInfo info, StreamingContext context)
		{
			MemoryStream dataStream = null;
			int width=0;
			int height=0;
			foreach (SerializationEntry serEnum in info) {
				if (String.Compare(serEnum.Name, "IconData", true) == 0) {
					dataStream = new MemoryStream ((byte []) serEnum.Value);
				}
				if (String.Compare(serEnum.Name, "IconSize", true) == 0) {
					Size iconSize = (Size) serEnum.Value;
					width = iconSize.Width;
					height = iconSize.Height;
				}
			}
			if (dataStream != null) {
				dataStream.Seek (0, SeekOrigin.Begin);
				InitFromStreamWithSize (dataStream, width, height);
			}
                }

		internal Icon (string resourceName, bool undisposable)
		{
			using (Stream s = typeof (Icon).GetTypeInfo ().Assembly.GetManifestResourceStream (resourceName)) {
				if (s == null) {
					string msg = Locale.GetText ("Resource '{0}' was not found.", resourceName);
					throw new FileNotFoundException (msg);
				}
				InitFromStreamWithSize (s, 32, 32);		// 32x32 is default
			}
			this.undisposable = true;
		}

		void ISerializable.GetObjectData(SerializationInfo si, StreamingContext context)
		{
			MemoryStream ms = new MemoryStream ();
			Save (ms);
			si.AddValue ("IconSize", this.Size, typeof (Size));
			si.AddValue ("IconData", ms.ToArray ());
		}

		public Icon (Stream stream, Size size) : 
			this (stream, size.Width, size.Height)
		{
		}
		
		public Icon (string fileName, int width, int height)
		{
			using (FileStream fs = File.OpenRead (fileName)) {
				InitFromStreamWithSize (fs, width, height);
			}
		}
	
		public Icon (string fileName, Size size)
		{
			using (FileStream fs = File.OpenRead (fileName)) {
				InitFromStreamWithSize (fs, size.Width, size.Height);
			}
		}

		[MonoLimitation ("The same icon, SystemIcons.WinLogo, is returned for all file types.")]
		public static Icon ExtractAssociatedIcon (string filePath)
		{
			if (String.IsNullOrEmpty (filePath))
				throw new ArgumentException (Locale.GetText ("Null or empty path."), "filePath");
			if (!File.Exists (filePath))
				throw new FileNotFoundException (Locale.GetText ("Couldn't find specified file."), filePath);

			return SystemIcons.WinLogo;
		}	

		public void Dispose ()
		{
			// SystemIcons requires this
			if (undisposable)
				return;
			
			if (!disposed) {
#if !MONOTOUCH
				if (GDIPlus.RunningOnWindows () && (handle != IntPtr.Zero)) {
					GDIPlus.DestroyIcon (handle);
					handle = IntPtr.Zero;
				}
#endif
				if (bitmap != null) {
					bitmap.Dispose ();
					bitmap = null;
				}
				GC.SuppressFinalize (this);
			}
			disposed = true;
		}

		public object Clone ()
		{
			return new Icon (this, Size);
		}
		
#if !MONOTOUCH
		public static Icon FromHandle (IntPtr handle)
		{
			if (handle == IntPtr.Zero)
				throw new ArgumentException ("handle");

			return new Icon (handle);
		}
#endif
		private void SaveIconImage (BinaryWriter writer, IconImage ii)
		{
			BitmapInfoHeader bih = ii.iconHeader;
			writer.Write (bih.biSize);
			writer.Write (bih.biWidth);
			writer.Write (bih.biHeight);
			writer.Write (bih.biPlanes);
			writer.Write (bih.biBitCount);
			writer.Write (bih.biCompression);
			writer.Write (bih.biSizeImage);
			writer.Write (bih.biXPelsPerMeter);
			writer.Write (bih.biYPelsPerMeter);
			writer.Write (bih.biClrUsed);
			writer.Write (bih.biClrImportant);

			//now write color table
			int colCount = ii.iconColors.Length;
			for (int j=0; j < colCount; j++)
				writer.Write (ii.iconColors [j]);

			//now write XOR Mask
			writer.Write (ii.iconXOR);

			//now write AND Mask
			writer.Write (ii.iconAND);
		}

		private void SaveAlphaImage (BinaryWriter writer, AlphaImage ii)
		{
			BitmapInfoHeader bih = ii.iconHeader;
			writer.Write (bih.biSize);
			writer.Write (bih.biWidth);
			writer.Write (bih.biHeight);
			writer.Write (bih.biPlanes);
			writer.Write (bih.biBitCount);
			writer.Write (bih.biCompression);
			writer.Write (bih.biSizeImage);
			writer.Write (bih.biXPelsPerMeter);
			writer.Write (bih.biYPelsPerMeter);
			writer.Write (bih.biClrUsed);
			writer.Write (bih.biClrImportant);

			//now write bits
			writer.Write (ii.iconARGB);
		}

		private void WriteImageData (BinaryWriter writer, ImageData i)
		{
			if (i is IconDump)
				SaveIconDump (writer, (IconDump) i);
			else if (i is AlphaImage)
				SaveAlphaImage (writer, (AlphaImage) i);
			else
				SaveIconImage (writer, (IconImage) i);
		}

		private void SaveIconDump (BinaryWriter writer, IconDump id)
		{
			writer.Write (id.data);
		}

		private void SaveIconDirEntry (BinaryWriter writer, IconDirEntry ide, uint offset)
		{
			writer.Write (ide.width);
			writer.Write (ide.height);
			writer.Write (ide.colorCount);
			writer.Write (ide.reserved);
			writer.Write (ide.planes);
			writer.Write (ide.bitCount);
			writer.Write (ide.bytesInRes);
			writer.Write ((offset == UInt32.MaxValue) ? ide.imageOffset : offset);
		}

		private void SaveAll (BinaryWriter writer)
		{
			writer.Write (iconDir.idReserved);
			writer.Write (iconDir.idType);
			ushort count = iconDir.idCount;
			writer.Write (count);

			for (int i=0; i < (int)count; i++) {
				SaveIconDirEntry (writer, iconDir.idEntries [i], UInt32.MaxValue);
			}

			for (int i=0; i < (int)count; i++) {

				//FIXME: HACK: 1 (out of the 8) vista type icons had additional bytes (value:0)
				//between images. This fixes the issue, but perhaps shouldnt include in production?
				while (writer.BaseStream.Length < iconDir.idEntries[i].imageOffset)
					writer.Write ((byte) 0);

				WriteImageData (writer, imageData [i]);
			}
		}

		// TODO: check image not ignored (presently this method doesnt seem to be called unless width/height 
		// refer to image)
		private void SaveBestSingleIcon (BinaryWriter writer, int width, int height)
		{
			writer.Write (iconDir.idReserved);
			writer.Write (iconDir.idType);
			writer.Write ((ushort)1);

			// find best entry and save it
			int best = 0;
			int bitCount = 0;
			for (int i=0; i < iconDir.idCount; i++) {
				IconDirEntry ide = iconDir.idEntries [i];
				if ((width == ide.width) && (height == ide.height)) {
					if (ide.bitCount >= bitCount) {
						bitCount = ide.bitCount;
						best = i;
					}
				}
			}

			SaveIconDirEntry (writer, iconDir.idEntries [best], 22);
			WriteImageData (writer, imageData [best]);
		}

		private void SaveBitmapAsIcon (BinaryWriter writer)
		{
			writer.Write ((ushort)0);	// idReserved must be 0
			writer.Write ((ushort)1);	// idType must be 1
			writer.Write ((ushort)1);	// only one icon

			// when transformed into a bitmap only a single image exists
			IconDirEntry ide = new IconDirEntry ();
			ide.width = (byte) bitmap.Width;
			ide.height = (byte) bitmap.Height;
			ide.colorCount = 0;	// 32 bbp == 0, for palette size
			ide.reserved = 0;	// always 0
			ide.planes = 0;
			ide.bitCount = 32;
			ide.imageOffset = 22;	// 22 is the first icon position (for single icon files)

			BitmapInfoHeader bih = new BitmapInfoHeader ();
			bih.biSize = (uint) Marshal.SizeOf (typeof (BitmapInfoHeader));
			bih.biWidth = bitmap.Width;
			bih.biHeight = 2 * bitmap.Height; // include both XOR and AND images
			bih.biPlanes = 1;
			bih.biBitCount = 32;
			bih.biCompression = 0;
			bih.biSizeImage = 0;
			bih.biXPelsPerMeter = 0;
			bih.biYPelsPerMeter = 0;
			bih.biClrUsed = 0;
			bih.biClrImportant = 0;

			// FIXME: Write as AlphaImage ?
			IconImage ii = new IconImage ();
			ii.iconHeader = bih;
			ii.iconColors = new uint [0];	// no palette
			int xor_size = (((bih.biBitCount * bitmap.Width + 31) & ~31) >> 3) * bitmap.Height;
			ii.iconXOR = new byte [xor_size];
			int p = 0;
			for (int y = bitmap.Height - 1; y >=0; y--) {
				for (int x = 0; x < bitmap.Width; x++) {
					Color c = bitmap.GetPixel (x, y);
					ii.iconXOR [p++] = c.B;
					ii.iconXOR [p++] = c.G;
					ii.iconXOR [p++] = c.R;
					ii.iconXOR [p++] = c.A;
				}
			}
			int and_line_size = (((Width + 31) & ~31) >> 3);	// must be a multiple of 4 bytes
			int and_size = and_line_size * bitmap.Height;
			ii.iconAND = new byte [and_size];

			ide.bytesInRes = (uint) (bih.biSize + xor_size + and_size);

			SaveIconDirEntry (writer, ide, UInt32.MaxValue);
			SaveIconImage (writer, ii);
		}

		private void Save (Stream outputStream, int width, int height)
		{
			BinaryWriter writer = new BinaryWriter (outputStream);
			// if we have the icon information then save from this
			if (iconDir.idEntries != null) {
				if ((width == -1) && (height == -1))
					SaveAll (writer);
				else
					SaveBestSingleIcon (writer, width, height);
			} else if (bitmap != null) {
				// if the icon was created from a bitmap then convert it
				SaveBitmapAsIcon (writer);
			}
			writer.Flush ();
		}

		public void Save (Stream outputStream)
		{
			if (outputStream == null)
				throw new NullReferenceException ("outputStream");

			// save every icons available
			Save (outputStream, -1, -1);
		}
#if !MONOTOUCH
		internal Bitmap BuildBitmapOnWin32 ()
		{
			Bitmap bmp;

			if (imageData == null)
				return new Bitmap (32, 32);

			if (imageData [id] is AlphaImage)
			{
				AlphaImage ai = (AlphaImage) imageData [id];

				BitmapData bd = new BitmapData ();
				bd.Width = ai.iconHeader.biWidth;
				bd.Height = ai.iconHeader.biHeight / 2;
				bd.PixelFormat = PixelFormat.Format32bppArgb;
				bd.Stride = -ai.iconHeader.biWidth * 4;
				bmp = new Bitmap (bd.Width, bd.Height, PixelFormat.Format32bppArgb);

				unsafe {
					fixed (byte* p = ai.iconARGB) {
						bd.Scan0 = (IntPtr)(p + (bd.Width * 4 * (bd.Height - 1)));
						BitmapData bd2 = bmp.LockBits (
							new Rectangle (0, 0, bd.Width, bd.Height),
							ImageLockMode.WriteOnly|ImageLockMode.UserInputBuffer,
							PixelFormat.Format32bppArgb,
							bd);
						bmp.UnlockBits (bd2);
					}
				}

				return bmp;
			}

			IconImage ii = (IconImage) imageData [id];
			BitmapInfoHeader bih = ii.iconHeader;
			int biHeight = bih.biHeight / 2;

			int ncolors = (int)bih.biClrUsed;
			if ((ncolors == 0) && (bih.biBitCount < 24))
				ncolors = (int)(1 << bih.biBitCount);

			switch (bih.biBitCount) {
			case 1:
				bmp = new Bitmap (bih.biWidth, biHeight, PixelFormat.Format1bppIndexed);
				break;
			case 4:
				bmp = new Bitmap (bih.biWidth, biHeight, PixelFormat.Format4bppIndexed);
				break;
			case 8:
				bmp = new Bitmap (bih.biWidth, biHeight, PixelFormat.Format8bppIndexed);
				break;
			case 24:
				bmp = new Bitmap (bih.biWidth, biHeight, PixelFormat.Format24bppRgb);
				break;
			case 32:
				bmp = new Bitmap (bih.biWidth, biHeight, PixelFormat.Format32bppArgb);
				break;
			default:
				string msg = Locale.GetText ("Unexpected number of bits: {0}", bih.biBitCount);
				throw new Exception (msg);
			}

			if (bih.biBitCount < 24) {
				ColorPalette pal = bmp.Palette; // Managed palette

				for (int i = 0; i < ii.iconColors.Length; i++) {
					pal.Entries[i] = Color.FromArgb ((int)ii.iconColors[i] | unchecked((int)0xff000000));
				}
				bmp.Palette = pal;
			}

			int bytesPerLine = (int)((((bih.biWidth * bih.biBitCount) + 31) & ~31) >> 3);
			BitmapData bits = bmp.LockBits (new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.WriteOnly, bmp.PixelFormat);

			for (int y = 0; y < biHeight; y++) {
				Marshal.Copy (ii.iconXOR, bytesPerLine * y, 
					(IntPtr)(bits.Scan0.ToInt64() + bits.Stride * (biHeight - 1 - y)), bytesPerLine);
			}
			
			bmp.UnlockBits (bits);

			bmp = new Bitmap (bmp); // This makes a 32bpp image out of an indexed one

			// Apply the mask to make properly transparent
			bytesPerLine = (int)((((bih.biWidth) + 31) & ~31) >> 3);
			for (int y = 0; y < biHeight; y++) {
				for (int x = 0; x < bih.biWidth / 8; x++) {
					for (int bit = 7; bit >= 0; bit--) {
						if (((ii.iconAND[y * bytesPerLine +x] >> bit) & 1) != 0) {
							bmp.SetPixel (x*8 + 7-bit, biHeight - y - 1, Color.Transparent);
						}
					}
				}
			}

			return bmp;
		}

		internal Bitmap GetInternalBitmap ()
		{
			if (bitmap == null) {
				if (GDIPlus.RunningOnUnix ()) {
					// Mono's libgdiplus doesn't require to keep the stream alive when loading images
					using (MemoryStream ms = new MemoryStream ()) {
						// save the current icon
						Save (ms, Width, Height);
						ms.Position = 0;

						// libgdiplus can now decode icons
						bitmap = (Bitmap) Image.LoadFromStream (ms, false);
					}
				} else {
					// MS GDI+ ICO codec is more limited than the MS Icon class
					// so we can't, reliably, get bitmap using it. We need to do this the "slow" way
					bitmap = BuildBitmapOnWin32 ();
				}
			}
			return bitmap;
		}

		// note: all bitmaps are 32bits ARGB - no matter what the icon format (bitcount) was
		public Bitmap ToBitmap ()
		{
			if (disposed)
				throw new ObjectDisposedException (Locale.GetText ("Icon instance was disposed."));

			// note: we can't return the original image because
			// (a) we have no control over the bitmap instance we return (i.e. it could be disposed)
			// (b) the palette, flags won't match MS results. See MonoTests.System.Drawing.Imaging.IconCodecTest.
			//     Image16 for the differences
			return new Bitmap (GetInternalBitmap ());
		}
#endif
		public override string ToString ()
		{
			//is this correct, this is what returned by .Net
			return "<Icon>";			
		}
		
#if !MONOTOUCH
		[Browsable (false)]
		public IntPtr Handle {
			get {
				// note: this handle doesn't survive the lifespan of the icon instance
				if (!disposed && (handle == IntPtr.Zero)) {
					if (GDIPlus.RunningOnUnix ()) {
						handle = GetInternalBitmap ().NativeObject;
					} else {
						// remember that this block executes only with MS GDI+
						IconInfo ii = new IconInfo ();
						ii.IsIcon = true;
						ii.hbmColor = ToBitmap ().GetHbitmap ();
						ii.hbmMask = ii.hbmColor;
						handle = GDIPlus.CreateIconIndirect (ref ii);
					}
				}
				return handle;
			}
		}
#endif
		[Browsable (false)]
		public int Height {
			get {
				return iconSize.Height;
			}
		}

		public Size Size {
			get {
				return iconSize;
			}
		}

		[Browsable (false)]
		public int Width {
			get {
				return iconSize.Width;
			}
		}

		~Icon ()
		{
			Dispose ();
		}
			
		private void InitFromStreamWithSize (Stream stream, int width, int height)
		{
			//read the icon header
			if (stream == null || stream.Length == 0)
				throw new System.ArgumentException ("The argument 'stream' must be a picture that can be used as a Icon", "stream");
			
			BinaryReader reader = new BinaryReader (stream);

			//iconDir = new IconDir ();
			iconDir.idReserved = reader.ReadUInt16();
			if (iconDir.idReserved != 0) //must be 0
				throw new System.ArgumentException ("Invalid Argument", "stream");
			
			iconDir.idType = reader.ReadUInt16();
			if (iconDir.idType != 1) //must be 1
				throw new System.ArgumentException ("Invalid Argument", "stream");

			ushort dirEntryCount = reader.ReadUInt16();

			if (dirEntryCount == 0)
				throw new Win32Exception (0, "Zero directory entry count.");

			imageData = new ImageData [dirEntryCount]; 
			iconDir.idCount = dirEntryCount; 
			iconDir.idEntries = new IconDirEntry [dirEntryCount];
			bool sizeObtained = false;
			// now read in the IconDirEntry structures
			for (int i = 0; i < dirEntryCount; i++) {
				IconDirEntry ide;
				ide.width = reader.ReadByte ();
				ide.height = reader.ReadByte ();
				ide.colorCount = reader.ReadByte ();
				ide.reserved = reader.ReadByte ();
				ide.planes = reader.ReadUInt16 ();
				ide.bitCount = reader.ReadUInt16 ();
				ide.bytesInRes = reader.ReadUInt32 ();
				ide.imageOffset = reader.ReadUInt32 ();
#if false
Console.WriteLine ("Entry: {0}", i);
Console.WriteLine ("\tide.width: {0}", ide.width);
Console.WriteLine ("\tide.height: {0}", ide.height);
Console.WriteLine ("\tide.colorCount: {0}", ide.colorCount);
Console.WriteLine ("\tide.reserved: {0}", ide.reserved);
Console.WriteLine ("\tide.planes: {0}", ide.planes);
Console.WriteLine ("\tide.bitCount: {0}", ide.bitCount);
Console.WriteLine ("\tide.bytesInRes: {0}", ide.bytesInRes);
Console.WriteLine ("\tide.imageOffset: {0}", ide.imageOffset);
#endif
				// Vista 256x256 icons points directly to a PNG bitmap
				// 256x256 icons are decoded as 0x0 (width and height are encoded as BYTE)
				// and we ignore them just like MS does (at least up to fx 2.0) 
				// Added: storing data so it can be saved back
				if ((ide.width == 0) && (ide.height == 0))
					ide.ignore = true;
				else
					ide.ignore = false;

				iconDir.idEntries [i] = ide;

				//is this is the best fit??
				if (!sizeObtained) {
					if (((ide.height == height) || (ide.width == width)) && !ide.ignore) {
						this.id = (ushort) i;
						sizeObtained = true;
						this.iconSize.Height = ide.height;
						this.iconSize.Width = ide.width;
					}
				}
			}

			// throw error if no valid entries found
			int valid = 0;
			for (int i = 0; i < dirEntryCount; i++) {
				if (!(iconDir.idEntries [i].ignore))
					valid++;
			}

			if (valid == 0)
				iconDir.idEntries [0].ignore = false;

			// if we havent found the best match, return the one with the
			// largest size. Is this approach correct??
			if (!sizeObtained){
				uint largestSize = 0;
				for (int j=0; j<dirEntryCount; j++){ 
					if (iconDir.idEntries [j].bytesInRes >= largestSize && !iconDir.idEntries [j].ignore)	{
						largestSize = iconDir.idEntries [j].bytesInRes;
						this.id = (ushort) j;
						if (iconDir.idEntries[j].height == 0 && iconDir.idEntries[j].width == 0)
						{
							this.iconSize.Height = 256;
							this.iconSize.Width = 256;
						}
						else
						{
							this.iconSize.Height = iconDir.idEntries [j].height;
							this.iconSize.Width = iconDir.idEntries [j].width;
						}
					}
				}
			}
			
			//now read in the icon data
			for (int j = 0; j<dirEntryCount; j++) 
			{
				// process ignored into IconDump
				if (iconDir.idEntries [j].ignore) {
					IconDump id = new IconDump ();
					stream.Seek (iconDir.idEntries [j].imageOffset, SeekOrigin.Begin);
					id.data = new byte [iconDir.idEntries [j].bytesInRes];
					stream.Read (id.data, 0, id.data.Length);
					imageData [j] = id;
					continue;
				}
				// standard image
				IconImage iidata = new IconImage();
				BitmapInfoHeader bih = new BitmapInfoHeader();
				stream.Seek (iconDir.idEntries [j].imageOffset, SeekOrigin.Begin);
				byte [] png_header = new byte[] {0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a};
				byte [] buffer = new byte [iconDir.idEntries [j].bytesInRes];
				stream.Read (buffer, 0, buffer.Length);

				{
					int i;

					for (i = 0; i < 8; ++i)
						if (png_header[i] != buffer[i])
							break;
					if (i == 8)
					{
					    /* PNG image, convert it. */
						Bitmap bmp = new Bitmap(new MemoryStream(buffer));
						AlphaImage aidata = new AlphaImage ();

						aidata.iconHeader.biSize = 40;
						aidata.iconHeader.biWidth = bmp.Width;
						aidata.iconHeader.biHeight = bmp.Height;
						aidata.iconHeader.biPlanes = 1;
						aidata.iconHeader.biBitCount = 32;
						aidata.iconHeader.biCompression = 0;
						aidata.iconHeader.biSizeImage = 0;
						aidata.iconHeader.biXPelsPerMeter = 0;
						aidata.iconHeader.biYPelsPerMeter = 0;
						aidata.iconHeader.biClrUsed = 0;
						aidata.iconHeader.biClrImportant = 0;

						BitmapData bmpData = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
								System.Drawing.Imaging.ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
						int bytes  = Math.Abs(bmpData.Stride) * bmp.Height;
						IntPtr ptr = bmpData.Scan0;
						aidata.iconARGB = new byte[bytes];
						System.Runtime.InteropServices.Marshal.Copy(ptr, aidata.iconARGB, 0, bytes);
						bmp.UnlockBits(bmpData);
						imageData [j] = aidata;
						continue;
					}
				}

				BinaryReader bihReader = new BinaryReader (new MemoryStream(buffer));
				bih.biSize = bihReader.ReadUInt32 ();
				bih.biWidth = bihReader.ReadInt32 ();
				bih.biHeight = bihReader.ReadInt32 ();
				bih.biPlanes = bihReader.ReadUInt16 ();
				bih.biBitCount = bihReader.ReadUInt16 ();
				bih.biCompression = bihReader.ReadUInt32 ();
				bih.biSizeImage = bihReader.ReadUInt32 ();
				bih.biXPelsPerMeter = bihReader.ReadInt32 ();
				bih.biYPelsPerMeter = bihReader.ReadInt32 ();
				bih.biClrUsed = bihReader.ReadUInt32 ();
				bih.biClrImportant = bihReader.ReadUInt32 ();
#if false
Console.WriteLine ("Entry: {0}", j);
Console.WriteLine ("\tbih.biSize: {0}", bih.biSize);
Console.WriteLine ("\tbih.biWidth: {0}", bih.biWidth);
Console.WriteLine ("\tbih.biHeight: {0}", bih.biHeight);
Console.WriteLine ("\tbih.biPlanes: {0}", bih.biPlanes);
Console.WriteLine ("\tbih.biBitCount: {0}", bih.biBitCount);
Console.WriteLine ("\tbih.biCompression: {0}", bih.biCompression);
Console.WriteLine ("\tbih.biSizeImage: {0}", bih.biSizeImage);
Console.WriteLine ("\tbih.biXPelsPerMeter: {0}", bih.biXPelsPerMeter);
Console.WriteLine ("\tbih.biYPelsPerMeter: {0}", bih.biYPelsPerMeter);
Console.WriteLine ("\tbih.biClrUsed: {0}", bih.biClrUsed);
Console.WriteLine ("\tbih.biClrImportant: {0}", bih.biClrImportant);
#endif
				iidata.iconHeader = bih;
				//Read the number of colors used and corresponding memory occupied by
				//color table. Fill this memory chunk into rgbquad[]
				int numColors;
				switch (bih.biBitCount){
					case 1: numColors = 2;
						break;
					case 4: numColors = 16;
						break;
					case 8: numColors = 256;
						break;
					default: numColors = 0;
						break;
				}
				
				iidata.iconColors = new uint [numColors];
				for (int i=0; i<numColors; i++)
					iidata.iconColors [i] = bihReader.ReadUInt32 ();

				//XOR mask is immediately after ColorTable and its size is 
				//icon height* no. of bytes per line
				
				//icon height is half of BITMAPINFOHEADER.biHeight, since it contains
				//both XOR as well as AND mask bytes
				int iconHeight = bih.biHeight/2;
				
				//bytes per line should should be uint aligned
				int numBytesPerLine = ((((bih.biWidth * bih.biPlanes * bih.biBitCount)+ 31)>>5)<<2);
				
				//Determine the XOR array Size
				int xorSize = numBytesPerLine * iconHeight;
				iidata.iconXOR = new byte [xorSize];
				int nread = bihReader.Read (iidata.iconXOR, 0, xorSize);
				if (nread != xorSize) {
					string msg = Locale.GetText ("{0} data length expected {1}, read {2}", "XOR", xorSize, nread);
					throw new ArgumentException (msg, "stream");
				}
				
				if (bih.biBitCount == 32) {
					// This may be an ARGB icon
					bool has_alpha = false;

					for (int k=3; k < xorSize; k+=4) {
						if (iidata.iconXOR [k] != 0)
						{
							has_alpha = true;
							break;
						}
					}

					if (has_alpha) {
						AlphaImage aidata = new AlphaImage ();
						aidata.iconHeader = iidata.iconHeader;
						aidata.iconARGB = iidata.iconXOR;

						imageData [j] = aidata;
						bihReader.Dispose ();
						continue;
					}
				}

				//Determine the AND array size
				numBytesPerLine = (int)((((bih.biWidth) + 31) & ~31) >> 3);
				int andSize = numBytesPerLine * iconHeight;
				iidata.iconAND = new byte [andSize];
				nread = bihReader.Read (iidata.iconAND, 0, andSize);
				if (nread != andSize) {
					string msg = Locale.GetText ("{0} data length expected {1}, read {2}", "AND", andSize, nread);
					throw new ArgumentException (msg, "stream");
				}
				
				imageData [j] = iidata;
				bihReader.Dispose ();
			}			

			reader.Dispose ();
		}
	}
}
