﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Xml;
using System.Xml.Linq;

// TODO: Leap second handling.
// TODO: SegmentTemplate without timeline.
// TODO: More complex manifests.
// TODO: More time sync methods.
// TODO: Validate segment references.

namespace Axinom.LiveStreamValidation
{
    public static class LiveStream
    {
        // We expect everything to be fast.
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

        private const string MpdNamespace = "urn:mpeg:dash:schema:mpd:2011";

        private static readonly XName PeriodName = XName.Get("Period", MpdNamespace);
        private static readonly XName AdaptationSetName = XName.Get("AdaptationSet", MpdNamespace);
        private static readonly XName RepresentationName = XName.Get("Representation", MpdNamespace);
        private static readonly XName SegmentTemplateName = XName.Get("SegmentTemplate", MpdNamespace);
        private static readonly XName SegmentTimelineName = XName.Get("SegmentTimeline", MpdNamespace);
        private static readonly XName SegmentName = XName.Get("S", MpdNamespace);
        private static readonly XName UtcTimingName = XName.Get("UTCTiming", MpdNamespace);

        /// <summary>
        /// Attempts to validate a live stream, provided its URL.
        /// </summary>
        /// <exception cref="NotSupportedException">
        /// Thrown if the live stream is of a type not supported by the validator.
        /// This may happen if it uses rarely used features or just due to lack of implementation support.
        /// </exception>
        public static void Validate(Uri manifestUrl, IFeedbackSink feedback)
        {
            if (manifestUrl == null)
                throw new ArgumentNullException(nameof(manifestUrl));

            if (feedback == null)
                throw new ArgumentNullException(nameof(feedback));

            using (var client = new HttpClient
            {
                Timeout = RequestTimeout
            })
            {
                feedback.Info($"Downloading manifest from {manifestUrl}");
                var manifestResponse = client.GetAsync(manifestUrl).GetAwaiter().GetResult();
                    ;
                manifestResponse.EnsureSuccessStatusCode();

                var manifestString = manifestResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                var timeSinceDownload = Stopwatch.StartNew();

                feedback.DownloadedManifest(manifestString);

                // Timestamp that has been adjusted by clock synchronization.
                // This should match what the packager does fairly well.
                // All timing logic in this codebase uses synchronized clocks.
                DateTimeOffset manifestDownloadTimestamp;

                feedback.Info("Parsing manifest.");

                // We first build an understanding of the content available, with easy cross-references and all that.
                // During this, we may already encounter some errors/warnings - this is fine, we want to scream all we can.
                var manifest = LoadManifest(manifestString, feedback);

                if (manifest.HttpIsoTimeSyncUrl == null)
                    throw new NotSupportedException("The live stream manifest must define a supported method for clock synchronization. This validator supports the following clock synchronization modes: http-iso.");

                var timeSyncResponse = client.GetStringAsync(manifest.HttpIsoTimeSyncUrl).GetAwaiter().GetResult();

                var timeSyncTimestamp = DateTimeOffset.Parse(timeSyncResponse, CultureInfo.InvariantCulture);
                feedback.Info($"Clock synchronized from time server. Current time is {timeSyncTimestamp.ToTimeStringAccurate()}.");

                // We subtract time since download, since our "now" for the manifest is a bit in the past already.
                manifestDownloadTimestamp = timeSyncTimestamp - timeSinceDownload.Elapsed;

                feedback.Info($"Manifest was downloaded at {manifestDownloadTimestamp.ToTimeStringAccurate()}. This timestamp will be used as 'now' when calculating ");

                var periodsList = string.Join(Environment.NewLine, manifest.Periods.Select(p => $"{p.Id ?? "unknown"} from {p.Start.ToTimeStringAccurate()} to {(p.End ?? manifestDownloadTimestamp).ToTimeStringAccurate()}."));
                feedback.Info($"Loaded manifest with {manifest.Periods.Count} periods:{Environment.NewLine}{periodsList}");

                // We test for common errors in manifests, one by one.
                RunSanityChecks(manifest);
                CheckTimelineCoverage(manifest, manifestDownloadTimestamp, feedback);
            }
        }

        private static void RunSanityChecks(Manifest manifest)
        {
            if (manifest.Periods.Count == 0)
                throw new NotSupportedException("There are 0 periods in the manifest.");

            if (manifest.Periods.Any(p => p.AdaptationSets.Count == 0))
                throw new NotSupportedException("There is a period with 0 adaptation sets in the manifest.");

            if (manifest.Periods.Any(p => p.AdaptationSets.Any(s => s.Representations.Count == 0)))
                throw new NotSupportedException("There is an adaptation set with 0 representations in the manifest.");
        }

        private static void CheckTimelineCoverage(Manifest manifest, DateTimeOffset now, IFeedbackSink feedback)
        {
            // The timeline within the playback window must be entirely covered by segments.
            // There can be some segements before and after the window boundaries (and overlapping)
            // but we do not accept gaps (either because of missing periods, gaps in timeline or whatever).
            //
            // Note that it is okay for segments to overlap - newer periods can "cut short" older periods.
            // We will only consider the portion of a segment that lies within its period when comparing.
            // This only comes up at period boundaries. Within a period, no overlap is acceptable.
            //
            // Period durations are always sequentially correct because we fixup the durations on load (as should players).

            var windowStart = now - manifest.PlaybackWindowLength;

            feedback.Info($"Playback window is from {windowStart.ToTimeStringAccurate()} to {now.ToTimeStringAccurate()} ({manifest.PlaybackWindowLength.ToStringAccurate()})");

            // First, check the period start/end itself, without bothering about segments.
            var firstPeriodStart = manifest.Periods.First().Start;
            if (firstPeriodStart > windowStart)
                feedback.InvalidContent($"There is a gap of {(firstPeriodStart - windowStart).TotalMilliseconds:F1} ms between the start of the playback window ({windowStart.ToTimeStringAccurate()}) and the start of the first period ({firstPeriodStart.ToTimeStringAccurate()}).");

            var lastPeriodEnd = manifest.Periods.Last().End;

            if (lastPeriodEnd != null) // Might be infinite duration (almost always is).
                if (lastPeriodEnd < now)
                    feedback.InvalidContent($"There is a gap of {(now - lastPeriodEnd.Value).TotalMilliseconds:F1} ms between the end of the last period ({lastPeriodEnd.Value.ToTimeStringAccurate()}) and the end of the playback window ({now.ToTimeStringAccurate()}).");

            // Okay now run through each period individually and ensure it is fully covered by segments.
            // It is OK if periods have segments that go outside period boundaries - we ignore them.
            // It is OK if periods have segments overlapping period boundaries - we clip them.
            // We will clip first period to playback window start but process last period until the end,
            // even over the playback window end if it defines segments in that region (defined data must be valid).
            foreach (var period in manifest.Periods)
            {
                // Each adaptation set has its own timeline. They must all have content for all of the timeline!
                foreach (var set in period.AdaptationSets)
                {
                    // The adaptation set may either define a shared timeline or individual timelines per representation.
                    if (set.SegmentTemplate != null)
                    {
                        var path = $"{period.Id}/{set.MimeType}";
                        feedback.Info($"Checking timeline of {path}.");

                        CheckTimelineCoverage(set.SegmentTemplate, windowStart, now, path, feedback);
                    }
                    else
                    {
                        foreach (var rep in set.Representations)
                        {
                            var path = $"{period.Id}/{set.MimeType}/{rep.Id}";
                            feedback.Info($"Checking timeline of {path}.");

                            CheckTimelineCoverage(rep.SegmentTemplate, windowStart, now, path, feedback);
                        }
                    }
                }
            }
        }

        private static void CheckTimelineCoverage(SegmentTemplate template, DateTimeOffset windowStart, DateTimeOffset now, string path, IFeedbackSink feedback)
        {
            var period = template.ResolveAdaptationSet().Period;
            var periodTimingString = $"The period lasts from {period.Start.ToTimeStringAccurate()} to {(period.End ?? now).ToTimeStringAccurate()}.";

            var contentExistsUpTo = period.Start;

            // Skip until playback window start. This may even mean skipping the entire period.
            if (windowStart > contentExistsUpTo)
            {
                feedback.Info("Skipping data that lies before the playback window start.");
                contentExistsUpTo = windowStart;
            }

            var ignoredPastSegments = 0;
            var ignoredFutureSegments = 0;

            foreach (var segment in template.Segments)
            {
                // If it is entirely in the past, skip it.
                if (segment.End <= contentExistsUpTo)
                {
                    ignoredPastSegments++;
                    continue;
                }

                // If it leaves a gap, scream.
                if (segment.Start > contentExistsUpTo)
                {
                    var gapLength = segment.Start - contentExistsUpTo;
                    var playbackWindowStartString = contentExistsUpTo == windowStart ? " This gap is at the start of the playback window." : "";

                    feedback.InvalidContent($"There is a gap of {gapLength.TotalMilliseconds:F1} ms from {contentExistsUpTo.ToTimeStringAccurate()} to {segment.Start.ToTimeStringAccurate()} in {path}. {periodTimingString}{playbackWindowStartString}");
                }

                // If the segment is entirely in the future, skip it.
                // This is expected if the period is cut short by the next period.
                if (period.End.HasValue && segment.Start > period.End)
                {
                    ignoredFutureSegments++;
                    continue;
                }

                // Mark as contentful time segment.
                contentExistsUpTo = segment.End;

                // If we overshot period end, clip back.
                if (period.End.HasValue && contentExistsUpTo > period.End)
                    contentExistsUpTo = period.End.Value;
            }

            if (period.End.HasValue)
            {
                // Make sure we covered the period until the end if we know its end.
                if (contentExistsUpTo != period.End.Value)
                {
                    var gapLength = period.End.Value - contentExistsUpTo;
                    feedback.InvalidContent($"There is a gap of {gapLength.TotalMilliseconds:F1} ms from {contentExistsUpTo.ToTimeStringAccurate()} to {period.End.Value.ToTimeStringAccurate()} in {path}. {periodTimingString} This gap is at the end of the period.");
                }
            }
            else
            {
                // Make sure we covered the period until playback window end if we do not know period end.
                if (contentExistsUpTo < now)
                {
                    var gapLength = now - contentExistsUpTo;
                    feedback.InvalidContent($"There is a gap of {gapLength.TotalMilliseconds:F1} ms from {contentExistsUpTo.ToTimeStringAccurate()} to {now.ToTimeStringAccurate()}. {periodTimingString} This gap is at the end of the period.");
                }
            }

            if (ignoredPastSegments != 0 || ignoredFutureSegments != 0)
                feedback.Info($"Ignored {ignoredPastSegments} segments that were too early and would never be played. Ignored {ignoredFutureSegments} segments that were too late and would never be played.");
        }

        private static Manifest LoadManifest(string manifestString, IFeedbackSink feedback)
        {
            var ns = new XmlNamespaceManager(new NameTable());
            ns.AddNamespace("mpd", MpdNamespace);

            var xml = XDocument.Load(new StringReader(manifestString));

            // Load <MPD>.

            var manifest = new Manifest
            {
                Document = xml,
                Namespaces = ns,

                AvailabilityStartTime = xml.Root.GetAttributeAsDateTimeOffset("availabilityStartTime"),
                PublishTime = xml.Root.GetAttributeAsDateTimeOffset("publishTime"),

                PlaybackWindowLength = xml.Root.GetAttributeAsTimeSpan("timeShiftBufferDepth"),
                ManifestRefreshInterval = xml.Root.GetAttributeAsTimeSpan("minimumUpdatePeriod")
            };

            if (xml.Root.Attribute("type")?.Value != "dynamic")
                throw new NotSupportedException("MPD@type must be 'dynamic'");

            foreach (var clockSyncElement in xml.Root.Elements(UtcTimingName))
            {
                if (clockSyncElement.Attribute("schemeIdUri").Value == "urn:mpeg:dash:utc:http-iso:2014")
                {
                    // Theoretically, there could be multiple URLs but we will just accept latest
                    // because having a single URL is the common case in practice.
                    if (manifest.HttpIsoTimeSyncUrl != null)
                        feedback.WillSkipSomeData("Multiple time sync URLs are present in the manifest. This validator will only use the last one listed.");

                    manifest.HttpIsoTimeSyncUrl = new Uri(clockSyncElement.Attribute("value").Value, UriKind.Absolute);
                }
            }

            // Load <Period>.

            foreach (var periodElement in xml.Root.Elements(PeriodName))
            {
                var period = new Period
                {
                    Element = periodElement,
                    Manifest = manifest,

                    Id = periodElement.Attribute("id")?.Value,

                    StartOffsetFromAst = periodElement.GetAttributeAsTimeSpan("start")
                };

                manifest.Periods.Add(period);
            }

            // Calculate period durations.
            for (var i = 0; i < manifest.Periods.Count; i++)
            {
                if (i == manifest.Periods.Count - 1)
                {
                    // Last period may have explicit duration in manifest. Otherwise infinite duration.
                    if (manifest.Periods[i].Element.Attribute("duration") != null)
                        manifest.Periods[i].Duration = manifest.Periods[i].Element.GetAttributeAsTimeSpan("duration");
                }
                else
                {
                    // If it is not the last, we always calculate (even if explicit duration is set).
                    manifest.Periods[i].Duration = manifest.Periods[i + 1].Start - manifest.Periods[i].Start;
                }
            }

            // Load <AdaptationSet>.

            foreach (var period in manifest.Periods)
            {
                foreach (var setElement in period.Element.Elements(AdaptationSetName))
                {
                    var set = new AdaptationSet
                    {
                        Element = setElement,
                        Period = period,

                        MimeType = setElement.Attribute("mimeType")?.Value,

                        AlignedSegments = setElement.Attribute("segmentAlignment")?.Value == "true"
                    };

                    var templateElement = setElement.Element(SegmentTemplateName);

                    if (templateElement != null)
                    {
                        set.SegmentTemplate = LoadSegmentTemplate(templateElement);
                        set.SegmentTemplate.AdaptationSet = set;
                    }

                    period.AdaptationSets.Add(set);
                }
            }

            // Load <Representation>.

            foreach (var set in manifest.Periods.SelectMany(p => p.AdaptationSets))
            {
                foreach (var repElement in set.Element.Elements(RepresentationName))
                {
                    var rep = new Representation
                    {
                        Element = repElement,
                        AdaptationSet = set,

                        Id = repElement.Attribute("id")?.Value
                    };

                    var templateElement = repElement.Element(SegmentTemplateName);

                    if (templateElement != null)
                    {
                        if (set.SegmentTemplate != null)
                        {
                            throw new NotSupportedException("This validator only supports validating manifests where SegmentTemplate is under AdaptationSet or Representation but not both together for the same Representation.");
                        }

                        rep.SegmentTemplate = LoadSegmentTemplate(templateElement);
                        rep.SegmentTemplate.Representation = rep;
                    }
                    else
                    {
                        if (set.SegmentTemplate == null)
                        {
                            throw new NotSupportedException("This validator requires a SegmentTemplate under one of the following: AdaptationSet or Representation.");
                        }
                    }

                    set.Representations.Add(rep);
                }
            }

            return manifest;
        }

        private static SegmentTemplate LoadSegmentTemplate(XElement element)
        {
            var template = new SegmentTemplate
            {
                Element = element,

                Timescale = element.GetAttributeAsInt64("timescale"),
                RawPresentationTimeOffset = element.GetAttributeAsInt64("presentationTimeOffset"),

                InitUrlTemplate = element.GetAttributeAsString("initialization"),
                SegmentUrlTemplate = element.GetAttributeAsString("media")
            };

            foreach (var segmentElement in element.Elements(SegmentTimelineName).Elements(SegmentName))
            {
                var repeat = 0L;

                if (segmentElement.Attribute("r") != null)
                    repeat = segmentElement.GetAttributeAsInt64("r");

                for (var i = 0; i < repeat + 1; i++)
                {
                    var segment = new TimelineSegment
                    {
                        Element = segmentElement,
                        SegmentTemplate = template,

                        RawDuration = segmentElement.GetAttributeAsInt64("d"),
                        RawStart = segmentElement.GetAttributeAsInt64("t")
                    };

                    segment.RawStart += segment.RawDuration * i;

                    template.Segments.Add(segment);
                }
            }

            return template;
        }

        private sealed class Manifest
        {
            public XDocument Document;
            public XmlNamespaceManager Namespaces;

            public DateTimeOffset AvailabilityStartTime;
            public DateTimeOffset PublishTime;

            public TimeSpan PlaybackWindowLength;
            public TimeSpan ManifestRefreshInterval;

            // If using http-iso method.
            public Uri HttpIsoTimeSyncUrl;

            public IList<Period> Periods = new List<Period>();
        }

        private sealed class Period
        {
            public XElement Element;

            public Manifest Manifest;

            // May be null.
            public string Id;

            // Period starts this much time after the AST.
            public TimeSpan StartOffsetFromAst;

            // May be null if this is last period with no fixed duration.
            // If last period and has fixed duration, will be used as value.
            // Will be calculated based on next period start otherwise (ignoring any explicit value).
            public TimeSpan? Duration;

            public DateTimeOffset Start => Manifest.AvailabilityStartTime + StartOffsetFromAst;
            public DateTimeOffset? End => Duration == null ? null : Start + Duration;

            public IList<AdaptationSet> AdaptationSets = new List<AdaptationSet>();
        }

        private sealed class AdaptationSet
        {
            public XElement Element;
            public Period Period;

            public bool AlignedSegments;

            // May be null.
            public string MimeType;

            // Null if each representation has its own segment template (rare but it happens).
            public SegmentTemplate SegmentTemplate;

            public IList<Representation> Representations = new List<Representation>();
        }

        private sealed class Representation
        {
            public XElement Element;
            public AdaptationSet AdaptationSet;

            // May be null.
            public string Id;

            // Null if using adaptation set's segment template (shared by all representations).
            public SegmentTemplate SegmentTemplate;
        }

        // May be owned either by AdaptationSet or Representation, depending on scoping.
        private sealed class SegmentTemplate
        {
            public XElement Element;

            // Null if owned by representation.
            public AdaptationSet AdaptationSet;

            // Null if owned by adaptation set.
            public Representation Representation;

            // Never null.
            public AdaptationSet ResolveAdaptationSet() => AdaptationSet ?? Representation.AdaptationSet;

            public long Timescale;

            // Subtract this from timestamp of a segment in order to get period-relative timestamp.
            public long RawPresentationTimeOffset;

            public string InitUrlTemplate;
            public string SegmentUrlTemplate;

            public IList<TimelineSegment> Segments = new List<TimelineSegment>();
        }

        // If <SegmentTimeline> is used
        private sealed class TimelineSegment
        {
            public XElement Element;

            public SegmentTemplate SegmentTemplate;

            public long RawDuration;
            public long RawStart;

            public long RawEnd => RawStart + RawDuration;

            public TimeSpan Duration => TimeSpan.FromSeconds(1d * RawDuration / SegmentTemplate.Timescale);
            public TimeSpan StartOffsetFromPeriodStart => TimeSpan.FromSeconds(1d * (RawStart - SegmentTemplate.RawPresentationTimeOffset) / SegmentTemplate.Timescale);
            public TimeSpan EndOffsetFromPeriodStart => StartOffsetFromPeriodStart + Duration;

            public DateTimeOffset Start => SegmentTemplate.ResolveAdaptationSet().Period.Start + StartOffsetFromPeriodStart;
            public DateTimeOffset End => Start + Duration;
        }
    }
}
