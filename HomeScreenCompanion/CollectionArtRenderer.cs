using MediaBrowser.Model.Entities;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using static HomeScreenCompanion.ArtCanvas;

namespace HomeScreenCompanion
{
    /// <summary>
    /// Generated collection art, drawn from the posters of the collection's titles (in list order).
    /// Poster = 1000x1500, background = 1920x1080. Styles (TagConfig.CollectionPosterStyle /
    /// CollectionBackgroundStyle). Fan, wall, hero strip, spotlight and ranked follow the mockups
    /// (stoep-shelves/docs/art-mockups/mock.py); grid follows collectra/artkit:
    ///   collage     — the Collectra/Collectify collage: posters edge to edge in a grid (spare cells
    ///                 filled with repeats, never beside the same poster); the poster gets the name in
    ///                 yellow Roboto Bold on a translucent box at the bottom, the background no text.
    ///   grid        — an even grid of rounded poster cards over a blurred backdrop, title below.
    ///   fan         — cards fanned out like a hand of playing cards, title underneath.
    ///   wall        — a tilted, brick-offset wall of posters with a title band (posters repeat).
    ///   ranked      — numbered Top 10 cards (big outlined numerals with a poster against each).
    ///   hero_strip  — the first poster as a soft hero, the title, then a strip of cards.
    ///   spotlight   — background: text panel left, first poster bleeding in on the right
    ///                 (as a poster it renders hero_strip).
    /// </summary>
    internal static class CollectionArtRenderer
    {
        public const string Collage = "collage", Grid = "grid", Fan = "fan", Wall = "wall", HeroStrip = "hero_strip", Spotlight = "spotlight", Ranked = "ranked";
        public static readonly string[] Styles = { Collage, Grid, Fan, Wall, HeroStrip, Spotlight, Ranked };

        // How many titles a style can show (the most it draws from).
        public static int PostersFor(string style, bool background)
        {
            switch ((style ?? "").Trim().ToLowerInvariant())
            {
                case Collage: return background ? 40 : 9;
                case Wall: return 20;
                case Ranked: return background ? 5 : 4;
                case Fan: return background ? 7 : 5;
                case HeroStrip: return background ? 7 : 6;
                case Spotlight: return background ? 5 : 6;
                default: return background ? 14 : 12;
            }
        }

        public const int PosterW = 1000, PosterH = 1500;
        public const int BackgroundW = 1920, BackgroundH = 1080;

        private const float PosterRatio = 1.5f;   // height / width of a movie poster
        private static readonly SKColor Accent = new SKColor(245, 197, 24);
        private static readonly Lazy<SKTypeface> Display = new Lazy<SKTypeface>(() => LoadFont("BebasNeue.ttf"));
        private static readonly Lazy<SKTypeface> Heavy = new Lazy<SKTypeface>(() => LoadFont("Anton.ttf"));
        private static readonly Lazy<SKTypeface> Roboto = new Lazy<SKTypeface>(() => LoadFont("Roboto-Bold.ttf"));

        public static bool IsStyle(string? style) => Styles.Contains((style ?? "").Trim().ToLowerInvariant());

        /// <summary>Each title once (by IMDb id, else name + year), in order: a library can hold
        /// the same film twice (versions, copies), and the art must never show a poster twice.</summary>
        public static List<MediaBrowser.Controller.Entities.BaseItem> DistinctTitles(IEnumerable<MediaBrowser.Controller.Entities.BaseItem> items)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<MediaBrowser.Controller.Entities.BaseItem>();
            foreach (var item in items)
            {
                var imdb = item.GetProviderId("Imdb");
                var key = !string.IsNullOrEmpty(imdb) ? imdb : (item.Name ?? "").Trim() + "|" + item.ProductionYear;
                if (seen.Add(key)) result.Add(item);
            }
            return result;
        }

        /// <summary>The text for the art: <paramref name="template"/> ("" = "{name}") with {name}
        /// (also {collectionName}) and {week} (ISO week number of today) filled in.</summary>
        public static string ArtTitle(string? template, string name)
        {
            var t = string.IsNullOrWhiteSpace(template) ? "{name}" : template!.Trim();
            var week = System.Globalization.CultureInfo.InvariantCulture.Calendar.GetWeekOfYear(
                DateTime.Now.AddDays(DateTime.Now.DayOfWeek == DayOfWeek.Sunday ? -3 : 3 - ((int)DateTime.Now.DayOfWeek - 1)),
                System.Globalization.CalendarWeekRule.FirstFourDayWeek, DayOfWeek.Monday);
            return t.Replace("{collectionName}", name).Replace("{name}", name).Replace("{week}", week.ToString()).Trim();
        }

        private static readonly Dictionary<string, string> SampleCache = new Dictionary<string, string>();

        /// <summary>
        /// A small example of a style (JPEG data: URL) for the style picker, drawn by the real
        /// renderer from plain coloured stand-in posters (no real artwork). Cached per process.
        /// </summary>
        public static string Sample(string style, bool background, string tempDir)
        {
            var key = style + (background ? "|bg" : "|poster");
            lock (SampleCache)
                if (SampleCache.TryGetValue(key, out var cached)) return cached;

            Directory.CreateDirectory(tempDir);
            var posters = new List<string>();
            for (int i = 0; i < 12; i++)
            {
                var path = Path.Combine(tempDir, $"stand-in-{i}.jpg");
                if (!File.Exists(path)) DrawStandInPoster(i, path);
                posters.Add(path);
            }
            var output = Path.Combine(tempDir, $"sample-{style}-{(background ? "bg" : "poster")}.jpg");
            Render(style, posters, "Collection", background, output);
            using var full = SKBitmap.Decode(output);
            int th = background ? 135 : 180, tw = (int)Math.Round(full.Width * th / (double)full.Height);
            using var small = full.Resize(new SKImageInfo(tw, th), SKFilterQuality.High);
            using var img = SKImage.FromBitmap(small);
            using var data = img.Encode(SKEncodedImageFormat.Jpeg, 82);
            var url = "data:image/jpeg;base64," + Convert.ToBase64String(data.ToArray());
            lock (SampleCache) SampleCache[key] = url;
            return url;
        }

        // A plain poster-shaped gradient in one of twelve hues, with a lighter "title" bar.
        private static void DrawStandInPoster(int i, string path)
        {
            const int w = 300, h = 450;
            float hue = (i * 47) % 360;
            using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
            var c = surface.Canvas;
            using (var g = new SKPaint
            {
                Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, h),
                    new[] { SKColor.FromHsv(hue, 55, 85), SKColor.FromHsv((hue + 30) % 360, 70, 35) }, null, SKShaderTileMode.Clamp)
            }) c.DrawRect(0, 0, w, h, g);
            using (var disc = new SKPaint { Color = SKColors.White.WithAlpha(60), IsAntialias = true }) c.DrawCircle(w / 2f, h * 0.42f, w * 0.22f, disc);
            using (var bar = new SKPaint { Color = SKColors.White.WithAlpha(200), IsAntialias = true })
                c.DrawRoundRect(SKRect.Create(w * 0.18f, h * 0.78f, w * 0.64f, h * 0.06f), 6, 6, bar);
            Save(surface, path);
        }

        /// <summary>Renders <paramref name="style"/> to a JPEG. Needs at least one poster file.</summary>
        public static void Render(string style, IList<string> posterPaths, string title, bool background, string outputPath)
        {
            var posters = new List<SKBitmap>();
            try
            {
                foreach (var path in posterPaths)
                {
                    var bmp = SKBitmap.Decode(path);
                    if (bmp != null) posters.Add(bmp);
                }
                if (posters.Count == 0) throw new InvalidOperationException("none of the posters could be read");

                int w = background ? BackgroundW : PosterW, h = background ? BackgroundH : PosterH;
                using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul))
                    ?? throw new InvalidOperationException($"SkiaSharp could not create a {w}x{h} surface");
                var canvas = surface.Canvas;
                canvas.Clear(Base);

                switch ((style ?? "").Trim().ToLowerInvariant())
                {
                    case Fan: DrawFan(canvas, posters, title, background, w, h); break;
                    case Collage: DrawCollage(canvas, posters, background ? "" : title, w, h); break;
                    case Wall: DrawWall(canvas, posters, title, background, w, h); break;
                    case Ranked: DrawRanked(canvas, posters, title, background, w, h); break;
                    case HeroStrip:
                        if (background) DrawHeroStripBackground(canvas, posters, title, w, h);
                        else DrawHeroStripPoster(canvas, posters, title, w, h);
                        break;
                    case Spotlight:
                        if (background) DrawSpotlight(canvas, posters, title, w, h);
                        else DrawHeroStripPoster(canvas, posters, title, w, h);
                        break;
                    default: DrawGrid(canvas, posters, title, background, w, h); break;
                }
                Save(surface, outputPath);
            }
            finally
            {
                foreach (var p in posters) p.Dispose();
            }
        }

        // ── grid ────────────────────────────────────────────────────────────────────────
        // Posters: up to 12 (1, 2x1, 3x2, 3x3, 4x3…); backgrounds: up to 14 in at most two rows.
        // A partial last row is centred. The title sits in a dark band at the bottom; a two-line
        // title gets an accent first line.
        private static void DrawGrid(SKCanvas c, List<SKBitmap> posters, string title, bool background, int w, int h)
        {
            DrawBackdrop(c, MostColourful(posters), w, h, 0.72f);
            DrawVignette(c, w, h, 0.45f);

            bool hasTitle = !string.IsNullOrWhiteSpace(title);
            int m = background ? 72 : 64;
            int titleH = hasTitle ? (background ? 200 : 300) : 0;
            int titleGap = hasTitle ? (background ? 30 : 40) : 0;
            var region = SKRect.Create(m, m, w - 2 * m, h - 2 * m - titleH - titleGap);
            var g = BestGrid(posters.Count, (int)region.Width, (int)region.Height, 22, background ? 14 : 12, background ? 2 : (int?)null);

            float radius = RadiusFor(g.CardW);
            var cells = g.Cells(region);
            for (int i = 0; i < cells.Count; i++)
                DrawCard(c, posters[i], SKRect.Create(cells[i].X, cells[i].Y, g.CardW, g.CardH), radius,
                         Math.Max(8, g.CardW / 18f), g.CardW / 30f, 190);

            if (!hasTitle) return;
            float bandTop = region.Bottom;
            Scrim(c, SKRect.Create(0, bandTop - 80, w, h - bandTop + 80), Dir.Down, 0, 0.55f, 235);
            var t = LayoutTitle(title, w - 2 * m, titleH, background ? 190 : 240);
            DrawTitle(c, t, w / 2f, bandTop + titleGap + (titleH - t.Height) / 2, TextAlign.Center);
        }

        // The mockup styles (stoep-shelves/docs/art-mockups/mock.py) were drawn at 680x1000 (poster)
        // and 1280x720 (background); sizes below are those numbers scaled to the output.
        private static float MockScale(bool background, int w) => w / (background ? 1280f : 680f);
        private static readonly SKColor MockBase = new SKColor(8, 8, 12);
        private static readonly SKColor RankRed = new SKColor(229, 9, 20);

        // Cover-fitted image, gaussian-blurred, blended toward the dark base (mock "backdrop").
        private static void MockBackdrop(SKCanvas c, SKBitmap img, int w, int h, float blur, float dark)
        {
            var src = CoverSource(img.Width, img.Height, w, h);
            float scale = w / src.Width;
            var fit = SKMatrix.CreateScaleTranslation(scale, scale, -src.Left * scale, -src.Top * scale);
            DrawClampedBlurred(c, img, fit, blur, w, h);
            using var tint = new SKPaint { Color = MockBase.WithAlpha((byte)Math.Round(255 * dark)) };
            c.DrawRect(0, 0, w, h, tint);
        }

        // Rounded card without the soft drop shadow (mock "rounded"), or with it (mock "shadow").
        private static void MockCard(SKCanvas c, SKBitmap img, float x, float y, float cw, float ch, float r, bool shadow, float s)
        {
            if (shadow) DrawCard(c, img, SKRect.Create(x, y, cw, ch), r, 18 * s, 10 * s, 170);
            else DrawCard(c, img, SKRect.Create(x, y, cw, ch), r, 1, 0, 0);
        }

        // Text whose top edge sits at y (like PIL's text at (x, y)).
        private static float MockText(SKCanvas c, string text, float x, float y, SKTypeface face, float size, SKColor colour,
            SKTextAlign align = SKTextAlign.Left, float stroke = 0, SKColor? strokeColour = null)
        {
            using var paint = new SKPaint { Typeface = face, TextSize = size, IsAntialias = true, TextAlign = align, Color = colour };
            var bounds = new SKRect();
            paint.MeasureText(text, ref bounds);
            float baseline = y - bounds.Top;
            if (stroke > 0)
            {
                using var sp = new SKPaint
                {
                    Typeface = face, TextSize = size, IsAntialias = true, TextAlign = align,
                    Color = strokeColour ?? SKColors.Black, Style = SKPaintStyle.Stroke, StrokeWidth = stroke * 2, StrokeJoin = SKStrokeJoin.Round
                };
                c.DrawText(text, x, baseline, sp);
            }
            c.DrawText(text, x, baseline, paint);
            return bounds.Height;
        }

        // Title split into at most two balanced lines that fit maxW at maxSize (shrinking if needed).
        // preferTwo: always two lines when there is more than one word (the two-tone hero titles).
        private static (string[] lines, float size) MockLines(string title, SKTypeface face, float maxW, float maxSize, bool upper = true, bool preferTwo = false)
        {
            string text = string.Join(" ", (upper ? title.ToUpperInvariant() : title).Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
            using var probe = new SKPaint { Typeface = face, TextSize = 100 };
            float SizeFor(string[] ls) => Math.Min(maxSize, 100 * maxW / Math.Max(1, ls.Max(l => probe.MeasureText(l))));
            var one = new[] { text };
            var words = text.Split(' ');
            string[] best = one;
            if (words.Length > 1)
            {
                string[]? two = null; float twoSize = 0;
                for (int i = 1; i < words.Length; i++)
                {
                    var cand = new[] { string.Join(" ", words.Take(i)), string.Join(" ", words.Skip(i)) };
                    float sz = SizeFor(cand);
                    if (two == null || sz > twoSize) { two = cand; twoSize = sz; }
                }
                if (two != null && (preferTwo || SizeFor(one) < maxSize * 0.8f)) best = two;
            }
            return (best, SizeFor(best));
        }

        // ── collage (Collectra) ─────────────────────────────────────────────────────────
        private static void DrawCollage(SKCanvas c, List<SKBitmap> posters, string title, int w, int h)
        {
            int n = posters.Count, rows, cols;
            if (w < h) { rows = Math.Max(1, (int)Math.Sqrt(n)); cols = (n + rows - 1) / rows; }
            else { rows = Math.Max(1, (int)(Math.Sqrt(n) / (w / (double)h))); cols = (n + rows - 1) / rows; }
            int cellW = w / cols, cellH = h / rows;
            var used = new int[rows * cols];
            var rng = new Random(n * 7919 + w);
            using var paint = new SKPaint { FilterQuality = SKFilterQuality.High };
            for (int i = 0; i < rows * cols; i++)
            {
                int pick = i;
                if (i >= n)
                {
                    var avoid = new HashSet<int>();
                    if (i % cols > 0) avoid.Add(used[i - 1]);
                    if (i >= cols) avoid.Add(used[i - cols]);
                    var cands = Enumerable.Range(0, n).Where(x => !avoid.Contains(x)).ToList();
                    if (cands.Count == 0) cands = Enumerable.Range(0, n).ToList();
                    pick = cands[rng.Next(cands.Count)];
                }
                used[i] = pick;
                var p = posters[pick];
                c.DrawBitmap(p, CoverSource(p.Width, p.Height, cellW, cellH), SKRect.Create((i % cols) * cellW, (i / cols) * cellH, cellW, cellH), paint);
            }
            if (string.IsNullOrWhiteSpace(title)) return;

            // Collectra's text overlay: wrapped to 90% of the width, at the bottom (30px margin),
            // on a 50% black box (16px padding), yellow with a 2px black shadow.
            // Collectify's settings: Roboto Bold 95px on a 680px poster, #F5C518.
            float size = 95 * w / 680f, margin = 30 * w / 680f, pad = Math.Max(16, 16 * w / 680f), shadowOff = 2 * w / 680f;
            using var text = new SKPaint { Typeface = Roboto.Value, TextSize = size, IsAntialias = true, Color = new SKColor(245, 197, 24) };
            float maxW = w * 0.9f;
            var lines = new List<string>();
            foreach (var word in title.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (lines.Count > 0 && text.MeasureText(lines[lines.Count - 1] + " " + word) <= maxW) lines[lines.Count - 1] += " " + word;
                else lines.Add(word);
            }
            var tg = new SKRect(); text.MeasureText("Tg", ref tg);
            float lineH = tg.Height + 5 * w / 680f;
            float total = lineH * lines.Count;
            float startY = h - total - margin;
            float widest = lines.Max(l => text.MeasureText(l));
            using (var box = new SKPaint { Color = SKColors.Black.WithAlpha(128) })
                c.DrawRect(new SKRect((w - widest) / 2 - pad, startY - pad, (w + widest) / 2 + pad, startY + total + pad), box);
            using var shadow = new SKPaint { Typeface = Roboto.Value, TextSize = size, IsAntialias = true, Color = SKColors.Black };
            for (int i = 0; i < lines.Count; i++)
            {
                var lb = new SKRect(); text.MeasureText(lines[i], ref lb);
                float x = (w - text.MeasureText(lines[i])) / 2;
                float y = startY + i * lineH + (lineH - lb.Height) / 2 - lb.Top;
                c.DrawText(lines[i], x + shadowOff, y + shadowOff, shadow);
                c.DrawText(lines[i], x, y, text);
            }
        }

        // ── fan ─────────────────────────────────────────────────────────────────────────
        // Cards fanned like a hand of playing cards, the middle one on top; title underneath.
        private static void DrawFan(SKCanvas c, List<SKBitmap> posters, string title, bool background, int w, int h)
        {
            float s = MockScale(background, w);
            MockBackdrop(c, posters.Count > 3 ? posters[3] : posters[0], w, h, 34 * s, 0.55f);
            int n = Math.Min(posters.Count, background ? 7 : 5);
            float cw = (background ? 230 : 260) * s, ch = cw * PosterRatio;
            float cy = (background ? 330 : 440) * (h / (background ? 720f : 1000f));
            float spread = background ? 7 : 9;
            float mid = (n - 1) / 2f;
            foreach (int i in Enumerable.Range(0, n).OrderByDescending(i => Math.Abs(i - mid)))
            {
                float k = i - mid;
                c.Save();
                c.Translate(w / 2f + k * cw * 0.55f, cy + (float)Math.Pow(Math.Abs(k), 1.6) * cw * 0.09f);
                c.RotateDegrees(k * spread);
                DrawCard(c, posters[i], SKRect.Create(-cw / 2, -ch / 2, cw, ch), 14 * s, 16 * s, 12 * s, 170);
                c.Restore();
            }
            if (string.IsNullOrWhiteSpace(title)) return;
            var (lines, size) = MockLines(title, Display.Value, w - 80 * s, (background ? 96 : 110) * s);
            float y = (background ? 590 : 800) * (h / (background ? 720f : 1000f));
            foreach (var line in lines)
            {
                MockText(c, line, w / 2f, y, Display.Value, size, SKColors.White, SKTextAlign.Center, 2 * s);
                y += size * 0.95f;
            }
        }

        // ── wall ────────────────────────────────────────────────────────────────────────
        // A brick-offset wall of posters tilted 14°, dimmed a little, with a dark title band and
        // gold lines across the middle. Posters repeat so the wall is always full.
        private static void DrawWall(SKCanvas c, List<SKBitmap> posters, string title, bool background, int w, int h)
        {
            float s = MockScale(background, w);
            float cw = 150 * s, ch = (int)(150 * PosterRatio) * s, gap = 10 * s;
            float bigW = w * 1.8f, bigH = h * 1.8f;
            int cols = (int)Math.Ceiling(bigW / (cw + gap)) + 1, rows = (int)Math.Ceiling(bigH / (ch + gap)) + 1;
            c.Save();
            c.Translate(w / 2f, h / 2f);
            c.RotateDegrees(-14);
            c.Translate(-bigW / 2, -bigH / 2);
            // Posters repeat to fill the wall, but never touch a card showing the same poster: not
            // the card to the left, nor the two cards above (rows are offset by half a card).
            var grid = new int[rows, cols];
            int next = 0;
            for (int r = 0; r < rows; r++)
                for (int col = 0; col < cols; col++)
                {
                    // Cards that touch this one, and (when there are enough films) the ring around
                    // those, so a repeat is at least two cards away.
                    var touching = new HashSet<int>();
                    var near = new HashSet<int>();
                    void Mark(HashSet<int> set, int rr, int cc) { if (rr >= 0 && cc >= 0 && cc < cols) set.Add(grid[rr, cc]); }
                    Mark(touching, r, col - 1); Mark(near, r, col - 2);
                    if (r > 0)
                    {
                        int a = r % 2 == 1 ? col : col - 1;   // odd rows sit half a card to the right
                        Mark(touching, r - 1, a); Mark(touching, r - 1, a + 1);
                        Mark(near, r - 1, a - 1); Mark(near, r - 1, a + 2);
                    }
                    if (r > 1) { Mark(near, r - 2, col - 1); Mark(near, r - 2, col); Mark(near, r - 2, col + 1); }
                    near.UnionWith(touching);
                    int pick = next % posters.Count;
                    var avoid = posters.Count > near.Count ? near : touching;
                    for (int tries = 0; tries < posters.Count && avoid.Contains(pick); tries++)
                        pick = (pick + 1) % posters.Count;
                    grid[r, col] = pick;
                    next = pick + 1;
                    MockCard(c, posters[pick], col * (cw + gap) + (r % 2) * (cw / 2), r * (ch + gap), cw, ch, 8 * s, false, s);
                }
            c.Restore();
            using (var dim = new SKPaint { Color = SKColors.Black.WithAlpha(90) }) c.DrawRect(0, 0, w, h, dim);

            if (string.IsNullOrWhiteSpace(title)) return;
            float size = (background ? 110 : 104) * s;
            // One line, or two when the name is long; the band grows with the text.
            var (lines, fitted) = MockLines(title, Display.Value, w - 60 * s, size);
            using var probe = new SKPaint { Typeface = Display.Value, TextSize = fitted };
            var b = new SKRect(); probe.MeasureText("H", ref b);
            float lineStep = fitted * 0.95f;
            float textH = b.Height + (lines.Length - 1) * lineStep;
            float bandH = Math.Max(size * 1.5f, textH + size * 0.6f);
            float by = (h - bandH) / 2;
            using (var band = new SKPaint { Color = MockBase.WithAlpha(215) }) c.DrawRect(0, by, w, bandH, band);
            using (var gold = new SKPaint { Color = Accent })
            {
                c.DrawRect(0, by - 2 * s, w, 4 * s, gold);
                c.DrawRect(0, by + bandH - 2 * s, w, 4 * s, gold);
            }
            float ty = by + (bandH - textH) / 2;
            foreach (var line in lines)
            {
                MockText(c, line, w / 2f, ty, Display.Value, fitted, SKColors.White, SKTextAlign.Center);
                ty += lineStep;
            }
        }

        // ── hero strip ──────────────────────────────────────────────────────────────────
        // Poster: the first title as a lightly blurred hero fading to dark, a two-tone title,
        // and a strip of five cards along the bottom.
        private static void DrawHeroStripPoster(SKCanvas c, List<SKBitmap> posters, string title, int w, int h)
        {
            float s = MockScale(false, w), sy = h / 1000f;
            MockBackdrop(c, posters[0], w, h, 22 * s, 0.25f);
            Scrim(c, SKRect.Create(0, 0, w, h), Dir.Down, 0.35f, 1f, 245);
            int n = 5; float gap = 12 * s, margin = 30 * s;
            float cw = (w - 2 * margin - gap * (n - 1)) / n, ch = cw * PosterRatio;
            float stripTop = h - ch - 34 * sy;
            var strip = posters.Skip(1).Take(n).ToList();
            if (strip.Count == 0) strip = posters.Take(1).ToList();
            float x0 = (w - (strip.Count * cw + (strip.Count - 1) * gap)) / 2;
            for (int i = 0; i < strip.Count; i++)
                MockCard(c, strip[i], x0 + i * (cw + gap), stripTop, cw, ch, 10 * s, false, s);
            if (string.IsNullOrWhiteSpace(title)) return;
            var (lines, size) = MockLines(title, Display.Value, w - 80 * s, 132 * s, preferTwo: true);
            float lineH = size * 0.83f;
            float y = stripTop - 30 * sy - lines.Length * lineH - (size - lineH);
            for (int i = 0; i < lines.Length; i++)
                MockText(c, lines[i], w / 2f, y + i * lineH, Display.Value, size, lines.Length > 1 && i == 0 ? Accent : SKColors.White, SKTextAlign.Center);
        }

        // Background: blurred hero with a dark left-to-right fade, title on the left, and a strip
        // of six cards with drop shadows underneath.
        private static void DrawHeroStripBackground(SKCanvas c, List<SKBitmap> posters, string title, int w, int h)
        {
            float s = MockScale(true, w);
            MockBackdrop(c, posters[0], w, h, 26 * s, 0.35f);
            Scrim(c, SKRect.Create(0, 0, w, h), Dir.Left, 0f, 1f, 235);
            float cw = 130 * s, ch = 195 * s, gap = 14 * s, x0 = 70 * s;
            var strip = posters.Count > 1 ? posters.Skip(1).Take(6).ToList() : posters.Take(1).ToList();
            for (int i = 0; i < strip.Count; i++)
                MockCard(c, strip[i], x0 + i * (cw + gap), h - ch - 60 * s, cw, ch, 10 * s, true, s);
            if (string.IsNullOrWhiteSpace(title)) return;
            var (lines, size) = MockLines(title, Display.Value, w * 0.55f, 150 * s, preferTwo: true);
            float y = 150 * s;
            for (int i = 0; i < lines.Length; i++)
            {
                MockText(c, lines[i], x0, y, Display.Value, size, lines.Length > 1 && i == 0 ? Accent : SKColors.White);
                y += size * 0.86f;
            }
        }

        // ── spotlight split (background) ────────────────────────────────────────────────
        // The first title's poster on the right, fading into dark on the left; a gold
        // "COLLECTION" kicker, the title in Anton (mixed case) and four cards on the left.
        private static void DrawSpotlight(SKCanvas c, List<SKBitmap> posters, string title, int w, int h)
        {
            float s = MockScale(true, w);
            c.Clear(new SKColor(10, 10, 14));
            var hero = posters[0];
            float heroW = h * 0.9f;
            using (var p = new SKPaint { FilterQuality = SKFilterQuality.High })
                c.DrawBitmap(hero, CoverSource(hero.Width, hero.Height, (int)heroW, h), SKRect.Create(w - heroW, 0, heroW, h), p);
            // Dark from the left edge to 45% of the width, then fading out to the right edge.
            using (var fade = new SKPaint
            {
                Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(w, 0),
                    new[] { new SKColor(10, 10, 14, 255), new SKColor(10, 10, 14, 255), new SKColor(10, 10, 14, 0) },
                    new[] { 0f, 0.45f, 1f }, SKShaderTileMode.Clamp)
            }) c.DrawRect(0, 0, w, h, fade);

            float x = 70 * s;
            MockText(c, "COLLECTION", x, 120 * s, Display.Value, 34 * s, Accent);
            float y = 160 * s;
            if (!string.IsNullOrWhiteSpace(title))
            {
                var (lines, size) = MockLines(title, Heavy.Value, w * 0.42f, 120 * s, upper: false, preferTwo: true);
                foreach (var line in lines)
                {
                    MockText(c, line, x - 4 * s, y, Heavy.Value, size, SKColors.White);
                    y += size * 1.25f;
                }
            }
            float cardsY = Math.Max(500 * s, y + 30 * s);
            var strip = posters.Skip(1).Take(4).ToList();
            for (int i = 0; i < strip.Count; i++)
                MockCard(c, strip[i], x + i * 124 * s, cardsY, 110 * s, 165 * s, 8 * s, false, s);
        }

        // ── ranked (Top 10) ─────────────────────────────────────────────────────────────
        // Netflix-style numbered cards: big outlined numerals with a poster tucked against each.
        // Poster: a red "TOP 10", the title, and a 2x2 of ranks 1-4. Background: the title and
        // ranks 1-5 in a row.
        private static void DrawRanked(SKCanvas c, List<SKBitmap> posters, string title, bool background, int w, int h)
        {
            float s = MockScale(background, w);
            c.Clear(new SKColor(14, 14, 16));
            var numFill = new SKColor(14, 14, 16);
            var numStroke = new SKColor(125, 125, 133);
            float cw = 168 * s, ch = 252 * s;

            void Numbered(int rank, float x, float y, float numSize, float tuck, float cardY)
            {
                string num = rank.ToString();
                MockText(c, num, x, y, Heavy.Value, numSize, numFill, SKTextAlign.Left, (background ? 5 : 4) * s, numStroke);
                using var probe = new SKPaint { Typeface = Heavy.Value, TextSize = numSize };
                var b = new SKRect(); probe.MeasureText(num, ref b);
                float right = x + b.Right;
                MockCard(c, posters[(rank - 1) % posters.Count], right - tuck, cardY, cw, ch, 10 * s, true, s);
            }

            if (background)
            {
                if (!string.IsNullOrWhiteSpace(title))
                {
                    var (lines, size) = MockLines(title, Display.Value, w - 120 * s, 64 * s);
                    float ty = 46 * s;
                    foreach (var line in lines) { MockText(c, line, 60 * s, ty, Display.Value, size, SKColors.White); ty += size * 0.95f; }
                }
                for (int i = 0; i < Math.Min(5, posters.Count); i++)
                    Numbered(i + 1, 30 * s + i * 245 * s, 205 * s, 330 * s, 34 * s, 240 * s);
            }
            else
            {
                float sy = h / 1000f;
                MockText(c, "TOP 10", w / 2f, 34 * sy, Heavy.Value, 120 * s, RankRed, SKTextAlign.Center);
                if (!string.IsNullOrWhiteSpace(title))
                {
                    var (lines, size) = MockLines(title, Display.Value, w - 80 * s, 54 * s);
                    float sz = lines.Length > 1 ? Math.Min(size, 44 * s) : size, ty = 196 * sy;
                    foreach (var line in lines) { MockText(c, line, w / 2f, ty, Display.Value, sz, SKColors.White, SKTextAlign.Center); ty += sz * 0.95f; }
                }
                var pos = new[] { (40f, 290f), (360f, 290f), (40f, 630f), (360f, 630f) };
                for (int i = 0; i < Math.Min(4, posters.Count); i++)
                    Numbered(i + 1, pos[i].Item1 * s, (pos[i].Item2 - 10) * sy, 230 * s, 28 * s, (pos[i].Item2 + 20) * sy);
            }
        }

        // ── shared pieces ───────────────────────────────────────────────────────────────

        // The most colourful of the first five posters makes the best blurred backdrop
        // (near-white or near-black posters give a muddy grey). Ties go to the earliest.
        private static SKBitmap MostColourful(List<SKBitmap> posters)
        {
            SKBitmap best = posters[0];
            double bestScore = -1;
            foreach (var p in posters.Take(5))
            {
                using var small = p.Resize(new SKImageInfo(32, 48), SKFilterQuality.Low);
                if (small == null) continue;
                double s = 0, v = 0;
                for (int y = 0; y < small.Height; y++)
                    for (int x = 0; x < small.Width; x++)
                    {
                        small.GetPixel(x, y).ToHsv(out _, out var sat, out var val);
                        s += sat / 100; v += val / 100;
                    }
                int n = small.Width * small.Height;
                double score = (s / n) * (1 - Math.Abs(v / n - 0.5));
                if (score > bestScore + 1e-9) { best = p; bestScore = score; }
            }
            return best;
        }

        private enum Dir { Down, Up, Left }

        // Base colour fading in across a box: Down = clear at the top, dense at the bottom (Up and
        // Left likewise). start/end are fractions of the way across; alpha = max * t^curve.
        private static void Scrim(SKCanvas c, SKRect box, Dir dir, float start, float end, byte maxAlpha, float curve = 1f)
        {
            const int stops = 32;
            var colors = new SKColor[stops + 1];
            var pos = new float[stops + 1];
            float span = Math.Max(1e-4f, end - start);
            for (int i = 0; i <= stops; i++)
            {
                float p = i / (float)stops;
                float t = Clamp01((p - start) / span);
                colors[i] = Base.WithAlpha((byte)Math.Round(maxAlpha * Math.Pow(t, curve)));
                pos[i] = p;
            }
            SKPoint from, to;
            switch (dir)
            {
                case Dir.Up: from = new SKPoint(box.Left, box.Bottom); to = new SKPoint(box.Left, box.Top); break;
                case Dir.Left: from = new SKPoint(box.Right, box.Top); to = new SKPoint(box.Left, box.Top); break;
                default: from = new SKPoint(box.Left, box.Top); to = new SKPoint(box.Left, box.Bottom); break;
            }
            using var paint = new SKPaint { Shader = SKShader.CreateLinearGradient(from, to, colors, pos, SKShaderTileMode.Clamp) };
            c.DrawRect(box, paint);
        }

        // Source crop that fills w x h (cover), with the crop's focus at (fx, fy) of the image.
        private static SKRect CoverSourceAt(int srcW, int srcH, int w, int h, float fx, float fy)
        {
            float scale = Math.Max(w / (float)srcW, h / (float)srcH);
            float cw = w / scale, ch = h / scale;
            float x = (srcW - cw) * fx, y = (srcH - ch) * fy;
            return new SKRect(x, y, x + cw, y + ch);
        }

        private enum TextAlign { Center, Left }

        private readonly struct TitleLayout
        {
            public TitleLayout(string[] lines, float size, float cap, float leading, bool heavy)
            { Lines = lines; Size = size; Cap = cap; Leading = leading; Heavy = heavy; }
            public string[] Lines { get; }
            public float Size { get; }
            public float Cap { get; }
            public float Leading { get; }
            public bool Heavy { get; }
            public float Height => Cap + (Lines.Length - 1) * Leading * Size;
        }

        // Fits the title into maxW x maxH on one line or two balanced lines (two only when that
        // makes the type at least 18% larger). Bebas Neue upper case by default; Anton for spotlight.
        private static TitleLayout LayoutTitle(string title, float maxW, float maxH, float maxSize,
            bool upper = true, bool heavy = false, float leading = 1.12f)
        {
            string text = string.Join(" ", (upper ? title.ToUpperInvariant() : title).Trim()
                .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
            using var probe = new SKPaint { Typeface = heavy ? Heavy.Value : Display.Value, TextSize = 100, IsAntialias = true };
            float capAt100 = CapHeight(probe);

            (string[] lines, float size) Fit(string[] lines)
            {
                float widest = lines.Max(l => probe.MeasureText(l));
                float byWidth = 100 * maxW / Math.Max(1, widest);
                float byHeight = maxH / (capAt100 / 100 + (lines.Length - 1) * leading);
                return (lines, Math.Min(maxSize, Math.Min(byWidth, byHeight)));
            }

            var best = Fit(new[] { text });
            var words = text.Split(' ');
            if (words.Length > 1)
            {
                (string[] lines, float size)? two = null;
                for (int i = 1; i < words.Length; i++)
                {
                    var cand = Fit(new[] { string.Join(" ", words.Take(i)), string.Join(" ", words.Skip(i)) });
                    if (two == null || cand.size > two.Value.size) two = cand;
                }
                if (two != null && two.Value.size >= best.size * 1.18f) best = two.Value;
            }
            return new TitleLayout(best.lines, best.size, capAt100 * best.size / 100, leading, heavy);
        }

        // Draws a laid-out title with its cap line at top. Two-line titles get an accent first
        // line (twoTone); a soft shadow sits underneath (shadow).
        private static void DrawTitle(SKCanvas c, TitleLayout t, float x, float top, TextAlign align,
            bool twoTone = true, bool shadow = true)
        {
            var face = t.Heavy ? Heavy.Value : Display.Value;
            var skAlign = align == TextAlign.Left ? SKTextAlign.Left : SKTextAlign.Center;
            float y = top + t.Cap;
            for (int i = 0; i < t.Lines.Length; i++)
            {
                var colour = twoTone && t.Lines.Length > 1 && i == 0 ? Accent : SKColors.White;
                if (shadow)
                {
                    using var sp = new SKPaint
                    {
                        Typeface = face, TextSize = t.Size, IsAntialias = true, TextAlign = skAlign,
                        Color = SKColors.Black.WithAlpha(170),
                        MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, Math.Max(4, t.Size / 14))
                    };
                    c.DrawText(t.Lines[i], x, y, sp);
                }
                using var fill = new SKPaint { Typeface = face, TextSize = t.Size, IsAntialias = true, TextAlign = skAlign, Color = colour };
                c.DrawText(t.Lines[i], x, y, fill);
                y += t.Leading * t.Size;
            }
        }

        private static float CapHeight(SKPaint paint)
        {
            var bounds = new SKRect();
            paint.MeasureText("H", ref bounds);
            return bounds.Height;
        }

        private readonly struct GridLayout
        {
            public GridLayout(int count, int cols, int rows, int cardW, int cardH, int gap)
            { Count = count; Cols = cols; Rows = rows; CardW = cardW; CardH = cardH; Gap = gap; }
            public int Count { get; }
            public int Cols { get; }
            public int Rows { get; }
            public int CardW { get; }
            public int CardH { get; }
            public int Gap { get; }

            // Top-left of each card: block centred in the region, partial last row centred.
            public List<SKPoint> Cells(SKRect region)
            {
                float totalH = Rows * CardH + (Rows - 1) * Gap;
                float oy = region.Top + (region.Height - totalH) / 2;
                var cells = new List<SKPoint>();
                for (int i = 0; i < Count; i++)
                {
                    int r = i / Cols, col = i % Cols;
                    int inRow = Math.Min(Cols, Count - r * Cols);
                    float rowW = inRow * CardW + (inRow - 1) * Gap;
                    float ox = region.Left + (region.Width - rowW) / 2;
                    cells.Add(new SKPoint((float)Math.Round(ox + col * (CardW + Gap)), (float)Math.Round(oy + r * (CardH + Gap))));
                }
                return cells;
            }
        }

        // How many posters (<= n, <= cap) and which grid fills w x h best: area coverage x
        // count^0.35, so more posters win unless they get much smaller. A partial last row is
        // only allowed when every poster is used.
        private static GridLayout BestGrid(int n, int w, int h, int gap, int cap, int? maxRows)
        {
            GridLayout? best = null;
            double bestScore = -1;
            for (int k = 1; k <= Math.Min(n, cap); k++)
                for (int cols = 1; cols <= k; cols++)
                {
                    int rows = (int)Math.Ceiling(k / (double)cols);
                    if (maxRows.HasValue && rows > maxRows.Value) continue;
                    if (rows * cols != k && k != n) continue;
                    if (rows > 1 && cols * rows - k >= cols) continue;
                    double cw = Math.Min((w - gap * (cols - 1)) / (double)cols, (h - gap * (rows - 1)) / (double)rows / PosterRatio);
                    if (cw <= 8) continue;
                    double ch = cw * PosterRatio;
                    double score = (k * cw * ch) / ((double)w * h) * Math.Pow(k, 0.35);
                    if (score > bestScore) { bestScore = score; best = new GridLayout(k, cols, rows, (int)cw, (int)ch, gap); }
                }
            return best ?? new GridLayout(1, 1, 1, Math.Max(1, w), Math.Max(1, (int)(w * PosterRatio)), gap);
        }
    }
}
