﻿using GitCommands;
using GitExtensions.Extensibility.Git;
using NSubstitute;

namespace GitCommandsTests.Git
{
    public sealed class GitRefTests
    {
        [Test]
        public void IsTrackingRemote_should_return_true_when_tracking_remote()
        {
            string remoteBranchShortName = "remote_branch";
            string remoteName = "origin";
            GitRef localBranchRef = SetupLocalBranchWithATrackingReference(remoteBranchShortName, remoteName);

            GitRef remoteBranchRef = SetupRemoteRef(remoteBranchShortName, remoteName);

            ClassicAssert.IsTrue(localBranchRef.IsTrackingRemote(remoteBranchRef));
        }

        [Test]
        public void IsTrackingRemote_should_return_false_when_remote_is_null()
        {
            GitRef localBranchRef = SetupLocalBranchWithATrackingReference("remote_branch", "origin");

            ClassicAssert.IsFalse(localBranchRef.IsTrackingRemote(null));
        }

        [Test]
        public void IsTrackingRemote_should_return_false_when_tracking_another_remote()
        {
            string remoteBranchShortName = "remote_branch";
            GitRef localBranchRef = SetupLocalBranchWithATrackingReference(remoteBranchShortName, "origin");

            GitRef remoteBranchRef = SetupRemoteRef(remoteBranchShortName, "upstream");

            ClassicAssert.IsFalse(localBranchRef.IsTrackingRemote(remoteBranchRef));
        }

        [Test]
        public void IsTrackingRemote_should_return_false_when_tracking_another_remote_branch()
        {
            GitRef localBranchRef = SetupLocalBranchWithATrackingReference("one_remote_branch", "origin");

            GitRef remoteBranchRef = SetupRemoteRef("another_remote_branch", "origin");

            ClassicAssert.IsFalse(localBranchRef.IsTrackingRemote(remoteBranchRef));
        }

        [Test]
        public void IsTrackingRemote_should_return_false_when_supposedly_local_branch_is_a_remote_ref()
        {
            GitRef localBranchRef = SetupRemoteRef("a_remote_branch", "origin");

            GitRef remoteBranchRef = SetupRemoteRef("a_remote_branch", "origin");

            ClassicAssert.IsFalse(localBranchRef.IsTrackingRemote(remoteBranchRef));
        }

        [Test]
        public void IsTrackingRemote_should_return_false_when_supposedly_remote_branch_is_a_local_ref()
        {
            GitRef localBranchRef = SetupLocalBranchWithATrackingReference("a_remote_branch", "origin");

            GitRef remoteBranchRef = SetupLocalBranchWithATrackingReference("a_remote_branch", "origin");

            ClassicAssert.IsFalse(localBranchRef.IsTrackingRemote(remoteBranchRef));
        }

        [Test]
        public void IsTrackingRemote_should_return_false_when_local_branch_is_tracking_nothing()
        {
            IGitModule localGitModule = Substitute.For<IGitModule>();
            localGitModule.GetEffectiveSetting($"branch.local_branch.merge").Returns(string.Empty);
            localGitModule.GetEffectiveSetting($"branch.local_branch.remote").Returns(string.Empty);
            GitRef localBranchRef = new(localGitModule, ObjectId.Random(), "refs/heads/local_branch");

            GitRef remoteBranchRef = SetupLocalBranchWithATrackingReference("a_remote_branch", "origin");

            ClassicAssert.IsFalse(localBranchRef.IsTrackingRemote(remoteBranchRef));
        }

        [Test]
        public void Remote_Should_prefix_LocalName_for_Name()
        {
            string remoteName = "origin";
            string name = "local_branch";
            string completeName = $"refs/remotes/{remoteName}/{name}";

            GitRef remoteBranchRef = SetupRawRemoteRef(name, remoteName, completeName);
            ClassicAssert.AreEqual(remoteBranchRef.LocalName, name);
        }

        [Test]
        public void If_Remote_is_not_prefix_of_Name_then_LocalName_should_return_Name()
        {
            // Not standard behavior but seem to occur for git-svn
            string remoteName = "Remote_longer_than_Name";
            string name = "a_short_name";
            string completeName = $"refs/remotes/{name}";

            GitRef remoteBranchRef = SetupRawRemoteRef(name, remoteName, completeName);
            ClassicAssert.AreEqual(remoteBranchRef.LocalName, name);
        }

        private static GitRef SetupRawRemoteRef(string remoteBranchShortName, string remoteName, string completeName)
        {
            IGitModule localGitModule = Substitute.For<IGitModule>();
            localGitModule.GetEffectiveSetting($"branch.local_branch.merge").Returns(completeName);
            localGitModule.GetEffectiveSetting($"branch.local_branch.remote").Returns(remoteName);
            GitRef remoteBranchRef = new(localGitModule, ObjectId.Random(), completeName, remoteName);
            return remoteBranchRef;
        }

        private static GitRef SetupRemoteRef(string remoteBranchShortName, string remoteName)
        {
            IGitModule remoteGitModule = Substitute.For<IGitModule>();
            GitRef remoteBranchRef = new(remoteGitModule, ObjectId.Random(), $"refs/remotes/{remoteName}/{remoteBranchShortName}", remoteName);
            return remoteBranchRef;
        }

        private static GitRef SetupLocalBranchWithATrackingReference(string remoteShortName, string remoteName)
        {
            IGitModule localGitModule = Substitute.For<IGitModule>();
            localGitModule.GetEffectiveSetting($"branch.local_branch.merge").Returns($"refs/heads/{remoteShortName}");
            localGitModule.GetEffectiveSetting($"branch.local_branch.remote").Returns(remoteName);
            GitRef localBranchRef = new(localGitModule, ObjectId.Random(), "refs/heads/local_branch");
            return localBranchRef;
        }
    }
}
