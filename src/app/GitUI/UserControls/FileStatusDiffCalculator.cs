﻿using GitCommands;
using GitCommands.Git;
using GitExtensions.Extensibility.Git;
using GitUI.Properties;
using GitUIPluginInterfaces;

namespace GitUI
{
    public sealed partial class FileStatusDiffCalculator
    {
        internal const string GitGrepIconName = nameof(GitGrepIconName);

        private readonly Func<IGitModule> _getModule;

        // Currently bound revisions etc. Cache so we can reload the view, if AppSettings.ShowDiffForAllParents is changed.
        private FileStatusDiffCalculatorInfo _fileStatusDiffCalculatorInfo = new();
        private const string _grepSummaryPrefix = "grep: ";

        public FileStatusDiffCalculator(Func<IGitModule> getModule)
        {
            _getModule = getModule;
        }

        // Default helper functions, can be set
        public Func<ObjectId, string>? DescribeRevision { get; set; }
        public Func<GitRevision, GitRevision>? GetActualRevision { get; set; }

        public static bool IsGrepItemStatuses(FileStatusWithDescription itemStatuses)
            => itemStatuses.Summary.StartsWith(_grepSummaryPrefix);

        public void SetDiff(
            IReadOnlyList<GitRevision> revisions,
            ObjectId? headId,
            bool allowMultiDiff)
        {
            _fileStatusDiffCalculatorInfo.Revisions = revisions;
            _fileStatusDiffCalculatorInfo.HeadId = headId;
            _fileStatusDiffCalculatorInfo.AllowMultiDiff = allowMultiDiff;
        }

        public void SetGrep(string grepArguments, bool fileTreeMode)
        {
            _fileStatusDiffCalculatorInfo.GrepArguments = grepArguments;
            _fileStatusDiffCalculatorInfo.FileTreeMode = fileTreeMode;
        }

        public IReadOnlyList<FileStatusWithDescription> Calculate(IReadOnlyList<FileStatusWithDescription> prevList, bool refreshDiff, bool refreshGrep, CancellationToken cancellationToken)
        {
            if (_fileStatusDiffCalculatorInfo.Revisions?.Count is not > 0
                || _fileStatusDiffCalculatorInfo.Revisions[0] is not GitRevision selectedRev)
            {
                return Array.Empty<FileStatusWithDescription>();
            }

            List<FileStatusWithDescription> fileStatusDescs = refreshDiff
                ? CalculateDiffs(_fileStatusDiffCalculatorInfo.Revisions, selectedRev,
                    _fileStatusDiffCalculatorInfo.HeadId, _fileStatusDiffCalculatorInfo.AllowMultiDiff, cancellationToken)
                : prevList.Where(p => !IsGrepItemStatuses(p)).ToList();

            FileStatusWithDescription? grepItemStatuses = refreshGrep
                ? GetGrepItemStatuses(selectedRev, cancellationToken)
                : prevList.FirstOrDefault(IsGrepItemStatuses);

            if (grepItemStatuses is not null)
            {
                fileStatusDescs.Add(grepItemStatuses);
            }

            return fileStatusDescs;
        }

        private List<FileStatusWithDescription> CalculateDiffs(
            IReadOnlyList<GitRevision> revisions,
            GitRevision selectedRev,
            ObjectId? headId,
            bool allowMultiDiff,
            CancellationToken cancellationToken)
        {
            IGitModule module = GetModule();
            List<FileStatusWithDescription> fileStatusDescs = [];
            if (revisions.Count == 1)
            {
                // If the grid is filtered, parents may be rewritten
                GitRevision actualRev = GetActualRevisionForRevision(selectedRev);

                if (actualRev.ParentIds is null || actualRev.ParentIds.Count == 0)
                {
                    fileStatusDescs.Add(new FileStatusWithDescription(
                        firstRev: null,
                        secondRev: selectedRev,
                        summary: GetDescriptionForRevision(selectedRev.ObjectId),
                        statuses: selectedRev.TreeGuid is null

                            // likely index commit without HEAD
                            ? module.GetDiffFilesWithSubmodulesStatus(firstId: null, selectedRev.ObjectId, parentToSecond: null, cancellationToken)

                            // No parent for the initial commit
                            : module.GetTreeFiles(selectedRev.TreeGuid, full: true)));
                }
                else
                {
                    // Get the parents for the selected revision
                    // Exclude the optional third group with the diff to the orphan commit containing the untracked files of a stash
                    int multipleParents = actualRev.ParentIds is null ? 0 : AppSettings.ShowDiffForAllParents ? actualRev.ParentIds.Count : 1;
                    fileStatusDescs.AddRange(actualRev
                        .ParentIds
                        .Take(multipleParents)
                        .Where(parentId => !(multipleParents == 3 && DescribeRevision?.Invoke(parentId).Contains(": untracked files on ") is true))
                        .Select(parentId =>
                            new FileStatusWithDescription(
                                firstRev: new GitRevision(parentId),
                                secondRev: selectedRev,
                                summary: TranslatedStrings.DiffWithParent + GetDescriptionForRevision(parentId),
                                statuses: module.GetDiffFilesWithSubmodulesStatus(parentId, selectedRev.ObjectId, actualRev.ParentIds[0], cancellationToken))));
                }

                // Show combined (merge conflicts) when a single merge commit is selected
                bool isMergeCommit = (selectedRev.ParentIds?.Count ?? 0) > 1;
                if (isMergeCommit && AppSettings.ShowDiffForAllParents)
                {
                    IReadOnlyList<GitItemStatus> conflicts = module.GetCombinedDiffFileList(selectedRev.ObjectId);
                    if (conflicts.Count != 0)
                    {
                        // Create an artificial commit
                        fileStatusDescs.Add(new FileStatusWithDescription(
                            firstRev: new GitRevision(ObjectId.CombinedDiffId), secondRev: selectedRev, summary: TranslatedStrings.CombinedDiff, statuses: conflicts, iconName: nameof(Images.DiffC)));
                    }
                }

                return fileStatusDescs;
            }

            // With more than 4, only first -> selected is interesting
            // Show multi compare if 2-4 are selected
            const int maxMultiCompare = 4;

            // With 4 selected, assume that ranges are selected: baseA..headA baseB..headB
            // the first item is therefore the second selected
            GitRevision firstRev = AppSettings.ShowDiffForAllParents && revisions.Count == maxMultiCompare
                ? revisions[2]
                : revisions[^1];

            fileStatusDescs.Add(new FileStatusWithDescription(
                firstRev: firstRev,
                secondRev: selectedRev,
                summary: TranslatedStrings.DiffWithParent + GetDescriptionForRevision(firstRev.ObjectId),
                statuses: module.GetDiffFilesWithSubmodulesStatus(firstRev.ObjectId, selectedRev.ObjectId, selectedRev.FirstParentId, cancellationToken)));

            if (!AppSettings.ShowDiffForAllParents || revisions.Count > maxMultiCompare || !allowMultiDiff)
            {
                return fileStatusDescs;
            }

            // Get merge base commit, use HEAD for artificial
            ObjectId? firstRevHead = GetRevisionOrHead(firstRev, headId);
            ObjectId? selectedRevHead = GetRevisionOrHead(selectedRev, headId);
            ObjectId? baseRevId = null;
            if (revisions.Count != 3)
            {
                baseRevId = GetMergeBase(firstRevHead, selectedRevHead);
            }
            else
            {
                // Check whether the middle commit is base to the first/second
                // Allow commits earlier to the actual base commit
                ObjectId middleId = revisions[1].ObjectId;
                if (!middleId.IsArtificial
                    && GetMergeBase(firstRevHead, middleId) == middleId
                    && GetMergeBase(selectedRevHead, middleId) == middleId)
                {
                    baseRevId = middleId;
                }
            }

            // If four selected: check if two ranges are selected
            ObjectId? baseA = null;
            ObjectId? baseB = null;

            // Check for separate branches (note that artificial commits both have HEAD as BASE)
            if (baseRevId is not null)
            {
                // Two/Three: Check that the selections are in separate branches
                if (revisions.Count < 4)
                {
                    if (baseRevId == firstRevHead || baseRevId == selectedRevHead)
                    {
                        baseRevId = null;
                    }
                }

                // Four: Two ranges must be selected
                else
                {
                    baseA = GetMergeBase(GetRevisionOrHead(revisions[3], headId), firstRevHead);
                    if (baseA == revisions[3].ObjectId)
                    {
                        baseB = GetMergeBase(GetRevisionOrHead(revisions[1], headId), selectedRevHead);
                        if (baseB != revisions[1].ObjectId)
                        {
                            baseB = null;
                        }
                    }

                    if (baseB is null)
                    {
                        // baseA/baseB were not ranges, this is no merge base
                        baseRevId = null;
                        baseA = null;
                    }
                }
            }

            if (baseRevId is null)
            {
                // No variant of range diff, show multi diff
                fileStatusDescs.AddRange(
                    revisions
                        .Where(rev => rev != firstRev && rev != selectedRev)
                        .Select(rev => new FileStatusWithDescription(
                            firstRev: rev,
                            secondRev: selectedRev,
                            summary: TranslatedStrings.DiffWithParent + GetDescriptionForRevision(rev.ObjectId),
                            statuses: module.GetDiffFilesWithSubmodulesStatus(rev.ObjectId, selectedRev.ObjectId, selectedRev.FirstParentId, cancellationToken))));

                return fileStatusDescs;
            }

            IReadOnlyList<GitItemStatus> allAToB = fileStatusDescs[0].Statuses;
            IReadOnlyList<GitItemStatus> allBaseToB = module.GetDiffFilesWithSubmodulesStatus(baseRevId, selectedRev.ObjectId, selectedRev.FirstParentId, cancellationToken);
            IReadOnlyList<GitItemStatus> allBaseToA = module.GetDiffFilesWithSubmodulesStatus(baseRevId, firstRev.ObjectId, firstRev.FirstParentId, cancellationToken);

            GitItemStatusNameEqualityComparer comparer = new();
            GitItemStatus[] allAToBExceptExactRenameCopy = [.. allAToB.Where(i => !((i.IsRenamed || i.IsCopied) && i.RenameCopyPercentage == "100"))];
            GitItemStatus[] sameBaseToAandB = [.. allBaseToB.Intersect(allBaseToA, comparer).Except(allAToBExceptExactRenameCopy, comparer)];
            GitItemStatus[] onlyA = [.. allBaseToA.Except(allBaseToB, comparer)];
            GitItemStatus[] onlyB = [.. allBaseToB.Except(allBaseToA, comparer)];

            foreach (IReadOnlyList<GitItemStatus> l in new[] { allAToB, allBaseToB, allBaseToA })
            {
                foreach (GitItemStatus f in l)
                {
                    f.DiffStatus = GetDiffStatus(f, l == allAToB);
                }
            }

            DiffBranchStatus GetDiffStatus(GitItemStatus f, bool atoBDiff)
            {
                // Always show where the change is done
                // This means that if a file is added in A it is shown as removed in the A->B diff,
                // but marked with A
                return sameBaseToAandB.Any(i => comparer.Equals(i, f)) ? DiffBranchStatus.SameChange
                    : onlyA.Any(i => comparer.Equals(i, f)) ? DiffBranchStatus.OnlyAChange
                    : onlyB.Any(i => comparer.Equals(i, f)) ? DiffBranchStatus.OnlyBChange
                    : DiffBranchStatus.UnequalChange;
            }

            GitRevision revBase = new(baseRevId);
            fileStatusDescs.Add(new FileStatusWithDescription(
                firstRev: revBase,
                secondRev: selectedRev,
                summary: $"{TranslatedStrings.DiffBaseWith} B {GetDescriptionForRevision(selectedRev.ObjectId)}",
                statuses: allBaseToB,
                iconName: nameof(Images.DiffB)));
            fileStatusDescs.Add(new FileStatusWithDescription(
                firstRev: revBase,
                secondRev: firstRev,
                summary: $"{TranslatedStrings.DiffBaseWith} A {GetDescriptionForRevision(firstRev.ObjectId)}",
                statuses: allBaseToA,
                iconName: nameof(Images.DiffA)));

            if (!module.GitVersion.SupportRangeDiffTool)
            {
                return fileStatusDescs;
            }

            // Add rangeDiff as a FileStatus item (even with artificial commits)
            GitRevision first = firstRev.ObjectId == firstRevHead ? firstRev : new GitRevision(firstRevHead);
            GitRevision selected = selectedRev.ObjectId == selectedRevHead ? selectedRev : new GitRevision(selectedRevHead);
            (int? baseToFirstCount, int? baseToSecondCount) = module.GetCommitRangeDiffCount(first.ObjectId, selected.ObjectId);

            // first and selected has a common merge base and count must be available
            // Only a printout, so no Validates
            string desc = $"{TranslatedStrings.DiffRange} {baseToFirstCount ?? -1}↓ {baseToSecondCount ?? -1}↑ BASE {GetDescriptionForRevision(baseRevId)}";
            fileStatusDescs.Insert(index: 1, new FileStatusWithDescription(
                firstRev: firstRev,
                secondRev: selectedRev,
                summary: desc,
                statuses: [new GitItemStatus(name: desc) { IsRangeDiff = true }],
                iconName: nameof(Images.DiffR)));

            return fileStatusDescs;

            ObjectId? GetMergeBase(ObjectId? a, ObjectId? b)
            {
                if (a is null || b is null || a == b)
                {
                    return null;
                }

                return module.GetMergeBase(a, b);
            }

            static ObjectId GetRevisionOrHead(GitRevision rev, ObjectId headId)
                => rev.IsArtificial ? headId : rev.ObjectId;

            GitRevision GetActualRevisionForRevision(GitRevision revision)
                => GetActualRevision is not null ? GetActualRevision(revision) : revision;
        }

        private FileStatusWithDescription? GetGrepItemStatuses(GitRevision selectedRev, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(_fileStatusDiffCalculatorInfo.GrepArguments))
            {
                return null;
            }

            IGitModule module = GetModule();
            IReadOnlyList<GitItemStatus> statuses = module.GetGrepFilesStatus(selectedRev.ObjectId, _fileStatusDiffCalculatorInfo.GrepArguments, applyAppSettings: !_fileStatusDiffCalculatorInfo.FileTreeMode, cancellationToken);

            if (_fileStatusDiffCalculatorInfo.FileTreeMode)
            {
                List<GitItemStatus> statusesWithSubmodules = new(statuses);
                statuses = statusesWithSubmodules;
                foreach (string submodulePath in module.GetSubmodulesLocalPaths(recursive: false))
                {
                    statusesWithSubmodules.Add(new GitItemStatus(submodulePath) { IsSubmodule = true });
                }
            }

            return new FileStatusWithDescription(
                               firstRev: null,
                               secondRev: selectedRev,
                               summary: $"{_grepSummaryPrefix}{_fileStatusDiffCalculatorInfo.GrepArguments} {GetDescriptionForRevision(selectedRev.ObjectId)}",
                               statuses,
                               iconName: GitGrepIconName);
        }

        private string GetDescriptionForRevision(ObjectId objectId)
            => DescribeRevision is not null ? DescribeRevision(objectId) : objectId.ToShortString();

        private IGitModule GetModule()
            => _getModule() ?? throw new ArgumentException($"Require a valid instance of {nameof(IGitModule)}");
    }
}
