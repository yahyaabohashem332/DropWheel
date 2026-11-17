using System;
using System.Drawing;
using System.IO;
using System.Text;

namespace Petal.Shell;

/// <summary>
/// An SVG drawn by Svg.NET at about the size asked, keeping its proportions. Nothing outside the file is read: no
/// pictures, fonts or entities it points to, no address on the internet, and no DTD. Telling an SVG by its start
/// doesn't load Svg.NET: only drawing one does.
/// </summary>
public static class SvgPicture
{
    /// <summary>True when the start of a file is an SVG's: XML (or a comment) with an svg element near the start.</summary>
    public static bool Looks(byte[] head, int count)
    {
        var text = Encoding.UTF8.GetString(head, 0, count).TrimStart('﻿', ' ', '\t', '\r', '\n');
        return text.StartsWith("<", StringComparison.Ordinal) && text.IndexOf("<svg", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>The SVG drawn; null when it can't be (a broken file, or Svg.NET couldn't be loaded).</summary>
    public static Bitmap? Render(Stream stream, int size)
    {
        try
        {
            return Draw(stream, size);
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or ArgumentException or InvalidOperationException or NullReferenceException or FormatException
            or OutOfMemoryException or FileNotFoundException or FileLoadException or TypeLoadException or TypeInitializationException or MissingMethodException or BadImageFormatException)
        {
            return null;
        }
    }

    private static bool _closed;

    /// <summary>Apart from <see cref="Render"/>, so a missing Svg.NET is caught there, where this is first called.</summary>
    private static Bitmap? Draw(Stream stream, int size)
    {
        if (!_closed)
        {
            Svg.SvgDocument.ResolveExternalImages = Svg.ExternalType.None;
            Svg.SvgDocument.ResolveExternalElements = Svg.ExternalType.None;
            Svg.SvgDocument.ResolveExternalXmlEntites = Svg.ExternalType.None;
            Svg.SvgDocument.DisableDtdProcessing = true;
            _closed = true;
        }
        stream.Position = 0;
        var document = Svg.SvgDocument.Open<Svg.SvgDocument>(stream);
        var natural = document.GetDimensions();
        if (natural.Width <= 0 || natural.Height <= 0)
            natural = new SizeF(size, size);
        double scale = size / Math.Max(natural.Width, natural.Height);
        int width = Math.Max(1, (int)Math.Round(natural.Width * scale)), height = Math.Max(1, (int)Math.Round(natural.Height * scale));
        return document.Draw(width, height);
    }
}
