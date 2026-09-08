// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Core.Processing;
using System;
using System.Globalization;
using System.Linq;
using System.Text;

namespace EdFi.Tools.ApiPublisher.Core.Metadata
{
    /// <summary>
    /// Renders a <see cref="RunSummary" /> as the table printed when a run ends, so that an operator can see
    /// what a run did to each resource without reading the log (see APIPUB-120).
    /// </summary>
    public static class RunSummaryFormatter
    {
        private const string UnknownItemCount = "unknown";

        public static string Format(RunSummary summary)
        {
            var rows = summary.Resources
                .Where(resource => resource.ExpectedItemCount.GetValueOrDefault() > 0
                    || resource.AttemptedItemCount > 0
                    || resource.FailedItemCount > 0
                    || resource.SkippedItemCount > 0)
                .ToArray();

            // A run that never got as far as attempting a document has nothing to account for, and an empty
            // table in the log is worse than no table at all
            if (rows.Length == 0 && summary.SourceReadErrorCount == 0 && summary.SkipReasons.Count == 0)
            {
                return string.Empty;
            }

            var message = new StringBuilder();
            message.AppendLine("Publishing run summary");

            int resourceColumnWidth = rows.Length == 0
                ? 8
                : Math.Min(60, rows.Max(row => row.ResourcePath.Length));

            message.AppendLine(FormatRow(
                "Stage",
                "Resource".PadRight(resourceColumnWidth),
                "Expected",
                "Attempted",
                "Failed",
                "Skipped",
                "Published*"));

            foreach (var row in rows)
            {
                message.AppendLine(FormatRow(
                    GetStageDisplayName(row.Stage),
                    row.ResourcePath.PadRight(resourceColumnWidth),
                    row.ExpectedItemCount.HasValue ? FormatCount(row.ExpectedItemCount.Value) : UnknownItemCount,
                    FormatCount(row.AttemptedItemCount),
                    FormatCount(row.FailedItemCount),
                    FormatCount(row.SkippedItemCount),
                    FormatCount(row.PublishedItemCount)));
            }

            message.AppendLine(FormatRow(
                "Total",
                new string(' ', resourceColumnWidth),
                rows.All(row => row.ExpectedItemCount.HasValue)
                    ? FormatCount(rows.Sum(row => row.ExpectedItemCount.Value))
                    : UnknownItemCount,
                FormatCount(rows.Sum(row => row.AttemptedItemCount)),
                FormatCount(rows.Sum(row => row.FailedItemCount)),
                FormatCount(rows.Sum(row => row.SkippedItemCount)),
                FormatCount(rows.Sum(row => row.PublishedItemCount))));

            message.AppendLine();
            message.AppendLine("  * Published is derived (attempted - failed - skipped), not counted on the target: the publishing pipeline reports errors, not successes. Attempted exceeds expected for a resource that is re-published by an authorization retry pass.");

            if (summary.SourceReadErrorCount > 0)
            {
                message.AppendLine(
                    $"  ! {FormatCount(summary.SourceReadErrorCount)} source read error(s) occurred, so an unknown number of documents was never attempted (the affected pages and counts are in the reported errors).");
            }

            if (summary.SkipReasons.Count > 0)
            {
                message.AppendLine($"  ! Documents were skipped: {string.Join("; ", summary.SkipReasons)}.");
            }

            return message.ToString().TrimEnd();
        }

        private static string FormatRow(
            string stage,
            string resource,
            string expected,
            string attempted,
            string failed,
            string skipped,
            string published)
        {
            return string.Concat(
                "  ",
                stage.PadRight(12),
                resource,
                expected.PadLeft(12),
                attempted.PadLeft(12),
                failed.PadLeft(10),
                skipped.PadLeft(10),
                published.PadLeft(12));
        }

        private static string FormatCount(long count) => count.ToString("N0", CultureInfo.InvariantCulture);

        private static string GetStageDisplayName(PublishingStage stage)
            => stage switch
            {
                PublishingStage.KeyChanges => "key changes",
                PublishingStage.Deletes => "deletes",
                _ => "upserts",
            };
    }
}
