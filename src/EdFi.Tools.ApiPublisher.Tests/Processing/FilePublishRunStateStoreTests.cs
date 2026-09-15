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
                .ShouldBe(Path.Combine(_directory, FilePublishRunStateStore.DefaultFileName));
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
                .ShouldBe(Path.Combine(Environment.CurrentDirectory, FilePublishRunStateStore.DefaultFileName));
        }

        [Test]
        public async Task A_directory_that_does_not_exist_yet_should_be_created()
        {
            var store = StoreAt(Path.Combine(_directory, "nested", "deeper", "run.json"));

            await store.SaveAsync(PublishRunState.StartNew("s", "t", changeWindow: null), CancellationToken.None);

            File.Exists(store.Location).ShouldBeTrue();
        }

        private static FilePublishRunStateStore StoreAt(string path)
            => new(new Options { RunStatePath = path });
    }
}
