using SkiaSharp;
using System;
using System.IO;

namespace HomeScreenCompanion
{
    /// <summary>
    /// Renders Netflix-style "Top 10" art: a huge outlined rank numeral with the movie poster as
    /// a rounded card, on a blurred, darkened copy of the same poster. Two shapes:
    ///   Render       — 1280x720 tile (numeral left, poster right) for the Thumb image.
    ///   RenderPoster — 1500x1200 (5:4) card (full-height poster right, numeral behind it) for the Primary image.
    ///
    /// Both are always composed from the Primary (poster) image. The numeral size is fixed by
    /// what makes "10" fit, so every image in a list has the same numeral height.
    /// </summary>
    internal static class TopTenTileRenderer
    {
        public const int Width = 1280;
        public const int Height = 720;
        public const int PosterWidth = 1500;
        public const int PosterHeight = 1200;

        private const int TileCardH = 610;       // poster card height on the landscape tile
        private const int TileNumeralBoxW = 640; // "10" must fit inside this width
        private const int Stroke = 9;        // outline thickness outside the glyph

        private static readonly SKColor Base = new SKColor(13, 13, 16);
        private static readonly SKColor Outline = new SKColor(150, 150, 160);

        private static readonly Lazy<SKTypeface> Numerals = new Lazy<SKTypeface>(LoadNumeralFont);

        public static void Render(string posterPath, int rank, string outputPath)
        {
            if (rank < 1 || rank > 10)
                throw new ArgumentOutOfRangeException(nameof(rank), "rank must be between 1 and 10");

            using var poster = SKBitmap.Decode(posterPath)
                ?? throw new InvalidOperationException($"SkiaSharp could not decode '{posterPath}' (unsupported format or corrupt file)");
            using var surface = SKSurface.Create(new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul))
                ?? throw new InvalidOperationException($"SkiaSharp could not create a {Width}x{Height} surface");
            var canvas = surface.Canvas;

            DrawBackdrop(canvas, poster, Width, Height);
            DrawScrim(canvas, Width, Height, towardBottom: false);
            DrawVignette(canvas, Width, Height, 0.5f);

            int pw = (int)(TileCardH / 1.5);
            int py = (Height - TileCardH) / 2;
            int overlap = (int)(pw * 0.05);
            float size = NumeralSize(TileNumeralBoxW, TileCardH * 1.02f);
            var num = rank.ToString();

            using var fill = NumeralPaint(size, SKTextAlign.Right);
            using var outline = OutlinePaint(size, SKTextAlign.Right);

            // Centre the numeral+poster group; the numeral tucks under the poster's left edge.
            int numW = (int)(InkWidth(fill, num) + Stroke * 2);
            int groupW = numW - overlap + pw;
            int px = (Width - groupW) / 2 + numW - overlap;
            px = Math.Min(px, Width - 90 - pw);

            float textX = px + overlap;
            float textY = py + TileCardH;
            canvas.DrawText(num, textX, textY, outline);
            canvas.DrawText(num, textX, textY, fill);

            DrawCard(canvas, poster, new SKRect(px, py, px + pw, py + TileCardH), RadiusFor(pw));
            Save(surface, outputPath);
        }

        // Netflix-style card for the Primary image: 5:4 (Emby treats 1.2-1.4 as its
        // "fourThree" card shape; it picks the shape from the images' aspect ratio). The poster
        // runs the full height on the right; the numeral sits behind its left edge.
        public static void RenderPoster(string posterPath, int rank, string outputPath)
        {
            if (rank < 1 || rank > 10)
                throw new ArgumentOutOfRangeException(nameof(rank), "rank must be between 1 and 10");

            const int w = PosterWidth, h = PosterHeight;
            const int margin = 16;
            using var poster = SKBitmap.Decode(posterPath)
                ?? throw new InvalidOperationException($"SkiaSharp could not decode '{posterPath}' (unsupported format or corrupt file)");
            using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul))
                ?? throw new InvalidOperationException($"SkiaSharp could not create a {w}x{h} surface");
            var canvas = surface.Canvas;

            DrawBackdrop(canvas, poster, w, h);
            DrawScrim(canvas, w, h, towardBottom: false);
            DrawVignette(canvas, w, h, 0.45f);

            // Emby shows this image in a 4:3 card and crops the top and bottom; the poster sits
            // inside the visible part so its rounded corners show.
            int crop = (h - w * 3 / 4) / 2;
            int py = crop + margin;
            int ph = h - py * 2;
            int pw = (int)(ph / 1.5);
            int px = w - margin - pw;
            int overlap = (int)(pw * 0.10);

            // Netflix style: every numeral runs (nearly) the full poster height; one that is too
            // wide for the space left of the poster (e.g. "10") is squeezed horizontally instead
            // of shrunk. Emby shows this image in a 4:3 card and crops ~37px off the top and
            // bottom, so the numeral (with its outline and the overshoot of round digits) stays
            // inside that safe area.
            int safe = crop + 27;
            float size = SizeForCapHeight(h - safe * 2);
            var num = rank.ToString();
            using var fill = NumeralPaint(size, SKTextAlign.Right);
            using var outline = OutlinePaint(size, SKTextAlign.Right);
            float room = px + overlap - margin;
            float inkW = InkWidth(fill, num) + Stroke * 2;
            if (inkW > room)
            {
                fill.TextScaleX = room / inkW;
                outline.TextScaleX = room / inkW;
            }
            float textX = px + overlap;
            float textY = h - safe;
            canvas.DrawText(num, textX, textY, outline);
            canvas.DrawText(num, textX, textY, fill);

            DrawCard(canvas, poster, new SKRect(px, py, px + pw, py + ph), RadiusFor(pw));
            Save(surface, outputPath);
        }

        private static void Save(SKSurface surface, string outputPath)
        {
            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, 92);
            using var stream = File.Create(outputPath);
            data.SaveTo(stream);
        }

        // Largest size where "10" (with its outline) fits maxW and its caps fit maxCap.
        private static float NumeralSize(float maxW, float maxCap)
        {
            float size = 900;
            while (size > 50)
            {
                using var paint = NumeralPaint(size, SKTextAlign.Right);
                var bounds = new SKRect();
                paint.MeasureText("10", ref bounds);
                if (bounds.Width + Stroke * 2 <= maxW && -bounds.Top + Stroke <= maxCap)
                    return size;
                size -= 4;
            }
            return size;
        }

        // Largest size whose digit height (with outline) fits maxCap.
        private static float SizeForCapHeight(float maxCap)
        {
            float size = 1400;
            while (size > 50)
            {
                using var paint = NumeralPaint(size, SKTextAlign.Right);
                var bounds = new SKRect();
                paint.MeasureText("8", ref bounds);
                if (-bounds.Top + Stroke <= maxCap) return size;
                size -= 4;
            }
            return size;
        }

        private static SKPaint NumeralPaint(float size, SKTextAlign align) => new SKPaint
        {
            Typeface = Numerals.Value,
            TextSize = size,
            TextAlign = align,
            Color = Base,
            IsAntialias = true
        };

        // A stroke is centred on the glyph edge, so it is twice as wide and drawn under the fill.
        private static SKPaint OutlinePaint(float size, SKTextAlign align)
        {
            var paint = NumeralPaint(size, align);
            paint.Color = Outline;
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = Stroke * 2;
            paint.StrokeJoin = SKStrokeJoin.Round;
            return paint;
        }

        private static float InkWidth(SKPaint paint, string text)
        {
            var bounds = new SKRect();
            paint.MeasureText(text, ref bounds);
            return bounds.Width;
        }

        private static int RadiusFor(int cardW) => Math.Max(6, (int)Math.Round(cardW * 0.045));

        // Heavily blurred cover-fit of the poster, pulled 60% toward the base colour.
        //
        // The image is drawn through a clamped shader over a rect larger than the canvas so the
        // blur samples real edge pixels instead of transparency (no dark rim). This avoids the
        // tile-mode CreateBlur overload, which Emby's bundled SkiaSharp (2.88.3) does not have.
        private static void DrawBackdrop(SKCanvas canvas, SKBitmap poster, int w, int h)
        {
            int sw = w / 4, sh = h / 4;
            using var small = new SKBitmap(new SKImageInfo(sw, sh, SKColorType.Rgba8888, SKAlphaType.Premul));
            using (var sc = new SKCanvas(small))
            {
                var src = CoverSource(poster.Width, poster.Height, sw, sh);
                float scale = sw / src.Width;
                var fit = SKMatrix.CreateScaleTranslation(scale, scale, -src.Left * scale, -src.Top * scale);
                DrawClampedBlurred(sc, poster, fit, 17.5f, sw, sh);
            }

            DrawClampedBlurred(canvas, small, SKMatrix.CreateScale(w / (float)sw, h / (float)sh), 2f, w, h);
            using var darken = new SKPaint { Color = Base.WithAlpha((byte)(255 * 0.6)) };
            canvas.DrawRect(0, 0, w, h, darken);
        }

        private static void DrawClampedBlurred(SKCanvas canvas, SKBitmap bitmap, SKMatrix toCanvas, float sigma, int w, int h)
        {
            float pad = sigma * 3;
            using var paint = new SKPaint
            {
                Shader = SKShader.CreateBitmap(bitmap, SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, toCanvas),
                ImageFilter = SKImageFilter.CreateBlur(sigma, sigma),
                FilterQuality = SKFilterQuality.High
            };
            canvas.DrawRect(new SKRect(-pad, -pad, w + pad, h + pad), paint);
        }

        // Base colour fading in from 20% to 85% of the way toward the left (or bottom) edge,
        // so the numeral has a dark ground to sit on.
        private static void DrawScrim(SKCanvas canvas, int w, int h, bool towardBottom)
        {
            const int stops = 64;
            var colors = new SKColor[stops + 1];
            var positions = new float[stops + 1];
            for (int i = 0; i <= stops; i++)
            {
                float p = i / (float)stops;           // position along the gradient line
                float toward = towardBottom ? p : 1 - p;
                float t = Clamp01((toward - 0.2f) / 0.65f);
                colors[i] = Base.WithAlpha((byte)Math.Round(255 * Math.Pow(t, 1.3)));
                positions[i] = p;
            }
            using var paint = new SKPaint
            {
                Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), towardBottom ? new SKPoint(0, h) : new SKPoint(w, 0), colors, positions, SKShaderTileMode.Clamp)
            };
            canvas.DrawRect(0, 0, w, h, paint);
        }

        // Elliptical edge darkening; strength is the corner opacity.
        private static void DrawVignette(SKCanvas canvas, int w, int h, float strength)
        {
            const int stops = 64;
            var colors = new SKColor[stops + 1];
            var positions = new float[stops + 1];
            for (int i = 0; i <= stops; i++)
            {
                float r = i / (float)stops;
                float t = Clamp01((255 * r - 70) / 185f);
                colors[i] = SKColors.Black.WithAlpha((byte)Math.Round(255 * strength * Math.Pow(t, 1.6)));
                positions[i] = r;
            }
            var toEllipse = SKMatrix.CreateScaleTranslation(w / 2f, h / 2f, w / 2f, h / 2f);
            using var paint = new SKPaint
            {
                Shader = SKShader.CreateRadialGradient(new SKPoint(0, 0), 1, colors, positions, SKShaderTileMode.Clamp, toEllipse)
            };
            canvas.DrawRect(0, 0, w, h, paint);
        }

        // Rounded, cover-fitted poster card with a soft drop shadow and a faint hairline edge.
        private static void DrawCard(SKCanvas canvas, SKBitmap poster, SKRect rect, float radius)
        {
            var rr = new SKRoundRect(rect, radius);

            using (var shadow = new SKPaint
            {
                Color = SKColors.Black.WithAlpha(230),
                MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 26),
                IsAntialias = true
            })
            {
                var dropped = new SKRoundRect(SKRect.Create(rect.Left, rect.Top + 14, rect.Width, rect.Height), radius);
                canvas.DrawRoundRect(dropped, shadow);
            }

            canvas.Save();
            canvas.ClipRoundRect(rr, SKClipOperation.Intersect, antialias: true);
            using (var img = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true })
                canvas.DrawBitmap(poster, CoverSource(poster.Width, poster.Height, (int)rect.Width, (int)rect.Height), rect, img);
            canvas.Restore();

            using var edge = new SKPaint
            {
                Color = SKColors.White.WithAlpha(28),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = Math.Max(1, (int)rect.Width / 300),
                IsAntialias = true
            };
            var inner = rr;
            inner.Deflate(edge.StrokeWidth / 2, edge.StrokeWidth / 2);
            canvas.DrawRoundRect(inner, edge);
        }

        // Centred source crop so the image fills w x h without distortion (CSS object-fit: cover).
        private static SKRect CoverSource(int srcW, int srcH, int w, int h)
        {
            float scale = Math.Max(w / (float)srcW, h / (float)srcH);
            float cw = w / scale, ch = h / scale;
            float x = (srcW - cw) / 2, y = (srcH - ch) / 2;
            return new SKRect(x, y, x + cw, y + ch);
        }

        private static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;

        private static SKTypeface LoadNumeralFont()
        {
            using var stream = typeof(TopTenTileRenderer).Assembly.GetManifestResourceStream("HomeScreenCompanion.Fonts.Anton.ttf");
            if (stream == null) return SKTypeface.Default;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return SKTypeface.FromData(SKData.CreateCopy(ms.ToArray())) ?? SKTypeface.Default;
        }
    }
}
