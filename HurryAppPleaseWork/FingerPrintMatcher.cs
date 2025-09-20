using OpenCvSharp;
using SourceAFIS;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace HurryAppPleaseWork
{
    public class FingerPrintMatcher
    {
        const int WIDTH = 256;
        const int HEIGHT = 288;
        public static byte[] MatToBytes(Mat mat, string ext = ".bmp")
        {
            Cv2.ImEncode(ext, mat, out var buf);
            return buf.ToArray();
        }

        public static Mat MatFromBytes(byte[] data, ImreadModes mode = ImreadModes.Unchanged)
        {
            return Cv2.ImDecode(data, mode);
        }

        public static (Mat mat, List<(Rect rect, FingerprintTemplate template)> templates) GenerateRectsAndTemplate(string path)
        {
            int roiSize = 100;
            int maxRois = 128;
            var candGray = LoadAndClahe(path);
            var timeStamp = Stopwatch.GetTimestamp();

            var candRects = SelectRoisByKeypoints(candGray, roiSize, maxRois);

            return (candGray, BuildTemplates(candGray, candRects, 500));
        }

        public static List<(Rect Rect, FingerprintTemplate Template)> GetRectanglesAndTemplates(Mat mat)
        {
            var probeRects = SelectRoisByKeypoints(mat, roiSize: 100, maxRois: 128);

            return BuildTemplates(mat, probeRects, dpi: 500);
        }

        public static (double bestScore, (Rect probeRect, Rect candRect) bestAnchor, Rect bestProbeOverlap, Rect bestCandOverlap)
        FindBestOverlap(List<(Rect probeRect, Rect candRect, double score)> anchors, Mat probeGray, Mat candGray, int dpi, double minAnchorScore = 0)
            => anchors
            .Where(a => a.score >= minAnchorScore)
            .Select(a =>
            {
                var probeCenter = Center(a.probeRect);
                var candCenter = Center(a.candRect);
                int transX = candCenter.X - probeCenter.X;
                int transY = candCenter.Y - probeCenter.Y;

                if (!ComputeOverlapRects(transX, transY, out Rect probeOverlap, out Rect candOverlap)) return null;

                if (probeOverlap.Width < 16 || probeOverlap.Height < 16) return null;

                using var probePatch = new Mat(probeGray, probeOverlap);
                using var candPatch = new Mat(candGray, candOverlap);

                Cv2.ImEncode(".bmp", probePatch, out byte[] probeBmp);
                Cv2.ImEncode(".bmp", candPatch, out byte[] candBmp);

                var probeTpl = new FingerprintTemplate(
                    new FingerprintImage(probeBmp, new FingerprintImageOptions { Dpi = dpi }));

                var candTpl = new FingerprintTemplate(
                    new FingerprintImage(candBmp, new FingerprintImageOptions { Dpi = dpi }));

                double score = new FingerprintMatcher(probeTpl).Match(candTpl);

                return new
                {
                    Score = score,
                    Anchor = (a.probeRect, a.candRect),
                    ProbeOverlap = probeOverlap,
                    CandOverlap = candOverlap
                };
            })
            .Where(r => r != null)
            .OrderByDescending(r => r!.Score)
            .Select(x => (x.Score, x.Anchor, x.ProbeOverlap, x.CandOverlap))
            .FirstOrDefault();

        public void Test()
        {
            string probePath = "zeena5.bmp";
            string candidatePath = "zeena7.bmp";

            // Parameters
            int dpi = 500;
            int roiSize = 100;
            int maxRois = 128;    // how many ROIs to consider per image for anchor search
            int topKAnchors = 3;  // try top-K anchors (recommended)
            int minAnchorScore = 8; // min score to accept anchor ROI
            var timeStamp = Stopwatch.GetTimestamp();
            // Run pipeline
            using var probeGray = LoadAndClahe(probePath);
            using var candGray = LoadAndClahe(candidatePath);
            Console.WriteLine(Stopwatch.GetElapsedTime(timeStamp) + "#1");

            // Build ROI templates (keypoint-driven + fallback grid)
            var probeRects = SelectRoisByKeypoints(probeGray, roiSize, maxRois);
            var candRects = SelectRoisByKeypoints(candGray, roiSize, maxRois);
            Console.WriteLine(Stopwatch.GetElapsedTime(timeStamp) + "#2");

            Console.WriteLine(probeRects.Count);
            Console.WriteLine(candRects.Count);

            var probeTemplates = BuildTemplates(probeGray, probeRects, dpi);
            Console.WriteLine(probeTemplates.Sum(x => x.template.Memory()));
            var candTemplates = BuildTemplates(candGray, candRects, dpi);

            Console.WriteLine(Stopwatch.GetElapsedTime(timeStamp) + "#3");
            // Find top-K anchor ROI pairs
            var anchors = FindTopAnchors(probeTemplates, candTemplates, topKAnchors);

            if (anchors.Count == 0)
            {
                Console.WriteLine("No anchor matches found.");
                return;
            }

            // Try each anchor: compute translation, shift probe, compute overlap match score.
            double bestOverlapScore = double.NegativeInfinity;
            (Rect probeAnchor, Rect candAnchor) bestAnchor = (default, default);
            Rect bestProbeOverlap = default, bestCandOverlap = default;

            foreach (var a in anchors)
            {
                Console.WriteLine(a.candRect.Location);
                if (a.score < minAnchorScore) continue;

                // compute translation using centers
                var probeCenter = Center(a.probeRect);
                var candCenter = Center(a.candRect);
                int transX = candCenter.X - probeCenter.X;
                int transY = candCenter.Y - probeCenter.Y;

                // shift probe by (transX, transY) and compute overlap rects
                if (!ComputeOverlapRects(transX, transY, out Rect probeOverlap, out Rect candOverlap))
                    continue;

                // if overlap is small skip
                if (probeOverlap.Width < 16 || probeOverlap.Height < 16) continue;

                // Extract Mats for overlap
                using var probePatch = new Mat(probeGray, probeOverlap);
                using var candPatch = new Mat(candGray, candOverlap);

                // Encode to BMP and create SourceAFIS templates for the patches
                Cv2.ImEncode(".bmp", probePatch, out byte[] probeBmp);
                Cv2.ImEncode(".bmp", candPatch, out byte[] candBmp);

                var probeImg = new FingerprintImage(probeBmp, new FingerprintImageOptions { Dpi = dpi });
                var candImg = new FingerprintImage(candBmp, new FingerprintImageOptions { Dpi = dpi });

                var probeTpl = new FingerprintTemplate(probeImg);
                var candTpl = new FingerprintTemplate(candImg);

                var matcher = new FingerprintMatcher(probeTpl);
                double overlapScore = matcher.Match(candTpl);

                Console.WriteLine($"Anchor score={a.score:F2} trans=({transX},{transY}) overlap={probeOverlap} -> score={overlapScore:F2}");

                if (overlapScore > bestOverlapScore)
                {
                    bestOverlapScore = overlapScore;
                    bestAnchor = (a.probeRect, a.candRect);
                    bestProbeOverlap = probeOverlap;
                    bestCandOverlap = candOverlap;
                }
            }

            if (bestOverlapScore == double.NegativeInfinity)
            {
                Console.WriteLine("No valid overlaps found from anchors.");
                return;
            }

            Console.WriteLine($"Best overlap score = {bestOverlapScore:F2} for anchor probe={bestAnchor.probeAnchor.Location} cand={bestAnchor.candAnchor.Location}");
            Console.WriteLine(Stopwatch.GetElapsedTime(timeStamp));
            // Visualize anchor ROIs and overlap regions
            VisualizeRect(probeGray, bestAnchor.probeAnchor, "best_probe_anchor.png", new OpenCvSharp.Scalar(0, 255, 0));
            VisualizeRect(candGray, bestAnchor.candAnchor, "best_cand_anchor.png", new OpenCvSharp.Scalar(0, 255, 0));

            VisualizeRect(probeGray, bestProbeOverlap, "best_probe_overlap.png", new OpenCvSharp.Scalar(255, 0, 0));
            VisualizeRect(candGray, bestCandOverlap, "best_cand_overlap.png", new OpenCvSharp.Scalar(255, 0, 0));

            Console.WriteLine("Saved best_anchor and overlap visualizations.");

        }
        // ---------- Helpers ----------
        public static Mat Clahe(in Mat src)
        {
            using var gray = new Mat();
            Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);

            using var clahe = Cv2.CreateCLAHE(clipLimit: 30, tileGridSize: new Size(16, 16));
            var dst = new Mat();
            clahe.Apply(gray, dst);

            if (dst.Width != WIDTH || dst.Height != HEIGHT)
            {
                Cv2.Resize(dst, dst, new Size(WIDTH, HEIGHT));
            }

            return dst;
        }

        static Mat LoadAndClahe(string path)
        {
            var src = Cv2.ImRead(path, ImreadModes.Color);
            Mat gray = new();
            Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);
            using var clahe = Cv2.CreateCLAHE(clipLimit: 30, tileGridSize: new Size(16, 16));
            Mat dst = new();
            clahe.Apply(gray, dst);
            if (dst.Width != WIDTH || dst.Height != HEIGHT)
                Cv2.Resize(dst, dst, new Size(WIDTH, HEIGHT));
            src.Dispose();
            gray.Dispose();
            return dst;
        }

        static List<Rect> SelectRoisByKeypoints(Mat gray, int roiSize, int maxRois)
        {
            var rects = new List<Rect>(maxRois);

            // Simple ORB detector
            using var orb = ORB.Create(nFeatures: 1000, fastThreshold: 5);

            var kps = orb.Detect(gray);

            if (kps == null || kps.Length == 0)
                return SelectRoisByGrid(gray, roiSize, maxRois, stride: Math.Max(roiSize / 2, 20));

            int half = roiSize / 2;
            int imgW = gray.Width;
            int imgH = gray.Height;

            // Accept keypoints by response; skip those whose centered ROI would go out of bounds
            foreach (var kp in kps.OrderByDescending(k => k.Response))
            {
                if (rects.Count >= maxRois) break;

                int cx = (int)Math.Round(kp.Pt.X);
                int cy = (int)Math.Round(kp.Pt.Y);

                // Without clamping: skip if ROI would be out of image bounds
                if (cx - half < 0 || cy - half < 0 || cx + half > imgW || cy + half > imgH) continue;

                rects.Add(new Rect(cx - half, cy - half, roiSize, roiSize));
            }

            return rects;
        }

        static List<Rect> SelectRoisByGrid(Mat gray, int roiSize, int maxRois, int stride)
        {
            var rects = new List<Rect>();
            for (int y = 0; y <= gray.Height - roiSize && rects.Count < maxRois; y += stride)
            {
                for (int x = 0; x <= gray.Width - roiSize && rects.Count < maxRois; x += stride)
                {
                    rects.Add(new Rect(x, y, roiSize, roiSize));
                }
            }
            return rects;
        }
        static List<(Rect rect, FingerprintTemplate template)> BuildTemplates(Mat grayImage, List<Rect> rects, int dpi)
        {
            var results = new ConcurrentBag<(Rect, FingerprintTemplate)>();

            Parallel.ForEach(rects, r =>
            {
                try
                {
                    using var roi = new Mat(grayImage, r);
                    Cv2.ImEncode(".bmp", roi, out byte[] bmp);
                    var fi = new FingerprintImage(bmp, new FingerprintImageOptions { Dpi = dpi });
                    var tpl = new FingerprintTemplate(fi);
                    results.Add((r, tpl));
                }
                catch
                {
                    // skip failures
                }
            });

            return results.ToList();
        }

        // Find top-K anchor ROI pairs by matching all probe×candidate ROI templates
        public static List<(Rect probeRect, Rect candRect, double score)> FindTopAnchors(
            List<(Rect rect, FingerprintTemplate template)> probes,
            List<(Rect rect, FingerprintTemplate template)> cands,
            int topK = 1)
        {
            var matches = new ConcurrentBag<(Rect, Rect, double)>();

            Parallel.ForEach(probes, probe =>
            {
                var matcher = new FingerprintMatcher(probe.template);
                foreach (var cand in cands)
                {
                    try
                    {
                        double score = matcher.Match(cand.template);
                        matches.Add((probe.rect, cand.rect, score));
                    }
                    catch { }
                }
            });

            return matches.ToArray()
                .OrderByDescending(m => m.Item3)
                .Take(topK)
                .Select(m => (m.Item1, m.Item2, m.Item3))
                .ToList();
        }

        // Compute overlapping rectangles for shifted probe image. Returns false if no overlap.
        static bool ComputeOverlapRects(int transX, int transY, out Rect probeOverlap, out Rect candOverlap)
        {
            int px0 = Math.Max(0, -transX);
            int py0 = Math.Max(0, -transY);

            int px1 = Math.Min(WIDTH, WIDTH - Math.Max(0, transX));    // exclusive bound
            int py1 = Math.Min(HEIGHT, HEIGHT - Math.Max(0, transY));  // exclusive bound

            int ow = px1 - px0;
            int oh = py1 - py0;

            if (ow <= 0 || oh <= 0)
            {
                probeOverlap = default;
                candOverlap = default;
                return false;
            }

            probeOverlap = new Rect(px0, py0, ow, oh);
            candOverlap = new Rect(px0 + transX, py0 + transY, ow, oh);
            return true;
        }

        static Point Center(Rect r) => new Point(r.X + r.Width / 2, r.Y + r.Height / 2);

        static void VisualizeRect(Mat gray, Rect r, string outPath, OpenCvSharp.Scalar color)
        {
            using var colorMat = new Mat();
            Cv2.CvtColor(gray, colorMat, ColorConversionCodes.GRAY2BGR);
            Cv2.Rectangle(colorMat, r, color, 2);
            Cv2.ImWrite(outPath, colorMat);
        }
    }
}