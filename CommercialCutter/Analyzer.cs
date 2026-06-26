using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace CommercialCutter;

public static class Analyzer
{
    // How many consecutive samples must agree before we flip program<->commercial state.
    // At a 1s sampling interval this means a ~5s debounce, which absorbs the bug
    // flickering briefly during transitions/bugs without flipping state.
    private const int DebounceSamples = 5;

    // Scores every thumbnail's similarity to the reference logo crop. Classification
    // (what threshold counts as "logo present") happens separately in BuildSegments,
    // so the same scores can be re-classified at different thresholds without re-reading images.
    //
    // Comparing the whole crop box would let background content swamp the signal: a title
    // sequence or scene with a much darker/brighter backdrop behind the bug can look
    // "different" from the reference even though the logo itself is unchanged and still
    // present, producing false "commercial" classifications. Instead we build a mask of
    // just the logo's pixels (the ones that stand out from the reference crop's background)
    // and compare only those, so background variation elsewhere in the box doesn't matter.
    public static List<SampleScore> ScoreThumbnails(string thumbsDir, string referenceImagePath, double intervalSeconds)
    {
        using var reference = Image.Load<Rgba32>(referenceImagePath);
        reference.Mutate(x => x.Resize(64, 64));

        var mask = BuildLogoMask(reference);

        var files = Directory.GetFiles(thumbsDir, "frame_*.jpg")
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        var scores = new List<SampleScore>(files.Count);
        for (int i = 0; i < files.Count; i++)
        {
            using var frame = Image.Load<Rgba32>(files[i]);
            frame.Mutate(x => x.Resize(64, 64));

            var similarity = PixelSimilarity(reference, frame, mask);
            scores.Add(new SampleScore(i * intervalSeconds, similarity));
        }

        return scores;
    }

    // Marks pixels whose luma differs enough from the reference crop's background to likely
    // be part of the logo glyph itself, rather than the backdrop behind it. Falls back to
    // using every pixel if that yields too few (e.g. a logo with very low contrast).
    private static bool[] BuildLogoMask(Image<Rgba32> reference)
    {
        int w = reference.Width, h = reference.Height, n = w * h;
        var luma = new double[n];

        reference.ProcessPixelRows(rows =>
        {
            for (int y = 0; y < rows.Height; y++)
            {
                var span = rows.GetRowSpan(y);
                for (int x = 0; x < span.Length; x++)
                {
                    var p = span[x];
                    luma[y * w + x] = 0.299 * p.R + 0.587 * p.G + 0.114 * p.B;
                }
            }
        });

        var meanLuma = luma.Average();
        var mask = new bool[n];
        var minMaskFraction = 0.05;

        for (var contrastThreshold = 30.0; contrastThreshold >= 5.0; contrastThreshold -= 5.0)
        {
            int count = 0;
            for (int i = 0; i < n; i++)
            {
                mask[i] = Math.Abs(luma[i] - meanLuma) > contrastThreshold;
                if (mask[i]) count++;
            }
            if (count >= n * minMaskFraction) return mask;
        }

        Array.Fill(mask, true); // low-contrast logo — fall back to comparing the whole crop
        return mask;
    }

    // Mean-pixel-distance based similarity over the masked pixels only, normalized to [0,1]
    // (1 = identical).
    private static double PixelSimilarity(Image<Rgba32> a, Image<Rgba32> b, bool[] mask)
    {
        double sumSqDiff = 0;
        int maskedCount = 0;
        int w = a.Width;

        a.ProcessPixelRows(b, (rowsA, rowsB) =>
        {
            for (int y = 0; y < rowsA.Height; y++)
            {
                var spanA = rowsA.GetRowSpan(y);
                var spanB = rowsB.GetRowSpan(y);
                for (int x = 0; x < spanA.Length; x++)
                {
                    if (!mask[y * w + x]) continue;
                    var pa = spanA[x];
                    var pb = spanB[x];
                    double dr = pa.R - pb.R, dg = pa.G - pb.G, db = pa.B - pb.B;
                    sumSqDiff += dr * dr + dg * dg + db * db;
                    maskedCount++;
                }
            }
        });

        if (maskedCount == 0) return 1.0;
        var maxSqDiff = maskedCount * 3 * 255.0 * 255.0;
        return 1.0 - Math.Sqrt(sumSqDiff / maxSqDiff);
    }

    // Classifies each sample against a single global `threshold`, debounces the resulting
    // presence signal, folds it into commercial/program segments, then snaps each commercial
    // break's length to the nearest ad-unit multiple (see SnapCommercialBreaksToAdUnits).
    //
    // A global threshold assumes the "logo present" baseline similarity is constant across the
    // whole episode. In practice it drifts (different scenes/lighting partially defeat the logo
    // mask even with the bug on screen), so a break whose dip isn't deep relative to the global
    // cutoff can go undetected even though it's clearly anomalous relative to its surroundings.
    // BuildSegmentsAdaptive below addresses that; this absolute version is kept for manual
    // threshold entry, where "an absolute number I can read off the chart" is more useful.
    public static List<Segment> BuildSegments(
        List<SampleScore> scores, double intervalSeconds, double threshold,
        double adUnitSeconds = 15.0, double minBreakSeconds = 59.0)
    {
        if (scores.Count == 0) return new List<Segment>();

        var present = scores.Select(s => s.Similarity >= threshold).ToArray();
        return ClassifyAndSnap(scores, intervalSeconds, present, adUnitSeconds, minBreakSeconds);
    }

    // Like BuildSegments, but classifies each sample relative to a rolling local baseline
    // instead of one fixed number for the whole episode. A sample counts as "logo present"
    // unless it falls more than `dropThreshold` below its local baseline. This catches breaks
    // whose dip is shallow in absolute terms but still a clear local anomaly, and is less
    // thrown off by scenes that happen to run lower similarity throughout (those just pull the
    // local baseline down with them) — PROVIDED the window is wide enough relative to the
    // longest break (see ComputeLocalBaseline).
    public static List<Segment> BuildSegmentsAdaptive(
        List<SampleScore> scores, double intervalSeconds, double dropThreshold,
        double windowRadiusSeconds = 300.0, double adUnitSeconds = 15.0, double minBreakSeconds = 59.0)
    {
        if (scores.Count == 0) return new List<Segment>();

        var baseline = ComputeLocalBaseline(scores, intervalSeconds, windowRadiusSeconds);
        return BuildSegmentsAdaptiveFromBaseline(scores, intervalSeconds, dropThreshold, baseline, adUnitSeconds, minBreakSeconds);
    }

    // Same as BuildSegmentsAdaptive, but takes an already-computed baseline. The baseline
    // doesn't depend on dropThreshold at all — only on the scores and window radius — so a
    // search that re-evaluates many dropThreshold candidates (see FindAdaptiveThresholdForTarget)
    // should compute it once and reuse it, rather than redoing the O(n × window) rolling
    // percentile pass on every candidate.
    public static List<Segment> BuildSegmentsAdaptiveFromBaseline(
        List<SampleScore> scores, double intervalSeconds, double dropThreshold, double[] baseline,
        double adUnitSeconds = 15.0, double minBreakSeconds = 59.0)
    {
        if (scores.Count == 0) return new List<Segment>();

        var present = new bool[scores.Count];
        for (int i = 0; i < scores.Count; i++)
            present[i] = scores[i].Similarity >= baseline[i] - dropThreshold;

        return ClassifyAndSnap(scores, intervalSeconds, present, adUnitSeconds, minBreakSeconds);
    }

    // Rolling local baseline of similarity, used as the "expected if logo is present" value for
    // adaptive classification. Two things make this robust against a break dragging its own
    // baseline down with it:
    //  - It uses the 65th percentile rather than the median, so the baseline tracks the
    //    upper/"good" part of the window even if a sizeable minority of it is inside a break.
    //  - The window needs to be wider than the longest break you expect, full stop — a window
    //    only slightly bigger than a break means that once you're more than half the window
    //    radius into the break, the window is sitting entirely inside it and the percentile
    //    just describes the ad, not the program. The default (300s radius => 600s window) comfortably
    //    covers ad breaks up into the 3-4 minute range; widen it further for unusually long breaks.
    public static double[] ComputeLocalBaseline(
        List<SampleScore> scores, double intervalSeconds, double windowRadiusSeconds, double percentile = 0.65)
    {
        int radiusSamples = Math.Max(1, (int)Math.Round(windowRadiusSeconds / intervalSeconds));
        var baseline = new double[scores.Count];

        for (int i = 0; i < scores.Count; i++)
        {
            int lo = Math.Max(0, i - radiusSamples);
            int hi = Math.Min(scores.Count - 1, i + radiusSamples);

            var window = new List<double>(hi - lo + 1);
            for (int j = lo; j <= hi; j++) window.Add(scores[j].Similarity);
            window.Sort();

            int idx = Math.Clamp((int)(window.Count * percentile), 0, window.Count - 1);
            baseline[i] = window[idx];
        }

        return baseline;
    }

    // Shared debounce + segment-build + ad-unit-snap pipeline used by both the absolute and
    // adaptive classifiers — they only differ in how `present` gets computed.
    private static List<Segment> ClassifyAndSnap(
        List<SampleScore> scores, double intervalSeconds, bool[] present,
        double adUnitSeconds, double minBreakSeconds)
    {
        var smoothed = new bool[scores.Count];

        // Simple majority-vote debounce: state at sample i is the majority logo-presence
        // value within a window of DebounceSamples centered on i.
        int half = DebounceSamples / 2;
        for (int i = 0; i < scores.Count; i++)
        {
            int lo = Math.Max(0, i - half);
            int hi = Math.Min(scores.Count - 1, i + half);
            int presentCount = 0;
            for (int j = lo; j <= hi; j++)
                if (present[j]) presentCount++;
            smoothed[i] = presentCount * 2 >= (hi - lo + 1);
        }

        var segments = new List<Segment>();
        bool curIsCommercial = !smoothed[0];
        double segStart = scores[0].TimeSeconds;

        for (int i = 1; i < scores.Count; i++)
        {
            bool isCommercial = !smoothed[i];
            if (isCommercial != curIsCommercial)
            {
                segments.Add(new Segment(segStart, scores[i].TimeSeconds, curIsCommercial));
                segStart = scores[i].TimeSeconds;
                curIsCommercial = isCommercial;
            }
        }

        var lastTime = scores[^1].TimeSeconds + intervalSeconds;
        segments.Add(new Segment(segStart, lastTime, curIsCommercial));

        return SnapCommercialBreaksToAdUnits(segments, adUnitSeconds, minBreakSeconds);
    }

    // Commercial breaks are built from individual 15s/30s ad slots, so a real break's total
    // length is almost always a multiple of 15s. The raw boundary-detection above is noisy at
    // the edges (debounce, compression artifacts, etc.), so round each detected break's length
    // to the nearest 15s multiple — anchored on its start, since that edge tends to be a hard
    // cut from program to ad. Breaks that round down below the minimum are treated as false
    // positives and dropped (merged back into the surrounding program).
    //
    // minBreakSeconds (default 59s) assumes a break bundles 2+ back-to-back spots, which holds
    // for breaks in the middle of the recording but not for a single promo slot at the very
    // start or end — a 15s leading/trailing promo is completely ordinary and shouldn't be
    // discarded as noise just because it's shorter than a typical mid-show break. So the first
    // and last segment only need to clear one ad unit, not the full minimum.
    public static List<Segment> SnapCommercialBreaksToAdUnits(
        List<Segment> segments, double adUnitSeconds, double minBreakSeconds)
    {
        if (segments.Count == 0) return segments;

        var snapped = new List<Segment>(segments.Count);
        double cursor = segments[0].StartSeconds;

        for (int i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];
            if (seg.IsCommercial)
            {
                var isEdgeSegment = i == 0 || i == segments.Count - 1;
                var effectiveMin = isEdgeSegment ? adUnitSeconds : minBreakSeconds;

                var units = Math.Round(seg.DurationSeconds / adUnitSeconds);
                var snappedDuration = units * adUnitSeconds;
                if (snappedDuration < effectiveMin) snappedDuration = 0;

                if (snappedDuration > 0)
                    snapped.Add(new Segment(cursor, cursor + snappedDuration, true));
                cursor += snappedDuration;
            }
            else
            {
                var end = Math.Max(cursor + 0.01, seg.EndSeconds);
                snapped.Add(new Segment(cursor, end, false));
                cursor = end;
            }
        }

        // Dropping a tiny commercial break leaves two adjacent program segments — merge them.
        var merged = new List<Segment>();
        foreach (var seg in snapped)
        {
            if (merged.Count > 0 && merged[^1].IsCommercial == seg.IsCommercial)
                merged[^1] = new Segment(merged[^1].StartSeconds, seg.EndSeconds, seg.IsCommercial);
            else
                merged.Add(seg);
        }

        return merged;
    }

    // The logo-presence signal alone can't tell where a fade-to-black bumper ends and the ad
    // content begins — it just knows roughly when the bug disappeared/reappeared, which can be
    // off by a lot more than a typical fade (e.g. an opening-titles sequence that suppresses the
    // bug entirely makes the raw boundary land wherever the deepest dip happens to be, which can
    // be tens of seconds from the true cut, and can contain several incidental black flashes —
    // e.g. between title cards — before the real cut to commercial). `maxNudgeSeconds` is also
    // the corroboration tolerance in ValidateBreaksAgainstBumpers, where it needs to stay tight
    // to avoid treating unrelated dips as false confirmation. But once a break has already been
    // validated as real, snapping its boundary to the nearest black/silent dip is much
    // lower-risk — we're not deciding whether it's a break anymore, just refining where it
    // actually starts/ends — so this searches considerably wider than the corroboration
    // tolerance, via FindChainedDip (see there for how it avoids grabbing a break's *other*
    // bumper instead of a decoy).
    //  - going INTO a commercial: the boundary moves to the END of the black/silent dip, so the
    //    fade-to-black and the black hold stay part of the kept program.
    //  - coming OUT of a commercial: the boundary moves to the START of the black/silent dip, so
    //    the black hold and fade-up-from-black stay part of the kept program.
    // Boundaries with no black/silent interval nearby (e.g. a hard cut with no bumper) are left
    // exactly where the logo analysis put them.
    public static List<Segment> RefineBoundariesWithBlackAndSilence(
        List<Segment> segments,
        List<(double Start, double End)> blackIntervals,
        List<(double Start, double End)> silenceIntervals,
        double maxNudgeSeconds = 6.0)
    {
        if (segments.Count < 2) return segments;

        var result = new List<Segment>(segments);
        var boundarySearchSeconds = Math.Max(maxNudgeSeconds, 90.0);

        for (int i = 0; i < result.Count - 1; i++)
        {
            var a = result[i];
            var b = result[i + 1];
            var boundary = a.EndSeconds;

            var intoCommercial = !a.IsCommercial && b.IsCommercial;
            var outOfCommercial = a.IsCommercial && !b.IsCommercial;
            if (!intoCommercial && !outOfCommercial) continue;

            var chosen = FindChainedDip(blackIntervals, boundary, boundarySearchSeconds, intoCommercial, a.StartSeconds, b.EndSeconds)
                         ?? FindChainedDip(silenceIntervals, boundary, boundarySearchSeconds / 2.0, intoCommercial, a.StartSeconds, b.EndSeconds);
            if (chosen is null) continue;

            var newBoundary = intoCommercial ? chosen.Value.End : chosen.Value.Start;
            newBoundary = Math.Clamp(newBoundary, a.StartSeconds + 0.1, b.EndSeconds - 0.1);

            result[i] = new Segment(a.StartSeconds, newBoundary, a.IsCommercial);
            result[i + 1] = new Segment(newBoundary, b.EndSeconds, b.IsCommercial);
        }

        return result;
    }

    private static (double Start, double End)? FindNearestInterval(
        List<(double Start, double End)> intervals, double time, double maxDistance)
    {
        (double Start, double End)? best = null;
        var bestDistance = double.MaxValue;

        foreach (var interval in intervals)
        {
            var distance = time >= interval.Start && time <= interval.End
                ? 0.0
                : Math.Min(Math.Abs(time - interval.Start), Math.Abs(time - interval.End));

            if (distance <= maxDistance && distance < bestDistance)
            {
                bestDistance = distance;
                best = interval;
            }
        }

        return best;
    }

    // Starts from the dip nearest `time` (within maxDistance, bounded to [allowedStart,
    // allowedEnd] so it can't reach into an unrelated adjacent segment), then walks through
    // consecutive dips in the preferred direction (latest for entering a break, earliest for
    // leaving one) as long as each next dip starts within ChainGapToleranceSeconds of the
    // current one's end. This is how a cluster of incidental flashes (e.g. between title cards)
    // gets walked all the way to the one that's actually the real bumper, without that same
    // "keep extending" logic ever reaching all the way to a break's *other*, isolated bumper —
    // the gap to that one is far larger than the gap between consecutive flashes in a cluster,
    // so the walk stops well before it.
    private const double ChainGapToleranceSeconds = 20.0;

    private static (double Start, double End)? FindChainedDip(
        List<(double Start, double End)> intervals, double time, double maxDistance, bool preferLatest,
        double allowedStart, double allowedEnd)
    {
        var candidates = intervals
            .Where(iv => iv.Start >= allowedStart && iv.Start <= allowedEnd)
            .OrderBy(iv => iv.Start)
            .ToList();
        if (candidates.Count == 0) return null;

        var seedIndex = -1;
        var seedDistance = double.MaxValue;
        for (int idx = 0; idx < candidates.Count; idx++)
        {
            var iv = candidates[idx];
            var distance = time >= iv.Start && time <= iv.End
                ? 0.0
                : Math.Min(Math.Abs(time - iv.Start), Math.Abs(time - iv.End));
            if (distance <= maxDistance && distance < seedDistance)
            {
                seedDistance = distance;
                seedIndex = idx;
            }
        }
        if (seedIndex < 0) return null;

        var currentIndex = seedIndex;
        if (preferLatest)
        {
            while (currentIndex + 1 < candidates.Count &&
                   candidates[currentIndex + 1].Start - candidates[currentIndex].End <= ChainGapToleranceSeconds)
                currentIndex++;
        }
        else
        {
            while (currentIndex - 1 >= 0 &&
                   candidates[currentIndex].Start - candidates[currentIndex - 1].End <= ChainGapToleranceSeconds)
                currentIndex--;
        }

        return candidates[currentIndex];
    }

    // A network's own promos for its other shows almost always keep that network's bug visible
    // throughout — they want their branding up while they're advertising their own lineup — so
    // logo-absence detection is structurally blind to a leading/trailing promo, unlike a paid
    // third-party commercial where the bug genuinely disappears. This is a separate, opt-in
    // pass that looks for a different signal entirely: even a self-promo almost always cuts to
    // black/silence at its own boundary. If the recording's very first or last segment is
    // currently classified as program, this looks for a black/silent dip within the first/last
    // `maxPromoSeconds` of the recording and trims up to it, regardless of what the logo
    // similarity said there.
    public static List<Segment> TrimLeadingTrailingPromo(
        List<Segment> segments,
        List<(double Start, double End)> blackIntervals,
        List<(double Start, double End)> silenceIntervals,
        double maxPromoSeconds,
        double recordingDurationSeconds)
    {
        if (segments.Count == 0 || maxPromoSeconds <= 0) return segments;

        var result = new List<Segment>(segments);

        var first = result[0];
        // A raw, too-short commercial blip right at t=0 (e.g. a single misclassified second from
        // the logo pass) shouldn't block this — fold it into whatever program segment follows
        // and treat the combined span as the "first" block to search within, the same as if it
        // had been classified as program to begin with.
        if (first.IsCommercial && first.DurationSeconds < maxPromoSeconds && result.Count > 1 && !result[1].IsCommercial)
        {
            first = new Segment(first.StartSeconds, result[1].EndSeconds, false);
            result[0] = first;
            result.RemoveAt(1);
        }
        if (!first.IsCommercial)
        {
            // Same "prefer latest within a connected chain" logic as entering a normal break:
            // a promo can itself contain incidental flashes, and we want the last one before
            // sustained real content starts, not just the first thing near t=0.
            var dip = FindChainedDip(blackIntervals, 0, maxPromoSeconds, preferLatest: true, first.StartSeconds, first.EndSeconds)
                      ?? FindChainedDip(silenceIntervals, 0, maxPromoSeconds / 2.0, preferLatest: true, first.StartSeconds, first.EndSeconds);
            if (dip is not null && dip.Value.End > first.StartSeconds + 0.5 && dip.Value.End < first.EndSeconds)
            {
                result[0] = new Segment(first.StartSeconds, dip.Value.End, true);
                result.Insert(1, new Segment(dip.Value.End, first.EndSeconds, false));
            }
        }

        var last = result[^1];
        if (!last.IsCommercial)
        {
            var dip = FindChainedDip(blackIntervals, recordingDurationSeconds, maxPromoSeconds, preferLatest: false, last.StartSeconds, last.EndSeconds)
                      ?? FindChainedDip(silenceIntervals, recordingDurationSeconds, maxPromoSeconds / 2.0, preferLatest: false, last.StartSeconds, last.EndSeconds);
            if (dip is not null && dip.Value.Start < last.EndSeconds - 0.5 && dip.Value.Start > last.StartSeconds)
            {
                var idx = result.Count - 1;
                result[idx] = new Segment(last.StartSeconds, dip.Value.Start, false);
                result.Add(new Segment(dip.Value.Start, last.EndSeconds, true));
            }
        }

        return result;
    }

    // The logo signal alone can't distinguish "a real ad break" from "a stretch where the bug
    // happens to be hidden for some other reason" — most commonly the opening titles, where
    // networks often suppress the bug entirely, or a dark scene that defeats the logo mask.
    // A real commercial break is always bracketed by a cut-to-black/silence bumper on at least
    // one side (often both); the show's own content almost never is. So: any "commercial"
    // segment with no black/silence dip near either of its edges gets demoted back to program
    // before boundary nudging runs. This is what actually fixes false positives like a title
    // sequence getting clipped — raising the minimum break length alone can't, since the false
    // positive can legitimately run as long as the titles do.
    //
    // This tolerance is deliberately its own constant, not tied to the GUI's "Max nudge"
    // field: that field controls how far the *boundary-nudge* step (below) is allowed to search
    // once a break is already trusted, which is a much lower-risk operation than deciding
    // whether to trust it in the first place. A too-tight corroboration tolerance rejects real
    // breaks whose raw (pre-nudge) boundary lands a bit far from any bumper; a too-loose one
    // (e.g. reusing the nudge step's ~90s reach) lets almost any dip find *some* black/silent
    // event purely by chance in a long recording, defeating corroboration's whole purpose.
    public static List<Segment> ValidateBreaksAgainstBumpers(
        List<Segment> segments,
        List<(double Start, double End)> blackIntervals,
        List<(double Start, double End)> silenceIntervals,
        double maxBumperDistanceSeconds = 20.0)
    {
        if (segments.Count == 0) return segments;

        // Silence is weaker evidence than black — a quiet line reading can trigger it just as
        // easily as a real dead-air bumper, whereas a genuine cut-to-black almost never happens
        // mid-scene. A single coincidental silence event on just one edge isn't enough (that's
        // exactly how a sustained-but-shallow dip in show content — e.g. a darker scene that
        // partially defeats the logo mask without it being an ad — can get incorrectly
        // corroborated and validated as a break). So: one black hit on *either* edge is enough
        // (strong evidence), but silence alone only counts if it corroborates *both* edges.
        var silenceToleranceSeconds = maxBumperDistanceSeconds / 2.0;

        var validated = new List<Segment>(segments.Count);
        foreach (var seg in segments)
        {
            if (seg.IsCommercial)
            {
                var hasBlackStart = FindNearestInterval(blackIntervals, seg.StartSeconds, maxBumperDistanceSeconds) is not null;
                var hasBlackEnd = FindNearestInterval(blackIntervals, seg.EndSeconds, maxBumperDistanceSeconds) is not null;
                var hasSilenceStart = FindNearestInterval(silenceIntervals, seg.StartSeconds, silenceToleranceSeconds) is not null;
                var hasSilenceEnd = FindNearestInterval(silenceIntervals, seg.EndSeconds, silenceToleranceSeconds) is not null;

                var corroborated = hasBlackStart || hasBlackEnd || (hasSilenceStart && hasSilenceEnd);
                validated.Add(new Segment(seg.StartSeconds, seg.EndSeconds, corroborated));
            }
            else
            {
                validated.Add(seg);
            }
        }

        // Demoting an uncorroborated break leaves adjacent program segments — merge them.
        var merged = new List<Segment>();
        foreach (var seg in validated)
        {
            if (merged.Count > 0 && merged[^1].IsCommercial == seg.IsCommercial)
                merged[^1] = new Segment(merged[^1].StartSeconds, seg.EndSeconds, seg.IsCommercial);
            else
                merged.Add(seg);
        }

        return merged;
    }

    // Some networks keep their bug visible through most or all of a commercial pod (seen e.g.
    // on H&I) — logo-absence can never detect that no matter how the threshold is tuned, because
    // the premise it relies on (bug disappears during ads) simply doesn't hold for that content.
    // But a real ad pod is still bracketed by cut-to-black bumpers even then. This looks for
    // pairs of black dips whose raw gap falls in a plausible break-length range, entirely
    // independently of the logo signal. Consecutive pairs only (not every combination), since a
    // real pod's own internal transitions between spots would otherwise create a combinatorial
    // explosion of nested candidates.
    //
    // The candidate's edges are the dips' own natural boundaries — entering the break lands on
    // the END of the first dip, leaving it lands on the START of the second — same convention as
    // RefineBoundariesWithBlackAndSilence's nudge, so the fade-to/from-black stays attached to
    // the program side on both ends rather than getting rounded outward to some ad-unit multiple.
    //
    // Both dips in the pair must also be *isolated* — no other black dip within
    // isolationToleranceSeconds on their outward-facing side. Without that, a title sequence's
    // cluster of incidental flickers (or a double-flicker right at a real break's own boundary)
    // produces phantom bridges: pairing one decoy from a cluster with some unrelated dip much
    // further away, landing on a plausible-looking length purely by coincidence. A genuine ad
    // pod's bracketing dips don't have other black activity crowded right next to them on the
    // program side.
    //
    // The floor is deliberately lower than the logo path's minBreakSeconds: a clean, isolated
    // black bracket on both sides is much stronger evidence than a bare similarity dip, so even
    // a single ~30s spot pod is plausible here without the same false-positive risk.
    public static List<(double Start, double End)> FindBlackBridgedBreaks(
        List<(double Start, double End)> blackIntervals,
        double minBreakSeconds = 25.0, double maxBreakSeconds = 250.0, double isolationToleranceSeconds = 20.0)
    {
        // A real bumper sometimes registers as a tight double-flash (two separate blackdetect
        // hits a couple seconds apart) rather than one continuous dip. Left alone, that pair
        // poisons isolation on both sides — the first flash looks like a decoy crowding the real
        // pairing from one side, and the second looks like a decoy crowding it from the other —
        // so the genuine break gets thrown out by the very check meant to protect it. Merging
        // anything within mergeToleranceSeconds into one combined dip before pairing fixes that
        // without weakening the isolation check itself, which still runs on the merged list.
        //
        // This tolerance is deliberately much tighter than FindChainedDip's
        // ChainGapToleranceSeconds: merging at that wider scale collapses an entire busy cluster
        // (e.g. cold-open flickers plus the whole title sequence) down to one anchor whose
        // *other* side then looks spuriously isolated, reintroducing exactly the phantom-bridge
        // problem this function exists to avoid. A break landing right at the tail of a long
        // decoy cluster (e.g. immediately after the titles) is a known remaining gap here — the
        // logo path with a reasonably low manual dip still catches those.
        const double mergeToleranceSeconds = 5.0;
        var sorted = MergeCloseIntervals(blackIntervals, mergeToleranceSeconds);
        var candidates = new List<(double Start, double End)>();

        // A greedy scan, not every consecutive pair: once dip i+1 is accepted as the *exit*
        // bumper of a break, it can't also be reused as the *entry* bumper of the next candidate
        // a couple minutes later — that's exactly how a single bumper at the end of a real break
        // spawns a phantom second "break" stretching across real program to whatever next
        // isolated dip happens to fall in range. Skipping past it after a match means each dip
        // anchors at most one accepted break.
        int i = 0;
        while (i < sorted.Count - 1)
        {
            var isolatedBefore = i == 0 || sorted[i].Start - sorted[i - 1].End > isolationToleranceSeconds;
            var isolatedAfter = i + 1 == sorted.Count - 1 || sorted[i + 2].Start - sorted[i + 1].End > isolationToleranceSeconds;
            if (!isolatedBefore || !isolatedAfter) { i++; continue; }

            var gapStart = sorted[i].End;
            var gapEnd = sorted[i + 1].Start;
            var duration = gapEnd - gapStart;
            if (duration < minBreakSeconds || duration > maxBreakSeconds) { i++; continue; }

            candidates.Add((gapStart, gapEnd));
            i += 2;
        }

        return candidates;
    }

    private static List<(double Start, double End)> MergeCloseIntervals(
        List<(double Start, double End)> intervals, double toleranceSeconds)
    {
        var sorted = intervals.OrderBy(iv => iv.Start).ToList();
        var merged = new List<(double Start, double End)>();

        foreach (var iv in sorted)
        {
            if (merged.Count > 0 && iv.Start - merged[^1].End <= toleranceSeconds)
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, iv.End));
            else
                merged.Add(iv);
        }

        return merged;
    }

    // Folds black-bridged candidates into a logo-detected segment list, but only where they'd
    // fill in a stretch the logo signal called program — if logo-detection already found a
    // commercial there (even with slightly different boundaries), trust that over the bridge
    // candidate rather than risk double-counting or fighting over the exact edges.
    public static List<Segment> MergeBlackBridgedBreaks(List<Segment> segments, List<(double Start, double End)> bridgedBreaks)
    {
        var result = new List<Segment>(segments);

        foreach (var (bridgeStart, bridgeEnd) in bridgedBreaks)
        {
            var hostIndex = result.FindIndex(s => !s.IsCommercial && s.StartSeconds <= bridgeStart && s.EndSeconds >= bridgeEnd);
            if (hostIndex < 0) continue;

            var host = result[hostIndex];
            result.RemoveAt(hostIndex);
            var insertAt = hostIndex;

            if (host.StartSeconds < bridgeStart - 0.01)
                result.Insert(insertAt++, new Segment(host.StartSeconds, bridgeStart, false));
            result.Insert(insertAt++, new Segment(bridgeStart, bridgeEnd, true));
            if (bridgeEnd < host.EndSeconds - 0.01)
                result.Insert(insertAt, new Segment(bridgeEnd, host.EndSeconds, false));
        }

        return result;
    }

    // Scans the similarity threshold over a fine grid so the resulting kept (program) duration
    // lands close to targetProgramSeconds. This used to be a binary search, on the assumption
    // that logo-presence is monotonic in threshold (lower threshold => more "present" => more
    // program kept) — true for the raw classification, but corroboration (which can accept or
    // reject a whole break based on a single black/silence check) makes the *final* programTotal
    // non-monotonic with small threshold changes, so bisection could get misled into a much
    // worse local result and — worse — wasn't even reproducible: the same file could converge
    // differently across runs depending on which side of a corroboration "cliff" the bisection
    // path happened to land on. A full grid scan costs nothing extra (~100ms per candidate per
    // the observed run times) and evaluates the whole range, so it can't be misled that way.
    // If black/silence intervals are supplied (pass null to skip), each candidate classification
    // is also run through ValidateBreaksAgainstBumpers + RefineBoundariesWithBlackAndSilence
    // before measuring its program length, so the search target reflects the same post-processing
    // the final result will get.
    public static (List<Segment> Segments, double Threshold) FindThresholdForTarget(
        List<SampleScore> scores, double intervalSeconds, double targetProgramSeconds,
        double adUnitSeconds = 15.0, double minBreakSeconds = 59.0,
        List<(double Start, double End)>? blackIntervals = null,
        List<(double Start, double End)>? silenceIntervals = null,
        double maxNudgeSeconds = 6.0,
        int gridSteps = 200)
    {
        var evaluated = new List<(double Candidate, double Diff)>(gridSteps + 1);

        // Black-bridged breaks (bug stays visible through the pod) don't depend on the logo
        // candidate at all, so they're found once and folded into every grid point's evaluation.
        // Without this, the search target (programTotal) only reflects what the logo path can
        // ever see — which, for content where bridging finds most of the real ad time, can never
        // get anywhere near the target no matter the threshold, so the search converges on
        // whatever degenerate point happens to minimize an unreachable objective instead of the
        // threshold that's actually right once the real pipeline (which does include bridging)
        // runs.
        var bridgedBreaks = blackIntervals is not null ? FindBlackBridgedBreaks(blackIntervals) : new List<(double Start, double End)>();

        for (int i = 0; i <= gridSteps; i++)
        {
            double candidate = (double)i / gridSteps;
            var segments = BuildSegments(scores, intervalSeconds, candidate, adUnitSeconds, minBreakSeconds);
            if (blackIntervals is not null && silenceIntervals is not null)
            {
                segments = ValidateBreaksAgainstBumpers(segments, blackIntervals, silenceIntervals);
                segments = RefineBoundariesWithBlackAndSilence(segments, blackIntervals, silenceIntervals, maxNudgeSeconds);
                segments = MergeBlackBridgedBreaks(segments, bridgedBreaks);
            }
            double programTotal = segments.Where(s => !s.IsCommercial).Sum(s => s.DurationSeconds);
            evaluated.Add((candidate, Math.Abs(programTotal - targetProgramSeconds)));
        }

        var chosen = PickPlateauMedian(evaluated);
        var finalSegments = BuildSegments(scores, intervalSeconds, chosen, adUnitSeconds, minBreakSeconds);
        if (blackIntervals is not null && silenceIntervals is not null)
        {
            finalSegments = ValidateBreaksAgainstBumpers(finalSegments, blackIntervals, silenceIntervals);
            finalSegments = RefineBoundariesWithBlackAndSilence(finalSegments, blackIntervals, silenceIntervals, maxNudgeSeconds);
            finalSegments = MergeBlackBridgedBreaks(finalSegments, bridgedBreaks);
        }
        return (finalSegments, chosen);
    }

    // Picking the single candidate closest to the target is fragile when the objective is
    // non-monotonic (see FindThresholdForTarget): a single degenerate point (e.g. an almost
    // empty segmentation) can coincidentally land closer to the target number than any sensible
    // threshold does, purely by chance cancellation. Instead, find every candidate within a
    // tolerance band of the best score — the "plateau" of comparably-good answers — and return
    // their median. A real, stable answer should have other reasonable candidates near it that
    // perform almost as well; a lucky degenerate fluke usually doesn't.
    private static double PickPlateauMedian(List<(double Candidate, double Diff)> evaluated)
    {
        var bestDiff = evaluated.Min(e => e.Diff);
        var band = Math.Max(30.0, bestDiff * 1.5);
        var plateau = evaluated.Where(e => e.Diff <= bestDiff + band).Select(e => e.Candidate).OrderBy(c => c).ToList();
        return plateau[plateau.Count / 2];
    }

    // Same idea as FindThresholdForTarget, but scans the adaptive drop-threshold instead of an
    // absolute similarity cutoff. Direction is the same: a larger drop-threshold is looser (more
    // program kept), a smaller one is stricter — but see FindThresholdForTarget for why this is
    // a grid scan rather than a bisection.
    public static (List<Segment> Segments, double DropThreshold) FindAdaptiveThresholdForTarget(
        List<SampleScore> scores, double intervalSeconds, double targetProgramSeconds,
        double windowRadiusSeconds = 300.0, double adUnitSeconds = 15.0, double minBreakSeconds = 59.0,
        List<(double Start, double End)>? blackIntervals = null,
        List<(double Start, double End)>? silenceIntervals = null,
        double maxNudgeSeconds = 6.0,
        int gridSteps = 200)
    {
        const double maxDrop = 0.6;
        var baseline = ComputeLocalBaseline(scores, intervalSeconds, windowRadiusSeconds);
        var evaluated = new List<(double Candidate, double Diff)>(gridSteps + 1);

        // See FindThresholdForTarget's matching comment — without this, the search target never
        // reflects what the real pipeline (which folds in bridging afterward) actually produces.
        var bridgedBreaks = blackIntervals is not null ? FindBlackBridgedBreaks(blackIntervals) : new List<(double Start, double End)>();

        for (int i = 0; i <= gridSteps; i++)
        {
            double candidate = maxDrop * i / gridSteps;
            var segments = BuildSegmentsAdaptiveFromBaseline(scores, intervalSeconds, candidate, baseline, adUnitSeconds, minBreakSeconds);
            if (blackIntervals is not null && silenceIntervals is not null)
            {
                segments = ValidateBreaksAgainstBumpers(segments, blackIntervals, silenceIntervals);
                segments = RefineBoundariesWithBlackAndSilence(segments, blackIntervals, silenceIntervals, maxNudgeSeconds);
                segments = MergeBlackBridgedBreaks(segments, bridgedBreaks);
            }
            double programTotal = segments.Where(s => !s.IsCommercial).Sum(s => s.DurationSeconds);
            evaluated.Add((candidate, Math.Abs(programTotal - targetProgramSeconds)));
        }

        var chosen = PickPlateauMedian(evaluated);
        var finalSegments = BuildSegmentsAdaptiveFromBaseline(scores, intervalSeconds, chosen, baseline, adUnitSeconds, minBreakSeconds);
        if (blackIntervals is not null && silenceIntervals is not null)
        {
            finalSegments = ValidateBreaksAgainstBumpers(finalSegments, blackIntervals, silenceIntervals);
            finalSegments = RefineBoundariesWithBlackAndSilence(finalSegments, blackIntervals, silenceIntervals, maxNudgeSeconds);
            finalSegments = MergeBlackBridgedBreaks(finalSegments, bridgedBreaks);
        }
        return (finalSegments, chosen);
    }
}
