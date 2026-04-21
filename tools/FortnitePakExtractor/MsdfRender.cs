using SkiaSharp;
using Serilog;

namespace FortnitePakExtractor;

/// <summary>
/// Renders signed-distance-field textures (single-channel DF and multi-channel MSDF)
/// into crisp rasterized PNGs.
///
/// MSDF decode: sample the three color channels, take the median, threshold at 0.5.
/// Use smoothstep for anti-aliased edges. Output white-on-transparent so the icons
/// are usable directly in a UI.
/// </summary>
internal static class MsdfRender
{
    private enum DistanceSampleMode
    {
        Msdf,
        Red,
        Alpha,
    }

    /// <summary>Scan a directory for *_SDF.png and *_DF.png files and render each.</summary>
    public static int RenderAll(string dir, int targetSize = 512)
    {
        var renderedDir = Path.Combine(dir, "_rendered");
        Directory.CreateDirectory(renderedDir);

        var files = Directory.EnumerateFiles(dir, "*.png", SearchOption.TopDirectoryOnly)
            .Where(p =>
            {
                var name = Path.GetFileNameWithoutExtension(p);
                // Match _SDF / _DF / _MSDF as a token: either end-of-name (T_Icon_X_DF)
                // or followed by an underscore (T_Icon_Instrument_DF_PlasticInstruments_Bass).
                return IsDfToken(name, "_MSDF")
                    || IsDfToken(name, "_SDF")
                    || IsDfToken(name, "_DF");
            })
            .ToList();

        static bool IsDfToken(string name, string token)
        {
            int idx = name.IndexOf(token, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return false;
            int end = idx + token.Length;
            return end == name.Length || name[end] == '_';
        }

        Log.Information("MSDF: rendering {N} SDF/DF textures -> {Out}", files.Count, renderedDir);

        int done = 0;
        foreach (var src in files)
        {
            try
            {
                var name = Path.GetFileNameWithoutExtension(src);
                var outPath = Path.Combine(renderedDir, name + ".png");
                RenderOne(src, outPath, targetSize);

                // Also emit a "master-format" version: white disc, black instrument
                // silhouette (opaque), black outline, transparent background — matching
                // the format of existing web-app icons in public/instruments/.
                var masterFmtDir = Path.Combine(renderedDir, "_master_format");
                Directory.CreateDirectory(masterFmtDir);
                var masterFmtPath = Path.Combine(masterFmtDir, name + ".png");
                RenderMasterFormat(src, masterFmtPath, targetSize);

                // If the source appears to be a per-channel atlas (RGB channels each
                // carry a separate silhouette), also emit split R/G/B renders so the
                // individual icons can be extracted.
                using var bmp = SKBitmap.Decode(src);
                if (bmp is not null)
                {
                    // Always emit per-channel splits for SDF/MSDF files — many atlases
                    // use R=drums, G=vocals/note, etc. The split is cheap and makes it
                    // trivial to pick the one we want.
                    var splitDir = Path.Combine(renderedDir, "_channels");
                    Directory.CreateDirectory(splitDir);
                    var instrumentDir = Path.Combine(renderedDir, "_instrument_only");
                    Directory.CreateDirectory(instrumentDir);
                    var masterDir = Path.Combine(renderedDir, "_master_style");
                    Directory.CreateDirectory(masterDir);
                    foreach (var ch in new[] { 'R', 'G', 'B', 'A' })
                    {
                        var chPath = Path.Combine(splitDir, $"{name}_{ch}.png");
                        RenderChannel(src, chPath, targetSize, ch);

                        // "instrument-only": the surrounding disc is dropped and the
                        // instrument cutout is filled white. Useful for contexts that
                        // want a plain silhouette. Uses a geometric disc mask so it
                        // still works when the instrument shape reaches the disc edge.
                        var instPath = Path.Combine(instrumentDir, $"{name}_{ch}.png");
                        RenderChannelInstrumentOnly(src, instPath, targetSize, ch);

                        // "master-style": white disc with instrument cutout as
                        // transparent, matching the layout of the existing web-app
                        // assets (guitar/bass/drums/vocals in public/instruments).
                        // This is the disc-with-hole shape inset with padding so a
                        // thin dark outline is visible against any chip background.
                        var masterPath = Path.Combine(masterDir, $"{name}_{ch}.png");
                        RenderChannelMasterStyle(src, masterPath, targetSize, ch);
                    }
                }
                done++;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "MSDF render failed for {Src}", src);
            }
        }

        Log.Information("MSDF: rendered {Done}/{Total}", done, files.Count);
        return done;
    }

    private static bool IsLikelyChannelAtlas(SKBitmap src)
    {
        // Heuristic: in a per-channel atlas, many pixels will be bright in exactly ONE channel
        // (pure red/green/blue areas). In a true MSDF, bright pixels are bright in 2+ channels.
        int w = src.Width, h = src.Height;
        int step = Math.Max(1, Math.Min(w, h) / 24);
        int singleChannelHot = 0, multiChannelHot = 0;
        for (int y = 0; y < h; y += step)
        {
            for (int x = 0; x < w; x += step)
            {
                var c = src.GetPixel(x, y);
                int hotCount = (c.Red > 180 ? 1 : 0) + (c.Green > 180 ? 1 : 0) + (c.Blue > 180 ? 1 : 0);
                if (hotCount == 1) singleChannelHot++;
                else if (hotCount >= 2) multiChannelHot++;
            }
        }
        // If single-channel-hot samples are common AND outnumber multi-channel ones,
        // this is almost certainly a per-channel atlas.
        return singleChannelHot > 40 && singleChannelHot > multiChannelHot;
    }

    private static void RenderOne(string srcPath, string outPath, int targetSize)
    {
        using var src = SKBitmap.Decode(srcPath);
        if (src == null) throw new Exception("SKBitmap.Decode returned null");

        var srcW = src.Width;
        var srcH = src.Height;

        // Preserve aspect ratio; fit the longer edge to targetSize.
        double scale = (double)targetSize / Math.Max(srcW, srcH);
        int outW = Math.Max(1, (int)Math.Round(srcW * scale));
        int outH = Math.Max(1, (int)Math.Round(srcH * scale));

        // Determine if this looks like MSDF (significant color variance between channels)
        // or single-channel DF (R==G==B). Sample a grid.
        bool isMsdf = DetectMsdf(src);
        var sampleMode = DetectDistanceSampleMode(src, isMsdf);

        using var dst = new SKBitmap(outW, outH, SKColorType.Rgba8888, SKAlphaType.Unpremul);

        // px size in source space = 1 output-pixel worth of source
        // Used to pick a smoothstep width that scales with output resolution.
        double pxInSrc = 1.0 / scale;
        // Edge softness: about half a source pixel, but at least 0.002 in normalized distance.
        // Normalized distance here means 0..1 where 0.5 is the isoline. We approximate
        // 1 normalized unit == pxRange source pixels. Assume pxRange ≈ 4 (Msdfgen default).
        const double pxRange = 4.0;
        double edge = Math.Max(0.5 * pxInSrc / pxRange, 0.01);

        for (int y = 0; y < outH; y++)
        {
            double sy = (y + 0.5) / scale - 0.5;
            for (int x = 0; x < outW; x++)
            {
                double sx = (x + 0.5) / scale - 0.5;
                double sd = SampleBilinearDistance(src, sx, sy, sampleMode);
                // Smoothstep around 0.5 isoline.
                double a = Smoothstep(0.5 - edge, 0.5 + edge, sd);
                byte alpha = (byte)Math.Clamp((int)Math.Round(a * 255.0), 0, 255);
                dst.SetPixel(x, y, new SKColor(255, 255, 255, alpha));
            }
        }

        using var img = SKImage.FromBitmap(dst);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        using (var fs = File.Create(outPath)) data.SaveTo(fs);

        // Also write a preview composited onto dark gray so alpha content is visually obvious.
        var previewDir = Path.Combine(Path.GetDirectoryName(outPath)!, "_preview");
        Directory.CreateDirectory(previewDir);
        var previewPath = Path.Combine(previewDir, Path.GetFileName(outPath));
        using var preview = new SKBitmap(outW, outH, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(preview))
        {
            canvas.Clear(new SKColor(30, 30, 40));
            canvas.DrawBitmap(dst, 0, 0);
        }
        using var previewImg = SKImage.FromBitmap(preview);
        using var previewData = previewImg.Encode(SKEncodedImageFormat.Png, 95);
        using (var pfs = File.Create(previewPath)) previewData.SaveTo(pfs);
    }

    private static bool DetectMsdf(SKBitmap src)
    {
        int w = src.Width, h = src.Height;
        int samples = 0, colorful = 0;
        int step = Math.Max(1, Math.Min(w, h) / 16);
        for (int y = 0; y < h; y += step)
        {
            for (int x = 0; x < w; x += step)
            {
                var c = src.GetPixel(x, y);
                int maxCh = Math.Max(c.Red, Math.Max(c.Green, c.Blue));
                int minCh = Math.Min(c.Red, Math.Min(c.Green, c.Blue));
                if (maxCh - minCh > 20) colorful++;
                samples++;
            }
        }
        // If more than ~5% of samples show significant channel divergence, treat as MSDF.
        return samples > 0 && (double)colorful / samples > 0.05;
    }

    private static DistanceSampleMode DetectDistanceSampleMode(SKBitmap src, bool isMsdf)
    {
        if (isMsdf)
        {
            return DistanceSampleMode.Msdf;
        }

        int w = src.Width;
        int h = src.Height;
        int step = Math.Max(1, Math.Min(w, h) / 24);
        double redSum = 0;
        double redSqSum = 0;
        double alphaSum = 0;
        double alphaSqSum = 0;
        int samples = 0;

        for (int y = 0; y < h; y += step)
        {
            for (int x = 0; x < w; x += step)
            {
                var c = src.GetPixel(x, y);
                double red = c.Red;
                double alpha = c.Alpha;
                redSum += red;
                redSqSum += red * red;
                alphaSum += alpha;
                alphaSqSum += alpha * alpha;
                samples++;
            }
        }

        if (samples == 0)
        {
            return DistanceSampleMode.Red;
        }

        double redMean = redSum / samples;
        double alphaMean = alphaSum / samples;
        double redVariance = redSqSum / samples - redMean * redMean;
        double alphaVariance = alphaSqSum / samples - alphaMean * alphaMean;

        return alphaVariance > redVariance * 1.5 && alphaVariance > 25.0
            ? DistanceSampleMode.Alpha
            : DistanceSampleMode.Red;
    }

    private static double SampleBilinearDistance(SKBitmap src, double x, double y, DistanceSampleMode mode)
    {
        int w = src.Width, h = src.Height;
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        int x1 = x0 + 1, y1 = y0 + 1;
        double fx = x - x0, fy = y - y0;

        double s00 = SamplePixelDistance(src, x0, y0, w, h, mode);
        double s10 = SamplePixelDistance(src, x1, y0, w, h, mode);
        double s01 = SamplePixelDistance(src, x0, y1, w, h, mode);
        double s11 = SamplePixelDistance(src, x1, y1, w, h, mode);

        double a = s00 + (s10 - s00) * fx;
        double b = s01 + (s11 - s01) * fx;
        return a + (b - a) * fy;
    }

    private static double SamplePixelDistance(SKBitmap src, int x, int y, int w, int h, DistanceSampleMode mode)
    {
        // Edge-clamp addressing.
        x = Math.Clamp(x, 0, w - 1);
        y = Math.Clamp(y, 0, h - 1);
        var c = src.GetPixel(x, y);
        if (mode == DistanceSampleMode.Msdf)
        {
            // median(R,G,B) is the standard MSDF decode.
            int r = c.Red, g = c.Green, b = c.Blue;
            int med = Math.Max(Math.Min(r, g), Math.Min(Math.Max(r, g), b));
            return med / 255.0;
        }

        return mode == DistanceSampleMode.Alpha
            ? c.Alpha / 255.0
            : c.Red / 255.0;
    }

    private static double Smoothstep(double edge0, double edge1, double x)
    {
        if (edge1 <= edge0) return x < edge0 ? 0 : 1;
        double t = Math.Clamp((x - edge0) / (edge1 - edge0), 0.0, 1.0);
        return t * t * (3 - 2 * t);
    }

    /// <summary>Render a single RGB/A channel as its own signed-distance-field.</summary>
    private static void RenderChannel(string srcPath, string outPath, int targetSize, char channel)
    {
        using var src = SKBitmap.Decode(srcPath);
        if (src == null) throw new Exception("SKBitmap.Decode returned null");

        int srcW = src.Width, srcH = src.Height;
        double scale = (double)targetSize / Math.Max(srcW, srcH);
        int outW = Math.Max(1, (int)Math.Round(srcW * scale));
        int outH = Math.Max(1, (int)Math.Round(srcH * scale));
        double pxInSrc = 1.0 / scale;
        const double pxRange = 4.0;
        double edge = Math.Max(0.5 * pxInSrc / pxRange, 0.01);

        using var dst = new SKBitmap(outW, outH, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        for (int y = 0; y < outH; y++)
        {
            double sy = (y + 0.5) / scale - 0.5;
            for (int x = 0; x < outW; x++)
            {
                double sx = (x + 0.5) / scale - 0.5;
                double sd = SampleBilinearChannel(src, sx, sy, channel);
                double a = Smoothstep(0.5 - edge, 0.5 + edge, sd);
                byte alpha = (byte)Math.Clamp((int)Math.Round(a * 255.0), 0, 255);
                dst.SetPixel(x, y, new SKColor(255, 255, 255, alpha));
            }
        }

        using var img = SKImage.FromBitmap(dst);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        using (var fs = File.Create(outPath)) data.SaveTo(fs);

        // Dark-bg preview alongside.
        var previewDir = Path.Combine(Path.GetDirectoryName(outPath)!, "_preview");
        Directory.CreateDirectory(previewDir);
        var previewPath = Path.Combine(previewDir, Path.GetFileName(outPath));
        using var preview = new SKBitmap(outW, outH, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(preview))
        {
            canvas.Clear(new SKColor(30, 30, 40));
            canvas.DrawBitmap(dst, 0, 0);
        }
        using var previewImg = SKImage.FromBitmap(preview);
        using var previewData = previewImg.Encode(SKEncodedImageFormat.Png, 95);
        using (var pfs = File.Create(previewPath)) previewData.SaveTo(pfs);
    }

    private static double SampleBilinearChannel(SKBitmap src, double x, double y, char channel)
    {
        int w = src.Width, h = src.Height;
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        int x1 = x0 + 1, y1 = y0 + 1;
        double fx = x - x0, fy = y - y0;
        double s00 = GetChannel(src, x0, y0, w, h, channel);
        double s10 = GetChannel(src, x1, y0, w, h, channel);
        double s01 = GetChannel(src, x0, y1, w, h, channel);
        double s11 = GetChannel(src, x1, y1, w, h, channel);
        double a = s00 + (s10 - s00) * fx;
        double b = s01 + (s11 - s01) * fx;
        return a + (b - a) * fy;
    }

    private static double GetChannel(SKBitmap src, int x, int y, int w, int h, char channel)
    {
        x = Math.Clamp(x, 0, w - 1);
        y = Math.Clamp(y, 0, h - 1);
        var c = src.GetPixel(x, y);
        return channel switch
        {
            'R' => c.Red / 255.0,
            'G' => c.Green / 255.0,
            'B' => c.Blue / 255.0,
            'A' => c.Alpha / 255.0,
            _ => 0
        };
    }

    /// <summary>
    /// Render a single channel as an "instrument-only" silhouette: the surrounding
    /// disc is dropped and the instrument cutout inside the disc becomes white.
    ///
    /// The source channel encodes (disc − instrument): the disc interior has SDF
    /// values &gt; 0.5 and the instrument cutout has values &lt; 0.5. A simple invert
    /// can't tell the inside-instrument region from outside-the-disc (both low),
    /// so we compute the disc's geometric bounding circle from "definitely-interior"
    /// pixels (d &gt; 0.75) and clip the inverted field to that circle. This handles
    /// instruments whose shape reaches the disc boundary (e.g. piano keys) where
    /// flood-fill would leak through the gap and consume the whole interior.
    /// </summary>
    private static void RenderChannelInstrumentOnly(string srcPath, string outPath, int targetSize, char channel)
    {
        using var src = SKBitmap.Decode(srcPath);
        if (src == null) throw new Exception("SKBitmap.Decode returned null");

        int srcW = src.Width, srcH = src.Height;
        double scale = (double)targetSize / Math.Max(srcW, srcH);
        int outW = Math.Max(1, (int)Math.Round(srcW * scale));
        int outH = Math.Max(1, (int)Math.Round(srcH * scale));
        double pxInSrc = 1.0 / scale;
        const double pxRange = 4.0;
        double edge = Math.Max(0.5 * pxInSrc / pxRange, 0.01);

        // Step 1: sample the channel at output resolution into a dense array.
        var field = new double[outW * outH];
        for (int y = 0; y < outH; y++)
        {
            double sy = (y + 0.5) / scale - 0.5;
            for (int x = 0; x < outW; x++)
            {
                double sx = (x + 0.5) / scale - 0.5;
                field[y * outW + x] = SampleBilinearChannel(src, sx, sy, channel);
            }
        }

        // Step 2: determine the disc mask. Each atlas cell places its disc in the
        // center of the image, so cx/cy = image center is a much more robust estimate
        // than the centroid of "definitely-interior" pixels (which gets pulled off
        // by asymmetric instrument cutouts in the Type01 atlas). The radius is
        // chosen conservatively as 43% of the shorter edge — small enough that
        // the disc's anti-aliased boundary is safely outside the mask even if
        // the disc slightly overhangs the square cell.
        // We still verify the channel has SOME disc content so we don't emit a
        // circle of random noise when a channel is empty.
        int nonZero = 0;
        for (int i = 0; i < field.Length; i++) if (field[i] > 0.75) nonZero++;
        if (nonZero == 0)
        {
            using var empty = new SKBitmap(outW, outH, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            WritePngWithPreview(empty, outPath);
            return;
        }
        double cx = outW / 2.0;
        double cy = outH / 2.0;
        double maskRadius = Math.Min(outW, outH) * 0.43;
        double maskRadiusSq = maskRadius * maskRadius;

        // Step 3: emit the output. Alpha = inverted SDF threshold (low channel
        // value → high alpha) clipped to the disc mask. Pixels with d > 0.4
        // are hard-cut to 0 so we don't render any part of the disc interior
        // or its AA boundary even if it sneaks inside the mask.
        using var dst = new SKBitmap(outW, outH, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        for (int y = 0; y < outH; y++)
        {
            for (int x = 0; x < outW; x++)
            {
                double dx = x - cx, dy = y - cy;
                byte alpha = 0;
                if (dx * dx + dy * dy <= maskRadiusSq)
                {
                    double d = field[y * outW + x];
                    if (d < 0.4)
                    {
                        // AA the instrument silhouette at d ∈ [0.3, 0.4]. Below
                        // 0.3 → fully opaque; between 0.3 and 0.4 → smooth fade
                        // to transparent; at/above 0.4 → clamped to 0 above.
                        double a = 1.0 - Smoothstep(0.3, 0.4, d);
                        alpha = (byte)Math.Clamp((int)Math.Round(a * 255.0), 0, 255);
                    }
                }
                dst.SetPixel(x, y, new SKColor(255, 255, 255, alpha));
            }
        }

        WritePngWithPreview(dst, outPath);
    }

    /// <summary>
    /// "Master-style" render: white disc with instrument cutout as transparent,
    /// inset with padding and a thin dark outline — matching the format of the
    /// existing FortniteFestivalWeb assets (guitar/bass/drums/vocals). On a
    /// colored chip the chip color bleeds through the instrument cutout; the
    /// dark outline keeps the disc edge crisp against any chip background.
    /// </summary>
    private static void RenderChannelMasterStyle(string srcPath, string outPath, int targetSize, char channel)
    {
        using var src = SKBitmap.Decode(srcPath);
        if (src == null) throw new Exception("SKBitmap.Decode returned null");

        // Padding fraction each side — matches master vocals.png which leaves
        // ~8-10% between the disc and the canvas edge. The dark outline lives
        // just outside the white disc within that padding region.
        const double paddingFrac = 0.08;
        int inner = (int)Math.Round(targetSize * (1.0 - 2.0 * paddingFrac));
        int offset = (targetSize - inner) / 2;

        // Render the channel (white-disc + transparent-cutout) at the inner size.
        int srcW = src.Width, srcH = src.Height;
        double scale = (double)inner / Math.Max(srcW, srcH);
        double pxInSrc = 1.0 / scale;
        const double pxRange = 4.0;
        double edge = Math.Max(0.5 * pxInSrc / pxRange, 0.01);

        // Compute a dense alpha-only field at the inner resolution.
        var alpha = new byte[inner * inner];
        bool anyContent = false;
        for (int y = 0; y < inner; y++)
        {
            double sy = (y + 0.5) / scale - 0.5;
            for (int x = 0; x < inner; x++)
            {
                double sx = (x + 0.5) / scale - 0.5;
                double sd = SampleBilinearChannel(src, sx, sy, channel);
                double a = Smoothstep(0.5 - edge, 0.5 + edge, sd);
                byte ab = (byte)Math.Clamp((int)Math.Round(a * 255.0), 0, 255);
                alpha[y * inner + x] = ab;
                if (ab > 16) anyContent = true;
            }
        }

        using var dst = new SKBitmap(targetSize, targetSize, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        // Clear to transparent. SKBitmap ctor already gives us zero-init memory.

        if (!anyContent)
        {
            WritePngWithPreview(dst, outPath);
            return;
        }

        // Build a slightly dilated alpha (1 px) to serve as the dark outline.
        // Anywhere the dilated alpha is visible but the original alpha is not
        // becomes the dark ring around the disc.
        var outlineAlpha = new byte[inner * inner];
        const int outlinePx = 2;
        for (int y = 0; y < inner; y++)
        {
            for (int x = 0; x < inner; x++)
            {
                byte best = alpha[y * inner + x];
                for (int dy = -outlinePx; dy <= outlinePx && best < 255; dy++)
                {
                    int yy = y + dy;
                    if ((uint)yy >= (uint)inner) continue;
                    for (int dx = -outlinePx; dx <= outlinePx && best < 255; dx++)
                    {
                        int xx = x + dx;
                        if ((uint)xx >= (uint)inner) continue;
                        byte v = alpha[yy * inner + xx];
                        if (v > best) best = v;
                    }
                }
                outlineAlpha[y * inner + x] = best;
            }
        }

        // Composite into the output canvas at (offset, offset).
        // dark outline layer (30,30,40) below, white disc layer on top.
        for (int y = 0; y < inner; y++)
        {
            for (int x = 0; x < inner; x++)
            {
                byte fg = alpha[y * inner + x];
                byte ol = outlineAlpha[y * inner + x];
                if (ol == 0) continue;
                // Alpha-over: white(fg) over dark(ol - fg, clamped to 0..255).
                // Output color = white where fg is opaque, fading to dark where
                // only the outline contributes.
                double fgA = fg / 255.0;
                double olA = ol / 255.0;
                // Composite color: mix(black, white, fgA)
                int r = (int)Math.Round(0 * (1 - fgA) + 255 * fgA);
                int g = (int)Math.Round(0 * (1 - fgA) + 255 * fgA);
                int b = (int)Math.Round(0 * (1 - fgA) + 255 * fgA);
                byte aout = (byte)Math.Clamp((int)Math.Round(olA * 255.0), 0, 255);
                dst.SetPixel(offset + x, offset + y, new SKColor((byte)r, (byte)g, (byte)b, aout));
            }
        }

        WritePngWithPreview(dst, outPath);
    }

    /// <summary>
    /// Render an SDF/MSDF into "master format": white shape body, black instrument
    /// silhouette, black outline traced around the actual shape boundary, transparent
    /// outside. Works for any shape (circles, circles with + badge, etc.).
    ///
    /// Algorithm: threshold the SDF to get shapeAlpha, dilate outward by outlinePx.
    /// Wherever dilatedAlpha &gt; 0 but shapeAlpha ≈ 0 → black (outline or cutout).
    /// Wherever shapeAlpha &gt; 0 → white body.
    /// </summary>
    private static void RenderMasterFormat(string srcPath, string outPath, int targetSize)
    {
        using var src = SKBitmap.Decode(srcPath);
        if (src == null) throw new Exception("SKBitmap.Decode returned null");

        int srcW = src.Width, srcH = src.Height;
        bool isMsdf = DetectMsdf(src);
        var sampleMode = DetectDistanceSampleMode(src, isMsdf);
        double scale = (double)targetSize / Math.Max(srcW, srcH);
        int sdfW = Math.Max(1, (int)Math.Round(srcW * scale));
        int sdfH = Math.Max(1, (int)Math.Round(srcH * scale));
        double pxInSrc = 1.0 / scale;
        const double pxRange = 4.0;
        double edge = Math.Max(0.5 * pxInSrc / pxRange, 0.01);

        // Step 1: compute shape alpha from the SDF field at the base resolution.
        var shapeAlpha = new double[sdfW * sdfH];
        bool anyContent = false;
        for (int y = 0; y < sdfH; y++)
        {
            double sy = (y + 0.5) / scale - 0.5;
            for (int x = 0; x < sdfW; x++)
            {
                double sx = (x + 0.5) / scale - 0.5;
                double d = SampleBilinearDistance(src, sx, sy, sampleMode);
                double a = Smoothstep(0.5 - edge, 0.5 + edge, d);
                shapeAlpha[y * sdfW + x] = a;
                if (a > 0.1) anyContent = true;
            }
        }

        if (!anyContent)
        {
            using var empty = new SKBitmap(sdfW, sdfH, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            WritePngWithPreview(empty, outPath);
            return;
        }

        // Step 2: detect the disc (center + radius) that represents the main icon.
        DetectDisc(shapeAlpha, sdfW, sdfH, out double cx, out double cy, out double discRadius);
        bool hasDisc = discRadius > 0;
        string discMethod = "p75-rays";

        // Some DF icons (notably ProDrums/ProSnare) are encoded edge-to-edge as
        // outlines, so the regular ray fit measures the icon envelope instead of
        // the circular badge. When the fitted radius is effectively the whole
        // half-image, fall back to scanning the raw DF from image center along the
        // cardinal axes and use the outer ring crossing instead.
        if (hasDisc
            && discRadius > Math.Min(sdfW, sdfH) * 0.48
            && TryDetectRawCenteredDiscRadius(src, scale, sampleMode, out double fallbackRadius))
        {
            discRadius = fallbackRadius;
            discMethod = "raw-cardinals";
        }

        Console.WriteLine($"  [disc] {Path.GetFileNameWithoutExtension(outPath)}: cx={cx:F1} cy={cy:F1} r={discRadius:F1} hasDisc={hasDisc} method={discMethod}");

        // TEST MODE: simple black circle behind the icon, no fill logic.
        // Circle radius = discRadius + 32 (covers protrusions like drumstick tips).
        // Use SDF-field center for the circle — the detected centroid gets pulled
        // off-center by asymmetric features (drum lugs, piano keys, + badges).
        double circleR = hasDisc ? discRadius + 32 : Math.Min(sdfW, sdfH) * 0.48;

        // Add an explicit thin outline around all white SDF shapes.
        int outlinePx = Math.Max(2, (int)Math.Round(Math.Max(sdfW, sdfH) * 0.006));
        var outlineAlpha = new byte[sdfW * sdfH];
        for (int y = 0; y < sdfH; y++)
        {
            for (int x = 0; x < sdfW; x++)
            {
                double sa = shapeAlpha[y * sdfW + x];
                if (sa >= 0.5)
                {
                    continue;
                }

                double best = 0;
                for (int dy = -outlinePx; dy <= outlinePx && best < 1.0; dy++)
                {
                    int yy = y + dy;
                    if ((uint)yy >= (uint)sdfH) continue;
                    for (int dx = -outlinePx; dx <= outlinePx && best < 1.0; dx++)
                    {
                        int xx = x + dx;
                        if ((uint)xx >= (uint)sdfW) continue;
                        double v = shapeAlpha[yy * sdfW + xx];
                        if (v > best) best = v;
                    }
                }

                if (best > 0.01)
                {
                    outlineAlpha[y * sdfW + x] = (byte)Math.Clamp((int)Math.Round(best * 255.0), 0, 255);
                }
            }
        }

        // Detached white islands (for example the + badge) need a much thicker
        // black halo that matches the disc border thickness.
        int badgeOutlinePx = hasDisc
            ? Math.Max(2, (int)Math.Round(circleR - discRadius))
            : outlinePx;
        bool[] primaryComponentMask = FindLargestWhiteComponentMask(shapeAlpha, sdfW, sdfH);
        var badgeOutlineAlpha = BuildDetachedOutlineAlpha(shapeAlpha, primaryComponentMask, sdfW, sdfH, badgeOutlinePx);

        // Pad the output canvas so the circle is never clipped.
        int pad = Math.Max(0, (int)Math.Ceiling(circleR - Math.Min(sdfW, sdfH) / 2.0) + 2);
        int outW = sdfW + 2 * pad;
        int outH = sdfH + 2 * pad;
        double circleCx = outW / 2.0;
        double circleCy = outH / 2.0;
        double circleR2 = circleR * circleR;
        // Smooth the circle edge over ~1.5px for anti-aliasing.
        double circleOuter = circleR + 1.5;
        double circleOuter2 = circleOuter * circleOuter;

        using var dst = new SKBitmap(outW, outH, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        for (int y = 0; y < outH; y++)
        {
            for (int x = 0; x < outW; x++)
            {
                // Map output coords back to SDF field coords.
                int sx = x - pad, sy = y - pad;
                double sa = (sx >= 0 && sx < sdfW && sy >= 0 && sy < sdfH)
                    ? shapeAlpha[sy * sdfW + sx] : 0;
                byte oa = 0;
                if (sx >= 0 && sx < sdfW && sy >= 0 && sy < sdfH)
                {
                    int sourceIdx = sy * sdfW + sx;
                    oa = Math.Max(outlineAlpha[sourceIdx], badgeOutlineAlpha[sourceIdx]);
                }
                double ddx = x - circleCx, ddy = y - circleCy;
                double dist2 = ddx * ddx + ddy * ddy;
                byte circleAlpha = 0;
                if (dist2 <= circleR2)
                {
                    circleAlpha = 255;
                }
                else if (dist2 <= circleOuter2)
                {
                    double dist = Math.Sqrt(dist2);
                    double a = 1.0 - Smoothstep(circleR, circleOuter, dist);
                    circleAlpha = (byte)Math.Clamp((int)Math.Round(a * 255.0), 0, 255);
                }

                if (sa >= 0.5)
                {
                    // Shape body → white.
                    dst.SetPixel(x, y, new SKColor(255, 255, 255, 255));
                }
                else if (oa > 0)
                {
                    // Explicit outline around badge/disc shapes.
                    dst.SetPixel(x, y, new SKColor(0, 0, 0, oa));
                }
                else if (circleAlpha > 0)
                {
                    dst.SetPixel(x, y, new SKColor(0, 0, 0, circleAlpha));
                }
                else
                {
                    dst.SetPixel(x, y, new SKColor(0, 0, 0, 0));
                }
            }
        }

        WritePngWithPreview(dst, outPath);
    }

    /// <summary>
    /// Detect whether the shape is a disc and fit its center + radius.
    /// Center: centroid of all shape-body pixels (disc ring dominates total mass,
    /// so + badge pulls center only slightly; a second pass excludes outliers).
    /// Radius: 75th percentile of 72-ray outermost shape-body distances from
    /// that center. Rays that land on the disc ring cluster tightly; + badge
    /// rays are longer (outliers above), small protrusions are shorter
    /// (outliers below). P75 picks the disc-ring consensus while extending far
    /// enough to cover instrument silhouettes that lie entirely within it.
    /// Sets radius = -1 if the shape is not disc-like.
    /// </summary>
    private static void DetectDisc(double[] shapeAlpha, int outW, int outH,
        out double centerX, out double centerY, out double radius)
    {
        // Pass 1: raw centroid of shape body.
        double sumX = 0, sumY = 0;
        long count = 0;
        for (int y = 0; y < outH; y++)
        {
            for (int x = 0; x < outW; x++)
            {
                if (shapeAlpha[y * outW + x] >= 0.5)
                {
                    sumX += x; sumY += y; count++;
                }
            }
        }
        if (count < 100)
        {
            centerX = outW / 2.0; centerY = outH / 2.0; radius = -1;
            return;
        }
        double cx = sumX / count, cy = sumY / count;

        // Pass 2: refine centroid by excluding pixels far from the rough center
        // (removes + badge influence so center sits on the disc's true center).
        double maxR = Math.Min(outW, outH) / 2.0;
        double trimR2 = maxR * maxR * 0.85 * 0.85; // within ~85% of half-image
        sumX = 0; sumY = 0; count = 0;
        for (int y = 0; y < outH; y++)
        {
            for (int x = 0; x < outW; x++)
            {
                if (shapeAlpha[y * outW + x] >= 0.5)
                {
                    double dx0 = x - cx, dy0 = y - cy;
                    if (dx0 * dx0 + dy0 * dy0 <= trimR2)
                    {
                        sumX += x; sumY += y; count++;
                    }
                }
            }
        }
        if (count > 100) { cx = sumX / count; cy = sumY / count; }

        centerX = cx; centerY = cy;

        // Ray-cast 72 rays from fitted center; record outermost shape-body hit.
        const int numRays = 72;
        var radii = new List<double>(numRays);
        for (int i = 0; i < numRays; i++)
        {
            double angle = i * (2.0 * Math.PI / numRays);
            double dx = Math.Cos(angle), dy = Math.Sin(angle);
            double outermost = -1;
            for (double r = 0; r < maxR; r += 0.5)
            {
                int px = (int)Math.Round(cx + dx * r);
                int py = (int)Math.Round(cy + dy * r);
                if ((uint)px >= (uint)outW || (uint)py >= (uint)outH) break;
                if (shapeAlpha[py * outW + px] >= 0.5)
                    outermost = r;
            }
            if (outermost > 5) radii.Add(outermost);
        }

        // Lenient threshold: discs with gear-like teeth (e.g. Drums) only have
        // shape body along a subset of ray angles. As long as enough rays hit
        // something, the P75 of their outer radii is a reliable disc fit.
        if (radii.Count < numRays / 4) { radius = -1; return; }

        radii.Sort();
        // 75th percentile: covers the disc ring and extends past it far enough
        // to encompass instrument silhouettes that cross the ring boundary.
        int p75Index = (int)Math.Min(radii.Count - 1, Math.Round(radii.Count * 0.75));
        radius = radii[p75Index];
    }

    private static bool TryDetectRawCenteredDiscRadius(SKBitmap src, double outputScale, DistanceSampleMode mode, out double radius)
    {
        radius = -1;

        int centerX = src.Width / 2;
        int centerY = src.Height / 2;
        var cardinals = new (int dx, int dy)[] { (-1, 0), (1, 0), (0, -1), (0, 1) };
        var samples = new List<double>(cardinals.Length);

        foreach (var (dx, dy) in cardinals)
        {
            int lastHit = -1;
            int maxSteps = Math.Max(src.Width, src.Height) / 2;
            for (int step = 0; step < maxSteps; step++)
            {
                int px = centerX + dx * step;
                int py = centerY + dy * step;
                if ((uint)px >= (uint)src.Width || (uint)py >= (uint)src.Height)
                {
                    break;
                }

                double d = SamplePixelDistance(src, px, py, src.Width, src.Height, mode);
                if (d >= 0.5)
                {
                    lastHit = step;
                }
            }

            if (lastHit < 0)
            {
                return false;
            }

            samples.Add(lastHit * outputScale);
        }

        double min = samples.Min();
        double max = samples.Max();
        if (max - min > 8.0)
        {
            return false;
        }

        radius = samples.Average();
        return true;
    }

    private static bool[] FindLargestWhiteComponentMask(double[] shapeAlpha, int width, int height)
    {
        int size = width * height;
        var visited = new bool[size];
        var largestMask = new bool[size];
        int largestCount = 0;
        var queue = new Queue<int>();
        var componentPixels = new List<int>();

        for (int start = 0; start < size; start++)
        {
            if (visited[start] || shapeAlpha[start] < 0.5)
            {
                continue;
            }

            queue.Clear();
            componentPixels.Clear();
            visited[start] = true;
            queue.Enqueue(start);

            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                componentPixels.Add(current);
                int x = current % width;
                int y = current / width;

                for (int dy = -1; dy <= 1; dy++)
                {
                    int yy = y + dy;
                    if ((uint)yy >= (uint)height) continue;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int xx = x + dx;
                        if ((uint)xx >= (uint)width) continue;
                        int next = yy * width + xx;
                        if (visited[next] || shapeAlpha[next] < 0.5) continue;
                        visited[next] = true;
                        queue.Enqueue(next);
                    }
                }
            }

            if (componentPixels.Count <= largestCount)
            {
                continue;
            }

            Array.Clear(largestMask, 0, largestMask.Length);
            foreach (int idx in componentPixels)
            {
                largestMask[idx] = true;
            }
            largestCount = componentPixels.Count;
        }

        return largestMask;
    }

    private static byte[] BuildDetachedOutlineAlpha(double[] shapeAlpha, bool[] primaryComponentMask, int width, int height, int outlinePx)
    {
        var detachedMask = new bool[width * height];
        var detachedPixels = new List<int>();
        for (int idx = 0; idx < shapeAlpha.Length; idx++)
        {
            if (shapeAlpha[idx] >= 0.5 && !primaryComponentMask[idx])
            {
                detachedMask[idx] = true;
                detachedPixels.Add(idx);
            }
        }

        var outline = new byte[width * height];
        if (detachedPixels.Count == 0)
        {
            return outline;
        }

        int radiusSq = outlinePx * outlinePx;
        foreach (int idx in detachedPixels)
        {
            int x = idx % width;
            int y = idx / width;
            int minY = Math.Max(0, y - outlinePx);
            int maxY = Math.Min(height - 1, y + outlinePx);
            int minX = Math.Max(0, x - outlinePx);
            int maxX = Math.Min(width - 1, x + outlinePx);

            for (int yy = minY; yy <= maxY; yy++)
            {
                int dy = yy - y;
                int dySq = dy * dy;
                for (int xx = minX; xx <= maxX; xx++)
                {
                    int dx = xx - x;
                    if (dx * dx + dySq > radiusSq)
                    {
                        continue;
                    }

                    int targetIdx = yy * width + xx;
                    if (detachedMask[targetIdx])
                    {
                        continue;
                    }

                    outline[targetIdx] = 255;
                }
            }
        }

        return outline;
    }

    private static void WritePngWithPreview(SKBitmap dst, string outPath)
    {
        using var img = SKImage.FromBitmap(dst);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        using (var fs = File.Create(outPath)) data.SaveTo(fs);

        var previewDir = Path.Combine(Path.GetDirectoryName(outPath)!, "_preview");
        Directory.CreateDirectory(previewDir);
        var previewPath = Path.Combine(previewDir, Path.GetFileName(outPath));
        using var preview = new SKBitmap(dst.Width, dst.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(preview))
        {
            canvas.Clear(new SKColor(30, 30, 40));
            canvas.DrawBitmap(dst, 0, 0);
        }
        using var previewImg = SKImage.FromBitmap(preview);
        using var previewData = previewImg.Encode(SKEncodedImageFormat.Png, 95);
        using (var pfs = File.Create(previewPath)) previewData.SaveTo(pfs);
    }
}
