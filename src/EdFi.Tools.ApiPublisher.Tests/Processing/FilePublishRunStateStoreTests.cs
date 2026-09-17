// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Processing;
using EdFi.Tools.ApiPublisher.Core.Processing.RunState;
using NUnit.Framework;
using Shouldly;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace EdFi.Tools.ApiPublisher.Tests.Processing
{
    /// <summary>
    /// The backing store a resume actually depends on (APIPUB-142): where the file lands, that what is
    /// written comes back, and that a file which cannot be read costs the run its resume rather than the run
    /// itself.
    /// </summary>
    [TestFixture]
    public class FilePublishRunStateStoreTests
    {
        private string _directory;

        [SetUp]
        public void CreateWorkingDirectory()
        {
            _directory = Path.Combine(Path.GetTempPath(), "apipub-142-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
        }

        [TearDown]
        public void RemoveWorkingDirectory()
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temporary directory is not worth failing a test over
            }
        }

        [Test]
        public async Task What_is_written_should_come_back()
        {
            var store = StoreAt(Path.Combine(_directory, "run.json"));

            var state = PublishRunState.StartNew(
                "TestSource",
                "TestTarget",
                new ChangeWindow { MinChangeVersion = 10, MaxChangeVersion = 20 });

            state.Resources.Add(
                new PublishRunResourceState
                {
                    ResourceUrl = "/ed-fi/students",
                    IsAuthorizationRetryPass = true,
                    Partitions =
                    {
                        new PublishRunPartitionState
                        {
                            PartitionIndex = 2,
                            StartingPageToken = "start",
                            LastCompletedPageToken = "confirmed",
                            LastCompletedPageNumber = 5,
                        },
                    },
                });

            await store.SaveAsync(state, CancellationToken.None);

            var loaded = await StoreAt(store.Location).TryLoadAsync(CancellationToken.None);

            loaded.RunId.ShouldBe(state.RunId);
            loaded.SourceConnectionName.ShouldBe("TestSource");
            loaded.TargetConnectionName.ShouldBe("TestTarget");
            loaded.MinChangeVersion.ShouldBe(10);
            loaded.MaxChangeVersion.ShouldBe(20);

            var partition = loaded.FindResource("/ed-fi/students", isAuthorizationRetryPass: true).Partitions[0];
            partition.PartitionIndex.ShouldBe(2);
            partition.StartingPageToken.ShouldBe("start");
            partition.LastCompletedPageToken.ShouldBe("confirmed");
            partition.LastCompletedPageNumber.ShouldBe(5);
        }

        [Test]
        public async Task Nothing_written_yet_should_read_as_nothing_to_resume()
        {
            var store = StoreAt(Path.Combine(_directory, "absent.json"));

            (await store.TryLoadAsync(CancellationToken.None)).ShouldBeNull();
        }

        [Test]
        public async Task A_file_that_cannot_be_read_should_cost_the_resume_and_not_the_run()
        {
            string path = Path.Combine(_directory, "truncated.json");

            // What a process killed mid-write would leave behind if the write were not atomic
            await File.WriteAllTextAsync(path, "{\"runId\":\"abc\",\"resources\":[");

            (await StoreAt(path).TryLoadAsync(CancellationToken.None)).ShouldBeNull();
        }

        [Test]
        public async Task Saving_should_not_leave_a_temporary_file_behind()
        {
            var store = StoreAt(Path.Combine(_directory, "run.json"));

            await store.SaveAsync(PublishRunState.StartNew("s", "t", changeWindow: null), CancellationToken.None);

            Directory.GetFiles(_directory).ShouldBe(new[] { store.Location });
        }

        [Test]
        public async Task Saving_twice_should_replace_what_is_stored()
        {
            var store = StoreAt(Path.Combine(_directory, "run.json"));

            var first = PublishRunState.StartNew("s", "t", changeWindow: null);
            await store.SaveAsync(first, CancellationToken.None);

            var second = PublishRunState.StartNew("s", "t", changeWindow: null);
            await store.SaveAsync(second, CancellationToken.None);

            (await store.TryLoadAsync(CancellationToken.None)).RunId.ShouldBe(second.RunId);
        }

        [Test]
        public async Task Deleting_should_leave_nothing_to_resume_and_should_not_mind_being_asked_twice()
        {
            var store = StoreAt(Path.Combine(_directory, "run.json"));

            await store.SaveAsync(PublishRunState.StartNew("s", "t", changeWindow: null), CancellationToken.None);
            await store.DeleteAsync(CancellationToken.None);

            (await store.TryLoadAsync(CancellationToken.None)).ShouldBeNull();

            // A run that never wrote any state still reaches the removal on its way out
            await store.DeleteAsync(CancellationToken.None);
        }

        [Test]
        public void A_directory_should_take_the_default_file_name_inside_it()
        {
            // What an operator gets by pointing the option at a mounted volume
            StoreAt(_directory).Location
                .ShouldBe(Path.Combine(_directory, FilePublishRunStateStore.BuildDefaultFileName("SourceOds", "TargetOds")));
        }

        [Test]
        public void A_path_that_names_a_file_should_be_used_as_it_stands()
        {
            string path = Path.Combine(_directory, "somewhere", "my-run.json");

            StoreAt(path).Location.ShouldBe(path);
        }

        [Test]
        public void No_configured_path_should_put_the_file_in_the_working_directory()
        {
            StoreAt(null).Location
                .ShouldBe(Path.Combine(Environment.CurrentDirectory, FilePublishRunStateStore.BuildDefaultFileName("SourceOds", "TargetOds")));
        }

        [Test]
        public async Task A_directory_that_does_not_exist_yet_should_be_created()
        {
            var store = StoreAt(Path.Combine(_directory, "nested", "deeper", "run.json"));

            await store.SaveAsync(PublishRunState.StartNew("s", "t", changeWindow: null), CancellationToken.None);

            File.Exists(store.Location).ShouldBeTrue();
        }

        /// <summary>
        /// Several publications share a working directory often enough that one file name for all of them
        /// means the last writer wins and the rest silently lose their resume.
        /// </summary>
        [Test]
        public void Two_publications_sharing_a_directory_should_get_their_own_state_files()
        {
            string first = StoreAt(_directory, "OdsA", "OdsB").Location;
            string second = StoreAt(_directory, "OdsC", "OdsD").Location;

            first.ShouldNotBe(second);
            Path.GetFileName(first).ShouldContain("OdsA");
            Path.GetFileName(first).ShouldContain("OdsB");
        }

        [Test]
        public void A_connection_name_that_is_not_a_legal_file_name_should_still_produce_one()
        {
            string location = StoreAt(_directory, "ods/a:b", "ods*c").Location;

            Path.GetFileName(location).IndexOfAny(Path.GetInvalidFileNameChars()).ShouldBe(-1);
        }

        [Test]
        public void Unnamed_connections_should_fall_back_to_the_bare_default_name()
        {
            // Such a run is refused a resume anyway, so there is nothing to tell apart
            FilePublishRunStateStore.BuildDefaultFileName(null, null)
                .ShouldBe(FilePublishRunStateStore.DefaultFileNamePrefix + ".json");
        }

        /// <summary>
        /// --lastChangeVersionProcessedNamespace exists so two publications can share a source and a target,
        /// so it has to reach the file name as well: without it they share one file and each destroys the
        /// other's resume.
        /// </summary>
        [Test]
        public void Two_namespaces_over_the_same_connections_should_get_their_own_state_files()
        {
            string first = StoreAt(_directory, changeVersionNamespace: "assessments").Location;
            string second = StoreAt(_directory, changeVersionNamespace: "enrollment").Location;

            first.ShouldNotBe(second);
            Path.GetFileName(first).ShouldContain("assessments");
            StoreAt(_directory).Location.ShouldNotBe(first);
        }

        [Test]
        public async Task State_that_records_no_resources_should_cost_the_resume_and_not_the_run()
        {
            string path = Path.Combine(_directory, "edited.json");

            // Valid JSON, matching identity, and a list the resume walks removed by hand
            await File.WriteAllTextAsync(path, "{\"runId\":\"abc\",\"resources\":null}");

            (await StoreAt(path).TryLoadAsync(CancellationToken.None)).ShouldBeNull();
        }

        [Test]
        public async Task State_with_half_a_change_window_should_cost_the_resume_and_not_the_run()
        {
            string path = Path.Combine(_directory, "half-window.json");

            // Taken as it stands this reads as no window at all, turning a resumed incremental publish into a
            // full one without a word
            await File.WriteAllTextAsync(path, "{\"runId\":\"abc\",\"minChangeVersion\":10,\"resources\":[]}");

            (await StoreAt(path).TryLoadAsync(CancellationToken.None)).ShouldBeNull();
        }

        [Test]
        public async Task State_with_a_partition_list_removed_should_cost_the_resume_and_not_the_run()
        {
            string path = Path.Combine(_directory, "no-partitions.json");

            await File.WriteAllTextAsync(
                path,
                "{\"runId\":\"abc\",\"resources\":[{\"resourceUrl\":\"/ed-fi/students\",\"partitions\":null}]}");

            (await StoreAt(path).TryLoadAsync(CancellationToken.None)).ShouldBeNull();
        }

        /// <summary>
        /// A partition this publisher wrote always carries at least its starting token, so one with neither
        /// token is an edited file. Refusing it here is what keeps a resume from falling back to asking the
        /// source to partition a resource it already holds recorded ranges for.
        /// </summary>
        [Test]
        public async Task State_with_a_partition_that_has_no_token_should_cost_the_resume_and_not_the_run()
        {
            string path = Path.Combine(_directory, "tokenless.json");

            await File.WriteAllTextAsync(
                path,
                "{\"runId\":\"abc\",\"resources\":[{\"resourceUrl\":\"/ed-fi/students\",\"partitions\":[{\"partitionIndex\":1}]}]}");

            (await StoreAt(path).TryLoadAsync(CancellationToken.None)).ShouldBeNull();
        }

        private static FilePublishRunStateStore StoreAt(
            string path,
            string sourceName = "SourceOds",
            string targetName = "TargetOds",
            string changeVersionNamespace = null)
            => new(
                new Options { RunStatePath = path, LastChangeVersionProcessedNamespace = changeVersionNamespace },
                new ApiConnectionDetailsStub { Name = sourceName },
                new ApiConnectionDetailsStub { Name = targetName });

        /// <summary>Carries a connection name and nothing else; the store reads no other property.</summary>
        private sealed class ApiConnectionDetailsStub : SourceConnectionDetailsBase, ITargetConnectionDetails
        {
        }
    }
}
