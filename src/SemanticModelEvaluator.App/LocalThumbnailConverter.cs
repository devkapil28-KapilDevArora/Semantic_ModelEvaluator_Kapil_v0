using System.Globalization;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace SemanticModelEvaluator.App;

/// <summary>
/// Turns a dataset image reference into a thumbnail. Only existing local files are loaded; remote URLs are never fetched
/// implicitly (that would bypass the network policy and offline mode). Unreadable images become null instead of throwing.
/// </summary>
public sealed class LocalThumbnailConverter : IValueConverter
{
    public int DecodeHeight { get; set; } = 160;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && (!uri.IsFile || uri.IsUnc)) return null;
            var full = Path.GetFullPath(uri?.IsFile == true ? uri.LocalPath : path);
            if (full.StartsWith(@"\\", StringComparison.Ordinal) || !File.Exists(full)) return null; // UNC/SMB would bypass offline mode
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.DecodePixelHeight = DecodeHeight;
            image.UriSource = new Uri(full);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or UriFormatException) { return null; }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
