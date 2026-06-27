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

    // The local-adaptive dip that, once corroboration and black-bridging are both active, needs
    // no per-video tuning — those two are what actually reject false positives, not the raw
    // threshold (confirmed by sweeping a wide range of dip values against hand-cut ground truth:
    // results were stable across roughly 0.05-0.10, degrading only once the dip got loose enough
    // to start missing real corroborated breaks). Shared so the GUI can use the exact same value
    // as its "no expected length, no manual dip" default, not just FindAdaptiveThresholdForTarget.
    public const double SafeDefaultDip = 0.05;

    // The local-baseline window radius used everywhere this matters. Used to be user-editable
    // (the rationale: a window narrower than your longest ad break lets that break drag its own
    // baseline down, hiding itself) — but black-bridge detection now catches long breaks
    // independently of the baseline entirely, so that concern is moot, and widening the window
    // past this has a real downside: it changes the baseline enough that a *short* break's raw
    // dip can stop crossing the detection floor on its own (confirmed in practice — a 400s radius
    // missed a 45s break that 300s caught, with bridging needed to cover the gap either way).
    // There's no longer a good reason to deviate from this value.
    public const double LocalWindowSeconds = 300.0;

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

    // Quick batch-compatibility check: given a handful of sample crops from a candidate
    // recording, returns the highest similarity seen against the reference logo. A genuine
    // match should show the bug clearly during ordinary program content at least some of the
    // time, so the *max* across a few samples (not the average) is the right statistic — it only
    // takes one frame catching the bug to confirm this crop region means something for this
    // file. A file with the wrong crop position, a different bug entirely, or no bug at all
    // will have every sample stuck at a low, noisy similarity instead.
    public static double EstimateMaxLogoSimilarity(string referenceImagePath, IEnumerable<string> sampleFramePaths)
    {
        using var reference = Image.Load<Rgba32>(referenceImagePath);
        reference.Mutate(x => x.Resize(64, 64));
        var mask = BuildLogoMask(reference);

        var best = 0.0;
        foreach (var path in sampleFramePaths)
        {
            using var frame = Image.Load<Rgba32>(path);
            frame.Mutate(x => x.Resize(64, 64));
            var similarity = PixelSimilarity(reference, frame, mask);
            if (similarity > best) best = similarity;
        }
        return best;
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
    //
    // But isolated black timing alone isn't sufficient either — a show with any dramatic content
    // (fast-cut fight scenes, flashbacks) produces plenty of isolated black flashes that have
    // nothing to do with commercials, and two of them can coincidentally land a plausible
    // ad-pod-length apart purely by chance. A real "bug stays visible" pod still has to look like
    // *something* in the logo signal — even if it never dips far enough to cross the configured
    // threshold, real ad content is noisier/lower than clean program (confirmed against the case
    // this function was built for: H&I's ad pods showed sustained, if shallow, dips throughout,
    // not flat high similarity). So: require a meaningful fraction of the candidate span to read
    // below baseline by at least a small margin — comfortably looser than the user's configured
    // threshold, just enough to rule out "this span is confidently, uniformly logo-present, i.e.
    // ordinary program" before trusting the black timing alone.
    public static List<(double Start, double End)> FindBlackBridgedBreaks(
        List<(double Start, double End)> blackIntervals,
        List<SampleScore>? scores = null, double[]? baseline = null,
        double minBreakSeconds = 25.0, double maxBreakSeconds = 300.0, double isolationToleranceSeconds = 20.0,
        double minAbsentFraction = 0.15, double softDropMargin = 0.05)
    {
        // A real bumper sometimes registers as a tight double-flash (two separate blackdetect
        // hits a couple seconds apart) rather than one continuous dip, and a real break can also
        // land right at the tail of a busy decoy cluster (e.g. immediately after the opening
        // titles' own flicker — confirmed happening in practice on two different episodes).
        // Left alone, both poison isolation — a nearby decoy looks like it's crowding the real
        // pairing, so the genuine break gets thrown out by the very check meant to protect it.
        // Merging anything within ChainGapToleranceSeconds (the same tolerance FindChainedDip
        // uses to decide what counts as "one cluster") into a single combined dip fixes that.
        //
        // This was tried once before with just the isolation check in place and reverted — it
        // let a stray candidate through (the merged cluster's *other* side ends up looking
        // spuriously isolated, since the whole cluster collapsed to one anchor). What makes it
        // safe now is the similarity check below: a phantom candidate spanning real, confidently-
        // classified program won't show meaningful absence, so it gets rejected on that basis
        // instead of relying on isolation distance alone to catch it.
        var sorted = MergeCloseIntervals(blackIntervals, ChainGapToleranceSeconds);

        // A dip can't anchor two accepted breaks (one bumper at the end of a real break can't
        // also be the start of a phantom second one a couple minutes later), but a single
        // left-to-right greedy scan picks whichever candidate comes first, not whichever is
        // actually better supported — and "first" can be a false positive. In one real case
        // (a busy cold-open/credits cluster merging into one anchor), the resulting bridge over
        // real program had *more* incidental similarity noise than the genuine break sharing its
        // boundary, just not enough to be flagged on its own. So: collect every candidate that
        // independently passes isolation/duration/absence, then resolve conflicts (candidates
        // that would reuse the same dip) by preferring the one with the strongest absence
        // evidence, not scan order.
        // A real pod's own internal transitions between spots are expected and shouldn't need to
        // be isolated from each other — only the *outer* edges (entering/leaving the whole pod)
        // need to look like a real bumper. Pairing strictly i with i+1 misses a pod whose own
        // internal cut happens to register as black too: that splits one real break into two
        // independently-plausible sub-candidates, and only one can win. So: also try pairing i
        // with i+2 and i+3, treating whatever's in between as internal pod structure rather than
        // requiring it to be isolated on its own.
        const int maxSpan = 3;
        var rawCandidates = new List<(int Lo, int Hi, double Start, double End, double AbsentFraction)>();
        for (int i = 0; i < sorted.Count - 1; i++)
        {
            var isolatedBefore = i == 0 || sorted[i].Start - sorted[i - 1].End > isolationToleranceSeconds;
            if (!isolatedBefore) continue;

            for (int j = i + 1; j <= Math.Min(i + maxSpan, sorted.Count - 1); j++)
            {
                var isolatedAfter = j == sorted.Count - 1 || sorted[j + 1].Start - sorted[j].End > isolationToleranceSeconds;
                if (!isolatedAfter) continue;

                var gapStart = sorted[i].End;
                var gapEnd = sorted[j].Start;
                var duration = gapEnd - gapStart;
                if (duration < minBreakSeconds || duration > maxBreakSeconds) continue;

                var absentFraction = scores is not null && baseline is not null
                    ? ComputeAbsentFraction(scores, baseline, gapStart, gapEnd, softDropMargin)
                    : 1.0;
                if (absentFraction < minAbsentFraction) continue;

                rawCandidates.Add((i, j, gapStart, gapEnd, absentFraction));
            }
        }

        // Conflicts are range overlaps now, not just shared endpoints — a wider candidate spans
        // every dip between its own endpoints, so it can't coexist with anything that touches one
        // of those interior dips either.
        //
        // Resolution order: absence fraction first, not span width. Span-width-first was tried —
        // it correctly recovers a pod split by its own internal transition (the wide candidate
        // spanning both halves wins), but it just as readily recovers a *false* wide candidate
        // that happens to span both a real short ad and the real program next to it, which is
        // exactly the cold-open/credits problem this resolution step exists to prevent in the
        // first place. There's no cheap way to tell "wide because it's one real pod" from "wide
        // because it accidentally bridges two unrelated things" from the aggregate fraction alone
        // — so this prefers the safer failure mode (a multi-transition pod's far side sometimes
        // stays uncaught) over the riskier one (real program getting swallowed).
        var claimed = new bool[sorted.Count];
        var candidates = new List<(double Start, double End)>();
        foreach (var c in rawCandidates.OrderByDescending(c => c.AbsentFraction))
        {
            var conflict = false;
            for (int k = c.Lo; k <= c.Hi && !conflict; k++)
                if (claimed[k]) conflict = true;
            if (conflict) continue;

            for (int k = c.Lo; k <= c.Hi; k++) claimed[k] = true;
            candidates.Add((c.Start, c.End));
        }
        candidates = candidates.OrderBy(c => c.Start).ToList();

        return candidates;
    }

    private static double ComputeAbsentFraction(
        List<SampleScore> scores, double[] baseline, double rangeStart, double rangeEnd, double softDropMargin)
    {
        int total = 0, absent = 0;
        foreach (var s in scores)
        {
            if (s.TimeSeconds < rangeStart || s.TimeSeconds > rangeEnd) continue;
            var idx = (int)s.TimeSeconds;
            if (idx >= baseline.Length) continue;
            total++;
            if (s.Similarity < baseline[idx] - softDropMargin) absent++;
        }
        return total > 0 ? (double)absent / total : 0.0;
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

    // Folds black-bridged candidates into a logo-detected segment list, stamping the bridge's
    // own boundaries over whatever was there before — including an existing commercial segment,
    // not just a program one. A bridge candidate is backed by clean, isolated black brackets on
    // both sides; a logo-path commercial nearby is only as good as wherever the threshold search
    // happened to land, which can clip a break short (e.g. the raw dip is brief even though the
    // real pod runs much longer once you look at where it's actually bracketed by black). Letting
    // the logo path's guess win by default — only filling in pure-program gaps — left exactly
    // that kind of short, wrong commercial in place instead of the bridge's more precise span.
    public static List<Segment> MergeBlackBridgedBreaks(List<Segment> segments, List<(double Start, double End)> bridgedBreaks)
    {
        var result = new List<Segment>(segments);

        foreach (var (bridgeStart, bridgeEnd) in bridgedBreaks)
        {
            var stamped = new List<Segment>();
            var inserted = false;

            foreach (var seg in result)
            {
                if (seg.EndSeconds <= bridgeStart || seg.StartSeconds >= bridgeEnd)
                {
                    stamped.Add(seg);
                    continue;
                }

                if (seg.StartSeconds < bridgeStart - 0.01)
                    stamped.Add(new Segment(seg.StartSeconds, bridgeStart, seg.IsCommercial));
                if (!inserted)
                {
                    stamped.Add(new Segment(bridgeStart, bridgeEnd, true));
                    inserted = true;
                }
                if (seg.EndSeconds > bridgeEnd + 0.01)
                    stamped.Add(new Segment(bridgeEnd, seg.EndSeconds, seg.IsCommercial));
            }

            result = stamped;
        }

        // A bridge candidate's boundary can land exactly where the logo path's own boundary
        // already was (e.g. one bracketing the break's exit, the other its entry), leaving two
        // adjacent same-type segments that are really one continuous break/program stretch.
        // Cosmetic only — cut accuracy doesn't depend on it — but worth tidying up before it
        // reaches the review grid.
        var merged = new List<Segment>();
        foreach (var seg in result)
        {
            if (merged.Count > 0 && merged[^1].IsCommercial == seg.IsCommercial)
                merged[^1] = new Segment(merged[^1].StartSeconds, seg.EndSeconds, seg.IsCommercial);
            else
                merged.Add(seg);
        }

        return merged;
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
        var bridgeBaseline = ComputeLocalBaseline(scores, intervalSeconds, 300.0);
        var bridgedBreaks = blackIntervals is not null ? FindBlackBridgedBreaks(blackIntervals, scores, bridgeBaseline) : new List<(double Start, double End)>();

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
    // threshold does, purely by chance cancellation. So instead: find the single best-scoring
    // point, then expand outward from it only while each next neighbor stays within a tolerance
    // band of that best score — the *contiguous* plateau of comparably-good answers around the
    // genuine optimum — and return its median.
    //
    // Critically, this must walk outward from the best point rather than collecting every
    // qualifying point anywhere in the grid. Once corroboration is in the loop, raw classification
    // tends to flatten out completely past some threshold (nothing left to detect, so every
    // higher candidate gives the exact same degenerate result) — that flat tail can span most of
    // the grid. If "plateau" meant "every point within the band," that tail's sheer size would
    // swamp the median even when a much better, narrower plateau exists elsewhere — which is
    // exactly what was happening before this was scoped to a contiguous walk.
    private static double PickPlateauMedian(List<(double Candidate, double Diff)> evaluated)
    {
        var bestIndex = 0;
        for (int i = 1; i < evaluated.Count; i++)
            if (evaluated[i].Diff < evaluated[bestIndex].Diff) bestIndex = i;

        var bestDiff = evaluated[bestIndex].Diff;
        var band = Math.Max(30.0, bestDiff * 1.5);

        int lo = bestIndex, hi = bestIndex;
        while (lo > 0 && evaluated[lo - 1].Diff <= bestDiff + band) lo--;
        while (hi < evaluated.Count - 1 && evaluated[hi + 1].Diff <= bestDiff + band) hi++;

        var plateau = evaluated.Skip(lo).Take(hi - lo + 1).Select(e => e.Candidate).OrderBy(c => c).ToList();
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
        var bridgedBreaks = blackIntervals is not null ? FindBlackBridgedBreaks(blackIntervals, scores, baseline) : new List<(double Start, double End)>();

        // Program-length matching is too coarse a signal once corroboration is doing the real
        // precision work: missing one short break barely moves the total, so the "best" length
        // match and a result that's missing a genuine break can come out nearly tied — and worse,
        // there's often no sharp step in between them for a plateau walk to stop at, so it can't
        // reliably tell the two apart (see PickPlateauMedian's history for the dead end that
        // motivated this). A small fixed dip already gets excellent results once corroboration
        // and bridging are active, since they're what actually reject false positives, not the
        // raw threshold. So: try that first, and only fall back to the full grid search if it
        // lands well outside the user's expected length — a sign this content doesn't fit the
        // usual pattern and the length target is the best signal available after all.
        if (blackIntervals is not null && silenceIntervals is not null)
        {
            var safeSegments = BuildSegmentsAdaptiveFromBaseline(scores, intervalSeconds, SafeDefaultDip, baseline, adUnitSeconds, minBreakSeconds);
            safeSegments = ValidateBreaksAgainstBumpers(safeSegments, blackIntervals, silenceIntervals);
            safeSegments = RefineBoundariesWithBlackAndSilence(safeSegments, blackIntervals, silenceIntervals, maxNudgeSeconds);
            safeSegments = MergeBlackBridgedBreaks(safeSegments, bridgedBreaks);
            var safeProgramTotal = safeSegments.Where(s => !s.IsCommercial).Sum(s => s.DurationSeconds);
            if (Math.Abs(safeProgramTotal - targetProgramSeconds) <= Math.Max(180.0, targetProgramSeconds * 0.1))
                return (safeSegments, SafeDefaultDip);
        }

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
