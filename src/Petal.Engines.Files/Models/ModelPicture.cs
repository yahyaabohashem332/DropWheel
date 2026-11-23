using System.Numerics;
using SkiaSharp;

namespace Petal.Engines.Files.Models;

/// <summary>
/// A picture of a model, drawn here (no graphics card needed): seen from the front, right and above, as product
/// pictures show things, shaded by a key light and a fill, in its colours and pictures, on a transparent background.
/// Drawn at twice the size and made smaller, so its edges are smooth.
/// </summary>
internal static class ModelPicture
{
    private const int Supersample = 2;

    public static byte[] Render(Model3D model, int longestSide = 1600)
    {
        model.Check();
        var (min, max) = model.Bounds();
        var center = (min + max) / 2;
        var forward = Vector3.Normalize(-new Vector3(1.1f, -1.6f, 1.0f));
        var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitZ));
        var up = Vector3.Cross(right, forward);

        // Where every corner lands on the picture plane, and how far it is from the viewer.
        var screen = new Vector3[model.Positions.Count];
        float left = float.MaxValue, top = float.MaxValue, rightMost = float.MinValue, bottom = float.MinValue;
        for (var i = 0; i < screen.Length; i++)
        {
            var d = model.Positions[i] - center;
            var x = Vector3.Dot(d, right);
            var y = -Vector3.Dot(d, up);
            screen[i] = new Vector3(x, y, Vector3.Dot(d, forward));
            left = MathF.Min(left, x); rightMost = MathF.Max(rightMost, x);
            top = MathF.Min(top, y); bottom = MathF.Max(bottom, y);
        }
        var spanX = MathF.Max(rightMost - left, 1e-6f);
        var spanY = MathF.Max(bottom - top, 1e-6f);
        var scale = longestSide * 0.9f / MathF.Max(spanX, spanY);
        var width = Math.Max(64, (int)MathF.Ceiling(spanX * scale / 0.9f));
        var height = Math.Max(64, (int)MathF.Ceiling(spanY * scale / 0.9f));
        int w = width * Supersample, h = height * Supersample;
        var s = scale * Supersample;
        var offsetX = (w - spanX * s) / 2 - left * s;
        var offsetY = (h - spanY * s) / 2 - top * s;
        for (var i = 0; i < screen.Length; i++)
            screen[i] = new Vector3(screen[i].X * s + offsetX, screen[i].Y * s + offsetY, screen[i].Z);

        var depth = new float[w * h];
        Array.Fill(depth, float.MaxValue);
        var pixels = new uint[w * h];
        var key = Vector3.Normalize(new Vector3(0.5f, -0.8f, 1.2f));
        var fill = Vector3.Normalize(new Vector3(-0.9f, -0.3f, 0.2f));
        var textures = model.Materials.Select(m => m.Texture is { } t && model.Uvs is not null ? SKBitmap.Decode(t) : null).ToArray();
        try
        {
            for (var t = 0; t < model.Triangles.Count; t++)
            {
                var (a, b, c, material) = model.Triangles[t];
                Vector3 pa = screen[a], pb = screen[b], pc = screen[c];
                var area = (pb.X - pa.X) * (pc.Y - pa.Y) - (pb.Y - pa.Y) * (pc.X - pa.X);
                if (MathF.Abs(area) < 1e-9f)
                    continue;
                var faceNormal = model.FaceNormal(t);
                var minX = Math.Max(0, (int)MathF.Floor(MathF.Min(pa.X, MathF.Min(pb.X, pc.X))));
                var maxX = Math.Min(w - 1, (int)MathF.Ceiling(MathF.Max(pa.X, MathF.Max(pb.X, pc.X))));
                var minY = Math.Max(0, (int)MathF.Floor(MathF.Min(pa.Y, MathF.Min(pb.Y, pc.Y))));
                var maxY = Math.Min(h - 1, (int)MathF.Ceiling(MathF.Max(pa.Y, MathF.Max(pb.Y, pc.Y))));
                var ca = model.CornerColor(a, material);
                var cb = model.CornerColor(b, material);
                var cc = model.CornerColor(c, material);
                var texture = material >= 0 && material < textures.Length ? textures[material] : null;
                for (var y = minY; y <= maxY; y++)
                {
                    var py = y + 0.5f;
                    for (var x = minX; x <= maxX; x++)
                    {
                        var px = x + 0.5f;
                        var wa = ((pb.X - px) * (pc.Y - py) - (pb.Y - py) * (pc.X - px)) / area;
                        var wb = ((pc.X - px) * (pa.Y - py) - (pc.Y - py) * (pa.X - px)) / area;
                        var wc = 1 - wa - wb;
                        if (wa < 0 || wb < 0 || wc < 0)
                            continue;
                        var z = wa * pa.Z + wb * pb.Z + wc * pc.Z;
                        var at = y * w + x;
                        if (z >= depth[at])
                            continue;
                        var normal = model.Normals is { } normals ? wa * normals[a] + wb * normals[b] + wc * normals[c] : faceNormal;
                        normal = normal == Vector3.Zero ? faceNormal : Vector3.Normalize(normal);
                        // Both sides lit: a surface seen from behind is still a surface.
                        if (Vector3.Dot(normal, forward) > 0)
                            normal = -normal;
                        var color = wa * ca + wb * cb + wc * cc;
                        if (texture is not null)
                        {
                            var uv = wa * model.Uvs![a] + wb * model.Uvs[b] + wc * model.Uvs[c];
                            color *= Sample(texture, uv);
                        }
                        if (color.W < 0.05f)
                            continue;
                        depth[at] = z;
                        var light = 0.32f + 0.62f * MathF.Max(0, Vector3.Dot(normal, key)) + 0.22f * MathF.Max(0, Vector3.Dot(normal, fill));
                        var rgb = Vector3.Clamp(new Vector3(color.X, color.Y, color.Z) * light, Vector3.Zero, Vector3.One) * 255f;
                        pixels[at] = 0xFF000000u | ((uint)rgb.X << 16) | ((uint)rgb.Y << 8) | (uint)rgb.Z;
                    }
                }
            }
        }
        finally
        {
            foreach (var texture in textures)
                texture?.Dispose();
        }

        using var large = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        System.Runtime.InteropServices.Marshal.Copy((int[])(object)pixels, 0, large.GetPixels(), pixels.Length);
        using var small = large.Resize(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul), new SKSamplingOptions(SKCubicResampler.Mitchell))
            ?? throw new InvalidOperationException("The picture could not be made smaller.");
        using var image = SKImage.FromBitmap(small);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static Vector4 Sample(SKBitmap texture, Vector2 uv)
    {
        var u = uv.X - MathF.Floor(uv.X);
        var v = uv.Y - MathF.Floor(uv.Y);
        var c = texture.GetPixel(Math.Min(texture.Width - 1, (int)(u * texture.Width)), Math.Min(texture.Height - 1, (int)(v * texture.Height)));
        return new Vector4(c.Red / 255f, c.Green / 255f, c.Blue / 255f, c.Alpha / 255f);
    }
}
