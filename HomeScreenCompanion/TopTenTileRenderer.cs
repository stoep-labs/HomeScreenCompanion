using SkiaSharp;
using System;
using System.IO;
using static HomeScreenCompanion.ArtCanvas;

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

        private static SKTypeface LoadNumeralFont() => LoadFont("Anton.ttf");
    }
}
