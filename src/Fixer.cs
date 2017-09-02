using System;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Svn2GitNet
{
    public class Fixer : Worker, IFixer
    {
        private MetaInfo _metaInfo;

        public Fixer(MetaInfo metaInfo,
                     Options options,
                     ICommandRunner commandRunner,
                     string gitConfigCommandArguments,
                     IMessageDisplayer messageDisplayer)
        : base(options, commandRunner, gitConfigCommandArguments, messageDisplayer)
        {
            _metaInfo = metaInfo;
        }

        public void FixBranches()
        {
            List<string> svnBranches = new List<string>();
            if (_metaInfo.RemoteBranches != null)
            {
                if (_metaInfo.Tags == null)
                {
                    svnBranches = _metaInfo.RemoteBranches.ToList();
                }
                else
                {
                    svnBranches = _metaInfo.RemoteBranches.Except(_metaInfo.Tags).ToList();
                }

                svnBranches.RemoveAll(b => !Regex.IsMatch(b.Trim(), @"^svn\/"));
            }

            if (_options.Rebase)
            {
                CommandInfo cmdInfo = CommandInfoBuilder.BuildGitSvnFetchCommandInfo();
                int exitCode = RunCommand(cmdInfo);
                if (exitCode != 0)
                {
                    throw new MigrateException(string.Format(ExceptionHelper.ExceptionMessage.FAIL_TO_EXECUTE_COMMAND, cmdInfo.ToString()));
                }
            }

            // In case of large branches, we build a hash set to boost the query later.
            HashSet<string> localBranchSet = new HashSet<string>(_metaInfo.LocalBranches);

            bool cannotSetupTrackingInformation = false;
            bool legacySvnBranchTrackingMessageDisplayed = false;

            foreach (var b in svnBranches)
            {
                var branch = Regex.Replace(b, @"^svn\/", "").Trim();
                bool isTrunkBranchOrIsLocalBranch = branch.Equals("trunk", StringComparison.InvariantCulture)
                                                    || localBranchSet.Contains(branch);
                if (_options.Rebase && isTrunkBranchOrIsLocalBranch)
                {
                    string localBranch = branch == "trunk" ? "master" : branch;

                    RunCommand(CommandInfoBuilder.BuildForceCheckoutLocalBranchCommandInfo(localBranch));
                    RunCommand(CommandInfoBuilder.BuildGitRebaseRemoteSvnBranchCommandInfo(branch));

                    continue;
                }

                if (isTrunkBranchOrIsLocalBranch)
                {
                    continue;
                }

                if (cannotSetupTrackingInformation)
                {
                    CommandInfo ci = CommandInfoBuilder.BuildCheckoutSvnRemoteBranchCommandInfo(branch);
                    _commandRunner.Run(ci.Command, ci.Arguments);
                }
                else
                {
                    string status = RunCommandIgnoreExitCode(CommandInfoBuilder.BuildGitBranchTrackCommandInfo(branch));

                    // As of git 1.8.3.2, tracking information cannot be set up for remote SVN branches:
                    // http://git.661346.n2.nabble.com/git-svn-Use-prefix-by-default-td7594288.html#a7597159
                    //
                    // Older versions of git can do it and it should be safe as long as remotes aren't pushed.
                    // Our --rebase option obviates the need for read-only tracked remotes, however.  So, we'll
                    // deprecate the old option, informing those relying on the old behavior that they should
                    // use the newer --rebase otion.
                    if (Regex.IsMatch(status, @"(?m)Cannot setup tracking information"))
                    {
                        cannotSetupTrackingInformation = true;
                        RunCommand(CommandInfoBuilder.BuildCheckoutSvnRemoteBranchCommandInfo(branch));
                    }
                    else
                    {
                        if (!legacySvnBranchTrackingMessageDisplayed)
                        {
                            ShowTrackingRemoteSvnBranchesDeprecatedWarning();
                        }

                        legacySvnBranchTrackingMessageDisplayed = true;

                        RunCommand(CommandInfoBuilder.BuildCheckoutLocalBranchCommandInfo(branch));
                    }
                }
            }
        }

        public void FixTags()
        {
            string currentUserName = string.Empty;
            string currentUserEmail = string.Empty;
            try
            {
                if (_metaInfo.Tags != null)
                {
                    Log("Reading user.name and user.email...");

                    Log($"Running command: git {_gitConfigCommandArguments} --get user.name");
                    _commandRunner.Run("git", $"{_gitConfigCommandArguments} --get user.name", out currentUserName);

                    Log($"Running command: git {_gitConfigCommandArguments} --get user.email");
                    _commandRunner.Run("git", $"{_gitConfigCommandArguments} --get user.email", out currentUserEmail);

                    Log($"user.name: {currentUserName}");
                    Log($"user.email: {currentUserEmail}");

                    foreach (string t in _metaInfo.Tags)
                    {
                        string tag = t.Trim();
                        Log($"Processing tag: {tag}");

                        string id = Regex.Replace(tag, @"^svn\/tags\/", "").Trim();
                        Log($"id: {id}");

                        string quotesFreeTag = Utils.EscapeQuotes(tag);
                        Log($"quotes free tag: {tag}");

                        string subject = Utils.RemoveFromTwoEnds(RunCommandIgnoreExitCode("git", $"log -1 --pretty=format:'%s' \"{quotesFreeTag}\""), '\'');
                        string date = Utils.RemoveFromTwoEnds(RunCommandIgnoreExitCode("git", $"log -1 --pretty=format:'%ci' \"{quotesFreeTag}\""), '\'');
                        string author = Utils.RemoveFromTwoEnds(RunCommandIgnoreExitCode("git", $"log -1 --pretty=format:'%an' \"{quotesFreeTag}\""), '\'');
                        string email = Utils.RemoveFromTwoEnds(RunCommandIgnoreExitCode("git", $"log -1 --pretty=format:'%ae' \"{quotesFreeTag}\""), '\'');

                        string quotesFreeAuthor = Utils.EscapeQuotes(author);
                        _commandRunner.Run("git", $"{_gitConfigCommandArguments} user.name \"{quotesFreeAuthor}\"");
                        _commandRunner.Run("git", $"{_gitConfigCommandArguments} user.email \"{quotesFreeAuthor}\"");

                        string originalGitCommitterDate = Environment.GetEnvironmentVariable("GIT_COMMITTER_DATE");
                        Environment.SetEnvironmentVariable("GIT_COMMITTER_DATE", Utils.EscapeQuotes(date));
                        _commandRunner.Run("git", $"tag -a -m \"{Utils.EscapeQuotes(subject)}\" \"{Utils.EscapeQuotes(id)}\" \"{quotesFreeTag}\"");
                        Environment.SetEnvironmentVariable("GIT_COMMITTER_DATE", originalGitCommitterDate);

                        _commandRunner.Run("git", $"branch -d -r \"{quotesFreeTag}\"");
                    }
                }
            }
            finally
            {
                // We only change the git config values if there are @tags available.
                // So it stands to reason we should revert them only in that case.
                if (_metaInfo.Tags != null && _metaInfo.Tags.Any())
                {
                    // If a line was read, then there was a config value so restore it.
                    // Otherwise unset the value because originally there was none.
                    if (!string.IsNullOrWhiteSpace(currentUserName))
                    {
                        _commandRunner.Run("git", $"{_gitConfigCommandArguments} user.name \"{currentUserName.Trim()}\"");
                    }
                    else
                    {
                        _commandRunner.Run("git", $"{_gitConfigCommandArguments} --unset user.name");
                    }

                    if (!string.IsNullOrWhiteSpace(currentUserEmail))
                    {
                        _commandRunner.Run("git", $"{_gitConfigCommandArguments} user.email \"{currentUserEmail.Trim()}\"");
                    }
                    else
                    {
                        _commandRunner.Run("git", $"{_gitConfigCommandArguments} --unset user.email");
                    }
                }
            }
        }

        public void FixTrunk()
        {
            if (_metaInfo.RemoteBranches != null)
            {
                string trunkBranch = _metaInfo.RemoteBranches.ToList().Find(b => b.Trim().Equals("trunk"));
                if (trunkBranch != null && !_options.Rebase)
                {
                    _commandRunner.Run("git", "checkout svn/trunk");
                    _commandRunner.Run("git", "branch -D master");
                    _commandRunner.Run("git", "checkout -f -b master");

                    return;
                }
            }

            _commandRunner.Run("git", "checkout -f master");
        }

        public void OptimizeRepos()
        {
            _commandRunner.Run("git", "gc");
        }

        private void ShowTrackingRemoteSvnBranchesDeprecatedWarning()
        {
            StringBuilder message = new StringBuilder();
            for (int i = 0; i < 68; ++i)
            {
                message.Append("*");
            }
            message.AppendLine();

            message.AppendLine("svn2git warning: Tracking remote SVN branches is deprecated.");
            message.AppendLine("In a future release local branches will be created without tracking.");
            message.AppendLine("If you must resync your branches, run: svn2git --rebase");

            for (int i = 0; i < 68; ++i)
            {
                message.Append("*");
            }

            ShowMessageIfPossible(message.ToString());
        }
    }
}