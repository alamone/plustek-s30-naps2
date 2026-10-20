using System.Windows.Media;
using System.Windows.Media.Imaging;
using Docnet.Core;
using Docnet.Core.Models;

namespace ScanKiosk;

/// <summary>
/// Page count and thumbnails via PDFium (Docnet.Core). Windows' own PDF renderer can't be used: NAPS2 + the
/// S30's WIA driver write pages ~444 inches wide (pixels-per-metre DPI bug), beyond the PDF spec's
/// 200-inch limit, and Windows.Data.Pdf then renders only part of each page.
/// </summary>
public static class PdfPages
{
    static readonly object Lock = new();  // PDFium is not thread-safe

    public static Task<int> CountAsync(string path) => Task.Run(() =>
    {
        lock (Lock)
        {
            using var doc = DocLib.Instance.GetDocReader(path, new PageDimensions(1.0));
            return doc.GetPageCount();
        }
    });

    /// <summary>Renders each page to fit a <paramref name="box"/> x <paramref name="box"/> pixel square.</summary>
    public static Task<List<BitmapSource>> ThumbnailsAsync(string path, int box, int max = 200) => Task.Run(() =>
    {
        var list = new List<BitmapSource>();
        lock (Lock)
        {
            using var doc = DocLib.Instance.GetDocReader(path, new PageDimensions(box, box));
            for (int i = 0; i < doc.GetPageCount() && i < max; i++)
            {
                using var page = doc.GetPageReader(i);
                int w = page.GetPageWidth(), h = page.GetPageHeight();
                var bgra = page.GetImage();
                FlattenOnWhite(bgra);
                var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bgra, w * 4);
                bmp.Freeze();
                list.Add(bmp);
            }
        }
        return list;
    });

    /// <summary>PDFium renders onto a transparent background; composite it onto white.</summary>
    static void FlattenOnWhite(byte[] bgra)
    {
        for (int i = 0; i < bgra.Length; i += 4)
        {
            int a = bgra[i + 3];
            if (a == 255) continue;
            for (int c = 0; c < 3; c++) bgra[i + c] = (byte)(bgra[i + c] * a / 255 + (255 - a));
            bgra[i + 3] = 255;
        }
    }
}
