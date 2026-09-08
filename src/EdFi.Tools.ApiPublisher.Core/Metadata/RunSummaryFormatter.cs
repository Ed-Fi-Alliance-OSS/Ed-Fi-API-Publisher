// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Core.Processing;
using System;
using System.Collections.Generic;
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

        private const int MaxResourceColumnWidth = 60;

        private const string ColumnGutter = "  ";

        private static readonly string[] _headers =
        {
            "Stage", "Resource", "Expected", "Attempted", "Failed", "Skipped", "Published*",
        };

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

            var cells = new List<string[]> { _headers };

            cells.AddRange(
                rows.Select(
                    row => new[]
                    {
                        GetStageDisplayName(row.Stage),
                        Truncate(row.ResourcePath, MaxResourceColumnWidth),
                        row.ExpectedItemCount.HasValue ? FormatCount(row.ExpectedItemCount.Value) : UnknownItemCount,
                        FormatCount(row.AttemptedItemCount),
                        FormatCount(row.FailedItemCount),
                        FormatCount(row.SkippedItemCount),
                        FormatCount(row.PublishedItemCount),
                    }));

            cells.Add(
                new[]
                {
                    "Total",
                    string.Empty,
                    rows.All(row => row.ExpectedItemCount.HasValue)
                        ? FormatCount(rows.Sum(row => row.ExpectedItemCount.Value))
                        : UnknownItemCount,
                    FormatCount(rows.Sum(row => row.AttemptedItemCount)),
                    FormatCount(rows.Sum(row => row.FailedItemCount)),
                    FormatCount(rows.Sum(row => row.SkippedItemCount)),
                    FormatCount(rows.Sum(row => row.PublishedItemCount)),
                });

            // Every column is sized from its own widest value, so a long resource path or a document count in
            // the millions cannot push the following columns out of alignment
            int[] widths = Enumerable.Range(0, _headers.Length)
                .Select(column => cells.Max(row => row[column].Length))
                .ToArray();

            var message = new StringBuilder();
            message.AppendLine("Publishing run summary");

            foreach (var row in cells)
            {
                message.AppendLine(FormatRow(row, widths));
            }

            message.AppendLine();
            message.AppendLine("  * Published is not counted on the target. It is derived as attempted - failed - skipped, because the publishing pipeline reports errors, not successes.");
            message.AppendLine("  * A run that did not complete reports what it read, not what the target holds: documents abandoned when the run stopped are still counted as attempted.");

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

        private static string FormatRow(string[] row, int[] widths)
        {
            var line = new StringBuilder("  ");

            for (int column = 0; column < row.Length; column++)
            {
                if (column > 0)
                {
                    line.Append(ColumnGutter);
                }

                // The stage and the resource read as labels; the counts are compared down the column
                line.Append(
                    column <= 1
                        ? row[column].PadRight(widths[column])
                        : row[column].PadLeft(widths[column]));
            }

            return line.ToString().TrimEnd();
        }

        private static string Truncate(string resourcePath, int maximumLength)
        {
            return resourcePath.Length <= maximumLength
                ? resourcePath
                : "..." + resourcePath.Substring(resourcePath.Length - (maximumLength - 3));
        }

        private static string FormatCount(long count) => count.ToString("N0", CultureInfo.InvariantCulture);

        private static string GetStageDisplayName(PublishingStage stage)
            => stage switch
            {
                PublishingStage.Upserts => "upserts",
                PublishingStage.Deletes => "deletes",
                PublishingStage.KeyChanges => "key changes",
                _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "Unknown publishing stage."),
            };
    }
}
